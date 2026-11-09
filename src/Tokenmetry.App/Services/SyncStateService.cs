using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Sync;

namespace Tokenmetry.App.Services;

/// <summary>
/// 跨设备同步的应用层装配（relay 设计 §4.6，relay 计划 Task 10）：开关即时生效无需重启，
/// 定时轮询 + 失败指数退避，改名/移除双写（本地存储 + 中继）。
/// <b>线程模型</b>：定时器回调在线程池，RunCoreAsync 以单飞行标志防重入；Changed 事件可能在
/// 任意线程触发，订阅方自行调度到 UI 线程。
/// <b>关闭语义（A12）</b>：停推停拉，本地已拉取的远端行保留；查询面强制本机（GlobalFilterMapper，
/// Task 11 接线）。再开启 = 从上次游标增量续拉。
/// </summary>
public sealed class SyncStateService : IDisposable
{
    private readonly IUsageStore _store;
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _settingsStore;
    private readonly Func<string, string, ISyncBackend> _backendFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timerPeriod;

    private Timer? _timer;
    private ISyncBackend? _backend;
    private int _syncing;
    private int _consecutiveFailures;
    private long _lastAttemptUtcTicks;

    /// <summary>设备清单/同步状态变化（每轮收尾、开关、改名）后触发；订阅方自行调度 UI 线程。</summary>
    public event Action? Changed;

    public SyncStateService(
        IUsageStore store,
        AppSettings settings,
        IAppSettingsStore settingsStore,
        Func<string, string, ISyncBackend>? backendFactory = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timerPeriod = null)
    {
        _store = store;
        _settings = settings;
        _settingsStore = settingsStore;
        // 默认工厂 = 真中继 HTTP 后端；测试注入 fake（EnableAsync 之后才调用，LocalDeviceId 已就绪）。
        _backendFactory = backendFactory ?? new Func<string, string, ISyncBackend>((endpoint, token) => new RelaySyncBackend(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, endpoint, token, LocalDeviceId));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _timerPeriod = timerPeriod ?? TimeSpan.FromSeconds(30);
        _lastAttemptUtcTicks = _clock().UtcTicks;
    }

    /// <summary>同步开关（= settings.SyncEnabled 投影）。</summary>
    public bool IsEnabled => _settings.SyncEnabled;

    /// <summary>本机设备 id（迁移自举，进程内稳定）。</summary>
    public string LocalDeviceId { get; private set; } = string.Empty;

    /// <summary>本机设备名（迁移时抓 MachineName 的初值，可在设置页改）。</summary>
    public string LocalDeviceName { get; private set; } = string.Empty;

    /// <summary>同步进行中标志（设置页状态文案用）。</summary>
    public bool IsSyncing => Volatile.Read(ref _syncing) == 1;

    /// <summary>最近一轮同步结果（null = 从未跑过或上一轮失败）。</summary>
    public SyncRunResult? LastRun { get; private set; }

    /// <summary>最近一轮同步的收尾时刻（无论成败；设置页「上次同步」文案用）。</summary>
    public DateTimeOffset LastRunAtUtc { get; private set; }

    /// <summary>最近一轮的失败原因（null = 上轮成功或从未跑过；设置页错误提示用）。</summary>
    public string? LastError { get; private set; }

    /// <summary>启动：装载本机设备 id/名字；开启中则建后端并触发首轮同步。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        LocalDeviceId = await _store.GetLocalDeviceIdAsync(ct);
        LocalDeviceName = await _store.GetLocalDeviceNameAsync(ct);

        if (_settings.SyncEnabled && !string.IsNullOrEmpty(_settings.SyncEndpoint))
        {
            BuildBackend(_settings.SyncEndpoint, _settings.SyncToken);
            StartTimer();
            _ = RunCoreAsync(ct); // 首轮后台跑，不阻塞启动序列
        }

        Changed?.Invoke();
    }

    /// <summary>开启同步：持久化配置、建后端、注册本机设备、立即跑一轮并启动定时器。</summary>
    public async Task EnableAsync(string endpoint, string token, CancellationToken ct = default)
    {
        _settings.SyncEnabled = true;
        _settings.SyncEndpoint = endpoint;
        _settings.SyncToken = token;
        await _settingsStore.SaveAsync(_settings, ct);

        BuildBackend(endpoint, token);
        _consecutiveFailures = 0;

        // 重置同步游标（终审 I5）：Enable 可能指向新中继/新账号（删库重建、轮换 token、切账号）——
        // 旧游标会让全量重推/重拉永不发生，新桶看不到历史。重置后首轮全量 push/pull，
        // 由两端 LWW 幂等消化（A14 值变化检测使重推天然只发有差异的行）。
        await _store.SetSyncCursorAsync(null, null, ct);

        // 注册本机设备（名字沿用本地注册表；A7：启用时刻抓一次，之后不随 hostname 漂移）。
        // 注册/换 token 是中继专属流程（A5）——fake 后端与社区实现无此成员时跳过。
        if (_backend is RelaySyncBackend relay)
        {
            await relay.RegisterDeviceAsync(LocalDeviceId, LocalDeviceName, ct);
        }

        StartTimer();
        await RunCoreAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>关闭同步（A12）：停定时器、停后端；远端行保留，再开启增量续拉。</summary>
    public async Task DisableAsync(CancellationToken ct = default)
    {
        _settings.SyncEnabled = false;
        await _settingsStore.SaveAsync(_settings, ct);

        StopTimer();
        _backend = null; // G3：关闭后零出站——唯一的出站句柄失效
        Changed?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>改名：本地注册表 + 中继（开启时）双写。</summary>
    public async Task RenameDeviceAsync(string name, CancellationToken ct = default)
    {
        await _store.SetLocalDeviceNameAsync(name, ct);
        LocalDeviceName = name;
        if (IsEnabled)
        {
            await _backend!.RenameDeviceAsync(LocalDeviceId, name, ct);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// 移除账号下的一台设备：中继解绑（级联清服务端行）+ 本地清其远端副本。
    /// 移除本机 = 注销自身并转未登录态（关同步——本机行是本地采集数据，不清）。
    /// </summary>
    public async Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        if (IsEnabled)
        {
            await _backend!.RemoveDeviceAsync(deviceId, ct);
        }

        if (deviceId == LocalDeviceId)
        {
            await DisableAsync(ct);
            return;
        }

        await _store.DeleteRemoteDeviceDataAsync(deviceId, ct);
        Changed?.Invoke();
    }

    /// <summary>读本账号排行榜偏好（透传后端；displayName = 生效名：自定义 ?? 默认）。同步未连接时抛 InvalidOperationException。</summary>
    public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
        RequireBackend().GetLeaderboardPreferenceAsync(ct);

    /// <summary>提交排行榜偏好（透传后端；返回服务端回显的生效名供 UI 回填）。同步未连接时抛 InvalidOperationException。</summary>
    public async Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default) =>
        await RequireBackend().SetLeaderboardPreferenceAsync(request, ct);

    /// <summary>排行榜偏好唯一事实源在中继（不落本地 settings.json）——无后端即无意义，直接失败。</summary>
    private ISyncBackend RequireBackend() =>
        _backend ?? throw new InvalidOperationException("Sync is not enabled.");

    /// <summary>立即同步（设置页按钮；未开启时无操作）。</summary>
    public async Task RunNowAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        await RunCoreAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// 账号设备清单（本机在首 + 远端）：设置页设备管理与顶栏设备下拉共用（单一真相源）。
    /// 本机 LastSeenAt 取 default（"本机"不参与最后活跃排序，UI 以名字标记本机）。
    /// </summary>
    public async Task<IReadOnlyList<SyncDeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<SyncDeviceInfo>();
        if (!string.IsNullOrEmpty(LocalDeviceId))
        {
            var name = string.IsNullOrWhiteSpace(LocalDeviceName) ? LocalDeviceId : LocalDeviceName;
            devices.Add(new SyncDeviceInfo(LocalDeviceId, name, default));
        }

        devices.AddRange(await _store.GetRemoteDevicesAsync(ct));
        return devices;
    }

    public void Dispose()
    {
        StopTimer();
        (_backend as IDisposable)?.Dispose();
    }

    /// <summary>定时器节流判定（测试经 TimerTickForTests 驱动；internal 供 App.Tests）。</summary>
    internal void TimerTickForTests() => OnTimerTick(null!);

    private void BuildBackend(string endpoint, string token)
    {
        (_backend as IDisposable)?.Dispose();
        _backend = _backendFactory(endpoint, token);
    }

    private void StartTimer()
    {
        StopTimer();
        _lastAttemptUtcTicks = _clock().UtcTicks;
        _timer = new Timer(OnTimerTick, null, _timerPeriod, _timerPeriod);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimerTick(object? state)
    {
        try
        {
            if (!IsEnabled || IsSyncing)
            {
                return;
            }

            var interval = _consecutiveFailures == 0
                ? TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes))
                : TimeSpan.FromMinutes(Math.Min(60, _consecutiveFailures * 2)); // 失败指数退避：次数 ×2 分钟，上限 60
            var lastAttempt = new DateTimeOffset(Volatile.Read(ref _lastAttemptUtcTicks), TimeSpan.Zero);
            if (_clock() - lastAttempt < interval)
            {
                return;
            }

            _ = RunCoreAsync();
        }
        catch
        {
            // 定时器回调不允许异常逃逸（未处理异常崩进程）；失败状态经 RunCoreAsync 记录并 Changed。
        }
    }

    /// <summary>单飞行的一轮同步：成败都记 LastRunAtUtc/LastError，失败计数驱动退避。</summary>
    private async Task<SyncRunResult?> RunCoreAsync(CancellationToken ct = default)
    {
        if (!IsEnabled || Volatile.Read(ref _syncing) == 1)
        {
            return null;
        }

        Volatile.Write(ref _syncing, 1);
        Volatile.Write(ref _lastAttemptUtcTicks, _clock().UtcTicks);
        Changed?.Invoke();
        try
        {
            var engine = new SyncEngine(_store, () => _backend);
            var result = await engine.RunOnceAsync(ct);
            LastRun = result;
            LastRunAtUtc = _clock();
            LastError = null;
            _consecutiveFailures = 0;
            return result;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            LastRunAtUtc = _clock();
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _syncing, 0);
            Changed?.Invoke();
        }
    }
}
