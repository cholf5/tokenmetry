using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.Infrastructure.ModelsDev;

/// <summary>
/// models.dev 价格拉取客户端（I-003，蓝本 pricing_refresh.rs 的移植）：
/// TTL 24 小时、后台刷新、失败静默沿用上一次成功数据。
/// <see cref="Current"/> 只读内存快照——查询路径（GetPrice 类调用）永远不阻塞、不触发网络；
/// 网络仅发生在显式调用 <see cref="RefreshIfDueAsync"/> / <see cref="RefreshAsync"/> 的后台路径
/// （由 I-005 的后台调度接入）。单飞：并发刷新只放行一个。
/// </summary>
public sealed class ModelsDevPricingClient : IRemotePricingSource, IDisposable
{
    public static readonly Uri DefaultApiUrl = new("https://models.dev/api.json");

    /// <summary>TTL 24h（蓝本 DEFAULT_PRICING_TTL_SECONDS = 86_400）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>单次拉取超时（蓝本 fetch_and_parse_pricing 的 20 秒）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private volatile RemotePricingSnapshot? _current;

    /// <summary>
    /// handler 可注入（测试用伪造 handler 模拟网络成功 / 失败 / 超时）；缺省时自建真实 HttpClient。
    /// </summary>
    public ModelsDevPricingClient(
        HttpMessageHandler? httpHandler = null,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        Uri? apiUrl = null)
    {
        if (ttl is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), value, "TTL must be positive.");
        }

        _httpClient = new HttpClient(httpHandler ?? new HttpClientHandler()) { Timeout = DefaultTimeout };
        _timeProvider = timeProvider ?? TimeProvider.System;
        Ttl = ttl ?? DefaultTtl;
        ApiUrl = apiUrl ?? DefaultApiUrl;
    }

    public Uri ApiUrl { get; }

    public TimeSpan Ttl { get; }

    /// <summary>最近一次成功拉取的价格表（只读内存引用）；尚未成功过为 null。</summary>
    public RemotePricingSnapshot? Current => _current;

    /// <summary>最近一次成功拉取时间；从未成功为 null。</summary>
    public DateTimeOffset? LastSuccessAt { get; private set; }

    /// <summary>最近一次尝试拉取时间（无论成败）；从未尝试为 null。</summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>最近一次失败的错误消息；成功后清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>TTL 是否已到期（从未成功过视为到期）。纯内存判断，供后台调度探测。</summary>
    public bool IsRefreshDue
    {
        get
        {
            var current = _current;
            return current is null || _timeProvider.GetUtcNow() - current.FetchedAt >= Ttl;
        }
    }

    /// <summary>
    /// TTL 未到期时是空操作（不发请求）；到期时刷新。失败静默（沿用旧数据、记录 LastError），永不抛出。
    /// 返回本次是否真正完成了刷新。
    /// </summary>
    public Task<bool> RefreshIfDueAsync(CancellationToken cancellationToken = default)
    {
        return IsRefreshDue ? RefreshAsync(cancellationToken) : Task.FromResult(false);
    }

    /// <summary>
    /// 立即刷新（忽略 TTL）。并发调用只放行一个（单飞）；失败静默，永不抛出，
    /// 成功前 <see cref="Current"/> 保持上一次成功数据不变。
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // 关机竞态：信号量已随 Dispose 释放。契约是"永不抛出"，静默放弃本次刷新。
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);
            LastAttemptAt = _timeProvider.GetUtcNow();

            using var response = await _httpClient
                .GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content
                .ReadAsStringAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            var entries = ModelsDevPricingParser.Parse(body);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("models.dev response contained no priced models.");
            }

            var fetchedAt = _timeProvider.GetUtcNow();
            _current = new RemotePricingSnapshot { FetchedAt = fetchedAt, Entries = entries };
            LastSuccessAt = fetchedAt;
            LastError = null;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = $"pricing refresh timed out after {DefaultTimeout.TotalSeconds:0}s";
            return false;
        }
        catch (Exception error)
        {
            // 失败静默：沿用上一次成功数据（蓝本 pricing_refresh.rs 的回退语义）。
            LastError = cancellationToken.IsCancellationRequested ? "pricing refresh cancelled" : error.Message;
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
        _httpClient.Dispose();
    }
}
