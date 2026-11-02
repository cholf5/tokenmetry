using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Update;

/// <summary>更新服务的状态机。检查与下载/落地共用一条主线：托盘、设置页、横幅读的都是它。</summary>
public enum UpdateState
{
    /// <summary>本进程还没检查过。</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>发现新版本（LastResult.LatestVersion / ReleaseUrl 可用）。</summary>
    Available,

    /// <summary>正在下载 / 校验 / 解包（进度见调用方传入的 IProgress）。</summary>
    Downloading,

    /// <summary>更新包已就绪，等用户点「立即重启并更新」。</summary>
    ReadyToRestart,

    /// <summary>正在替换文件并重启（随后本进程退出）。</summary>
    Restarting,

    Failed,
}

/// <summary>上一次更新在本进程启动时留下的结论。</summary>
public enum UpdateStartupOutcome
{
    /// <summary>没有待处理的更新记录。</summary>
    None,

    /// <summary>版本号对上了：更新成功（备份已清理）。</summary>
    Upgraded,

    /// <summary>记录里说更新到 vX，实际跑的还是旧版本——替换没生效，旧版本已由应用回滚，备份仍在。</summary>
    ApplyFailed,
}

/// <summary>下载成功、但目标目录不可写 → 已退回「下载 + 在文件管理器里显示」，路径在 <see cref="ArchivePath"/>。</summary>
public sealed class UpdateStageUnavailableException(string archivePath, Exception inner)
    : Exception(inner.Message, inner)
{
    public string ArchivePath { get; } = archivePath;
}

/// <summary>启动时对更新日志的解读结果，供界面弹「已更新到 vX」或「上次更新未生效」。</summary>
public sealed record UpdateStartupNotice(UpdateStartupOutcome Outcome, string? Version, string? BackupPath);

/// <summary>
/// 更新服务的进程级单例：设置页、托盘菜单与主窗横幅共享同一份状态（任一处发起，其余同步）。
/// 职责：检查（含启动期静默周检的节流与时间戳落盘）、下载 + SHA-256 校验 + 解包、落地并重启、
/// 启动时解读上次更新的结果（成功提示 / 失败回滚提示）。
/// <para>
/// 落地方式按形态分两类：Windows 安装副本交给 Inno 安装器（<see cref="UpdateInstaller"/>，
/// 它懂文件锁重试与半拷回滚）；便携版 / macOS / Linux 用 <see cref="UpdateStager"/> 做
/// 「同卷两次 rename」的原子替换，然后带着 <c>--wait-for-exit</c> 拉起新版本再退出。
/// </para>
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateChecker _checker;
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly UpdateDownloadService _downloader;
    private readonly UpdateInstaller _installer;
    private readonly UpdateStager _stager;
    private readonly UpdateJournal? _journal;
    private readonly string _processPath;
    private readonly Action<string> _openUrl;
    private readonly bool _windowsInstalledCopy;

    public UpdateService(UpdateChecker checker, AppSettings settings, IAppSettingsStore store,
        UpdateDownloadService? downloader = null, Action<string>? openUrl = null,
        UpdateInstaller? installer = null, bool? windowsInstalledCopy = null,
        UpdateStager? stager = null, UpdateJournal? journal = null, string? processPath = null)
    {
        _checker = checker;
        _settings = settings;
        _store = store;
        _downloader = downloader ?? new UpdateDownloadService();
        _installer = installer ?? new UpdateInstaller();
        _stager = stager ?? new UpdateStager();
        _journal = journal;
        _processPath = processPath ?? Environment.ProcessPath ?? string.Empty;
        // 升级方式在构造时定一次：进程的可执行文件位置整个生命周期不变，没必要每次下载重算；
        // 测试可注入（否则用例会跟着构建机是否恰好装在安装目录而飘）。
        _windowsInstalledCopy = windowsInstalledCopy ?? UpdateAssets.IsInstalledCopy();
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

        StartupNotice = ReconcileStartup();
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>本服务的比较基线（v 前缀、三段数字）——正常构造即程序集版本，测试可注入。</summary>
    public string CurrentVersion => _checker.Version;

    /// <summary>最近一次检查的原始结果（Idle 时 null；Available 时 LatestVersion/ReleaseUrl 可用）。</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>已就绪的更新（ReadyToRestart / Restarting 时非 null）。</summary>
    public PreparedUpdate? Prepared { get; private set; }

    /// <summary>最近一次失败的可展示原因（下载/校验/替换；检查失败走 <see cref="LastResult"/>）。</summary>
    public string? LastError { get; private set; }

    /// <summary>启动时对上次更新的解读：Upgraded → 界面提示「已更新到 vX」；ApplyFailed → 提示可恢复备份。</summary>
    public UpdateStartupNotice StartupNotice { get; }

    /// <summary>状态变化通知（可能在任意线程触发——订阅方自行 marshal 到 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>本进程是 Windows 安装目录里的副本（而不是解压出来的便携版）——升级走安装器静默升级。</summary>
    public bool CanSilentUpgrade => _windowsInstalledCopy;

    /// <summary>文件已替换、应该重启了。订阅方负责走正常生命周期退出（跳过则新旧实例抢互斥量）。</summary>
    public event Action? RestartRequested;

    /// <summary>发现新版本时浏览器打开的页面：优先这次检查结果的 URL，退回 Release 列表页。</summary>
    public void OpenReleasePage() => _openUrl(
        State == UpdateState.Available && LastResult?.ReleaseUrl is { } url
            ? url
            : UpdateAssets.ReleasesPageUrl);

    /// <summary>
    /// 下载当前已知新版本的平台资产 → SHA-256 校验 → 解包到目标同目录的暂存区。
    /// 成功后状态变 ReadyToRestart；失败抛给调用方展示，状态退回 Available（按钮仍可用）。
    /// </summary>
    public async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tag = AvailableTag() ?? throw new InvalidOperationException("no update available");
        var assetName = UpdateAssets.PickAssetName(tag, _windowsInstalledCopy);
        LastError = null;
        SetState(UpdateState.Downloading, LastResult);
        try
        {
            var path = await _downloader.DownloadVerifiedAsync(tag, assetName, progress, ct);
            Prepared = _windowsInstalledCopy
                ? _stager.PrepareInstaller(tag, path, _processPath)
                : PreparePortableOrReveal(tag, path);
            SetState(UpdateState.ReadyToRestart, LastResult);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Prepared = null;
            SetState(UpdateState.Available, LastResult);
            throw;
        }
    }

    /// <summary>
    /// 解包到目标同目录；目标目录不可写（/Applications 只读、企业策略、装在公司镜像里）时退回旧行为：
    /// 在文件管理器里显示已下载的包，让用户自己解压替换。自更新能力不该把「装不动」变成「更不了」。
    /// </summary>
    private PreparedUpdate PreparePortableOrReveal(string tag, string archivePath)
    {
        try
        {
            return _stager.PreparePortable(tag, archivePath, _processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _downloader.Reveal(archivePath);
            throw new UpdateStageUnavailableException(archivePath, ex);
        }
    }

    /// <summary>
    /// 落地并重启：写更新日志 → （安装器 / 原子替换）→ 拉起新版本 → 发 <see cref="RestartRequested"/>。
    /// 替换失败时保持 ReadyToRestart 并抛异常（旧版本仍在跑，用户可重试）。
    /// </summary>
    public void RestartAndApply()
    {
        if (Prepared is not { } prepared || State != UpdateState.ReadyToRestart)
        {
            throw new InvalidOperationException("no prepared update");
        }

        LastError = null;
        SetState(UpdateState.Restarting, LastResult);
        _journal?.Save(new UpdateJournalRecord
        {
            PendingVersion = prepared.Tag,
            BackupPath = prepared.BackupPath,
            TargetPath = prepared.TargetPath,
            StagedUtc = DateTime.UtcNow,
        });

        if (prepared.Mode == UpdateApplyMode.Installer)
        {
            try
            {
                if (!_installer.TryStartSilent(prepared.AssetPath))
                {
                    throw new InvalidOperationException("installer did not start");
                }
            }
            catch (Exception ex)
            {
                _journal?.Clear();
                LastError = ex.Message;
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw;
            }
        }
        else
        {
            try
            {
                _stager.Apply(prepared);
            }
            catch (Exception ex)
            {
                // 替换没成功：磁盘上还是旧版本，日志一并清掉，否则下次启动会误报「未生效」。
                _journal?.Clear();
                LastError = ex.Message;
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw;
            }

            if (!_stager.Relaunch(prepared))
            {
                // 文件已经是新的、只是没拉起来。保留日志：用户手动重启后版本号会对上，按成功处理。
                LastError = "updated files are in place but the new version did not start";
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw new InvalidOperationException(LastError);
            }
        }

        RestartRequested?.Invoke();
    }

    private string? AvailableTag() => LastResult?.LatestVersion;

    /// <summary>
    /// 立即检查一次。成功与失败都记 <c>LastUpdateCheckUtc</c>：周检节流的语义是「打过一次卡」，
    /// 打不通的网络每次启动都重试只是白费（下次检查照常手动/到点触发）。
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

    /// <summary>
    /// 解读上次更新留下的日志：版本对上 → 成功（清日志、删备份）；对不上 → 替换没生效，
    /// 保留备份路径给界面提示。没有日志 / 日志不可读 → 什么也没发生。
    /// </summary>
    private UpdateStartupNotice ReconcileStartup()
    {
        if (_journal?.Load() is not { PendingVersion: { } pending } record)
        {
            return new UpdateStartupNotice(UpdateStartupOutcome.None, null, null);
        }

        if (string.Equals(Normalize(pending), Normalize(CurrentVersion), StringComparison.OrdinalIgnoreCase))
        {
            _journal.Clear();
            DeleteBackup(record.BackupPath);
            return new UpdateStartupNotice(UpdateStartupOutcome.Upgraded, pending, null);
        }

        return new UpdateStartupNotice(UpdateStartupOutcome.ApplyFailed, pending, record.BackupPath);
    }

    private static string Normalize(string version) => version.TrimStart('v', 'V');

    private static void DeleteBackup(string? backupPath)
    {
        if (string.IsNullOrEmpty(backupPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
            else if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份删不掉只是占磁盘（同目录的 .old），下次更新会再试；不影响任何功能。
            Console.Error.WriteLine($"[Tokenmetry] 删除更新备份失败（{backupPath}）：{ex.Message}");
        }
    }

    private void SetState(UpdateState state, UpdateCheckResult? result)
    {
        State = state;
        LastResult = result;
        Changed?.Invoke();
    }
}
