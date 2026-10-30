using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Update;

/// <summary>更新服务的状态机（检查维度）。下载不进状态机——它只在设置页发起，进度即状态。</summary>
public enum UpdateState
{
    /// <summary>本进程还没检查过。</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>发现新版本（LastResult.LatestVersion / ReleaseUrl 可用）。</summary>
    Available,

    Failed,
}

/// <summary>
/// 更新检查的进程级单例：设置页与托盘菜单共享同一份状态（设置页查完，托盘菜单标题就地翻成
/// 「发现新版本 vX.Y.Z」，反之亦然，经 <see cref="Changed"/> 通知）。职责三件：检查（含启动期
/// 静默周检的判定与时间戳落盘）、打开 Release 页、下载更新包（转手 <see cref="UpdateDownloadService"/>）。
/// 不做自更新替换——见 <see cref="UpdateDownloadService"/> 注释。
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateChecker _checker;
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly UpdateDownloadService _downloader;
    private readonly Action<string> _openUrl;

    public UpdateService(UpdateChecker checker, AppSettings settings, IAppSettingsStore store,
        UpdateDownloadService? downloader = null, Action<string>? openUrl = null)
    {
        _checker = checker;
        _settings = settings;
        _store = store;
        _downloader = downloader ?? new UpdateDownloadService();
        // 默认经 shell 打 URL（跨平台 Process.Start + UseShellExecute）；测试注入记录器。
        _openUrl = openUrl ?? (url =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Tokenmetry] 打开 Release 页失败（{url}）：{ex.Message}");
            }
        });
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>本服务的比较基线（v 前缀、三段数字）——正常构造即程序集版本，测试可注入。
    /// 界面上的「当前版本」与 UpToDate 状态行必须读它而不是静态 <see cref="UpdateChecker.CurrentVersion"/>：
    /// 注入了基线的服务在状态行里显示程序集版本，自相矛盾。</summary>
    public string CurrentVersion => _checker.Version;

    /// <summary>最近一次检查的原始结果（Idle 时 null；Available 时 LatestVersion/ReleaseUrl 可用）。</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>状态变化通知（可能在任意线程触发——订阅方自行 marshal 到 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>发现新版本时浏览器打开的页面：优先这次检查结果的 URL，退回 Release 列表页。</summary>
    public void OpenReleasePage() => _openUrl(
        State == UpdateState.Available && LastResult?.ReleaseUrl is { } url
            ? url
            : UpdateAssets.ReleasesPageUrl);

    /// <summary>下载当前已知新版本的平台资产，返回落盘完整路径。仅 Available 状态可调（其余状态没有可信的 tag）。</summary>
    public Task<string> DownloadLatestAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tag = State == UpdateState.Available
            ? LastResult?.LatestVersion
            : null;
        return tag is null
            ? Task.FromException<string>(new InvalidOperationException("no update available"))
            : _downloader.DownloadAsync(tag, progress, ct);
    }

    /// <summary>下载完成后在文件管理器中显示（转手下载器，保持单一出口便于测试注入）。</summary>
    public void RevealDownloaded(string filePath) => _downloader.Reveal(filePath);

    /// <summary>
    /// 立即检查一次。成功与失败都记 <c>LastUpdateCheckUtc</c>：周检节流的语义是「打过一次卡」，
    /// 打不通的网络每次启动都重试只会白费（下次检查照常手动/到点触发）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct = default)
    {
        SetState(UpdateState.Checking, null);
        var result = await _checker.CheckAsync(ct);
        SetState(result.Outcome switch
        {
            UpdateCheckOutcome.UpdateAvailable => UpdateState.Available,
            UpdateCheckOutcome.UpToDate => UpdateState.UpToDate,
            _ => UpdateState.Failed,
        }, result);

        try
        {
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            await _store.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 更新检查时间戳落盘失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>距上次检查 ≥ minimumInterval（或从未检查过）才允许启动期静默检查；手动检查不受此门。</summary>
    public bool ShouldAutoCheck(TimeSpan minimumInterval) =>
        _settings.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= minimumInterval;

    private void SetState(UpdateState state, UpdateCheckResult? result)
    {
        State = state;
        LastResult = result;
        Changed?.Invoke();
    }
}
