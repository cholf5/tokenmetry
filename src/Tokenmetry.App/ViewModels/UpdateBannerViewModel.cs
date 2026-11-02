using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 主窗顶部的更新横幅：发现新版本 → 一键更新（带进度）→ 更新就绪 → 重启生效，
/// 以及重启后的「已更新到 vX」与「上次更新未生效」两条收尾提示。
///
/// 为什么放在主窗顶部而不是弹窗：应用是托盘常驻的，重要但不紧急的消息不该抢焦点；
/// 横幅常驻、可「稍后」，用户在处理完手头的事之后自然会看到。发现新版本时会话会自动唤回
/// 主窗（App 订阅 UpdateService.Changed），所以横幅不会被埋在一个隐藏窗口里。
/// </summary>
public partial class UpdateBannerViewModel : ViewModelBase
{
    private readonly UpdateService _service;
    private CancellationTokenSource? _downloadCancellation;

    /// <summary>用户按过「稍后」的版本 tag；换成另一个版本（或开始下载）时自动解除。</summary>
    private string? _dismissedTag;

    /// <summary>启动提示（已更新 / 上次未生效）只在本进程内展示一次。</summary>
    private bool _startupNoticeDismissed;

    /// <summary>最近一次操作失败的原因（下载校验 / 替换启动），渲染进横幅详情直到下一次操作。</summary>
    private string? _error;

    public UpdateBannerViewModel(UpdateService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        Sync();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _hasDetail;

    /// <summary>语义色（info / success / warning / danger）：视图按它切 Classes，颜色仍来自令牌。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToneSuccess), nameof(IsToneWarning), nameof(IsToneDanger))]
    private string _tone = "info";

    /// <summary>横幅左侧色条的语义态绑定（Classes.success / .warning / .danger；默认 info = 蓝）。</summary>
    public bool IsToneSuccess => Tone == "success";

    public bool IsToneWarning => Tone == "warning";

    public bool IsToneDanger => Tone == "danger";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressVisible;

    /// <summary>没有字节数可算的阶段（重启并应用）用不确定进度条。</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _primaryLabel = string.Empty;

    [ObservableProperty]
    private bool _isPrimaryVisible;

    [ObservableProperty]
    private string _secondaryLabel = string.Empty;

    [ObservableProperty]
    private bool _isSecondaryVisible;

    /// <summary>UI 线程 marshal 入口（同 SettingsViewModel 纪律）：生产走 Dispatcher，headless 测试注入内联。</summary>
    internal Action<Action> UiPost { get; set; } = PostToUiThread;

    private static void PostToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>主按钮：新版本就绪时是「立即更新」，已下载完成时是「立即重启并更新」。</summary>
    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (_service.State)
        {
            case UpdateState.Available:
                await DownloadAsync();
                break;
            case UpdateState.ReadyToRestart:
                RestartNow();
                break;
        }
    }

    private async Task DownloadAsync()
    {
        _dismissedTag = null;
        _error = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        // Progress<T> 在 UI 线程构造 → 回调被 marshal 回 UI 线程，属性更新不必自己 Post。
        var progress = new Progress<double>(percent => Progress = percent);
        try
        {
            await _service.DownloadAsync(progress, _downloadCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户自己取消：状态已退回 Available，横幅重新给出「立即更新」。
        }
        catch (UpdateStageUnavailableException ex)
        {
            // 目标目录不可写：包已下载并已在文件管理器中打开，横幅改说「请手动替换」。
            _error = string.Format(Translations.Instance.UpdateDownloaded, ex.ArchivePath);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            Sync();
        }
    }

    private void RestartNow()
    {
        _error = null;
        try
        {
            // 替换成功会发 RestartRequested，宿主随即退出；这里不需要再做别的。
            _service.RestartAndApply();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Sync();
        }
    }

    /// <summary>次按钮：下载中 = 取消；就绪 = 稍后；失败提示 = 打开备份位置；其余 = 收起。</summary>
    [RelayCommand]
    private void Secondary()
    {
        switch (_service.State)
        {
            case UpdateState.Downloading:
                _downloadCancellation?.Cancel();
                return;
            case UpdateState.ReadyToRestart:
            case UpdateState.Available:
                _dismissedTag = _service.LastResult?.LatestVersion;
                Sync();
                return;
        }

        if (_service.StartupNotice.Outcome == UpdateStartupOutcome.ApplyFailed)
        {
            DismissStartupNotice();
            if (_service.StartupNotice.BackupPath is { } backup)
            {
                var folder = System.IO.Path.GetDirectoryName(backup);
                if (!string.IsNullOrEmpty(folder))
                {
                    ShellLauncher.OpenFolder(folder);
                }
            }
            return;
        }

        DismissStartupNotice();
    }

    /// <summary>收起启动提示（「已更新到 vX」/「上次更新未生效」）。</summary>
    private void DismissStartupNotice()
    {
        _startupNoticeDismissed = true;
        Sync();
    }

    private void OnServiceChanged() => UiPost(Sync);

    /// <summary>按当前状态重算横幅的五个面：可见性 / 文案 / 进度 / 按钮 / 语义色。</summary>
    internal void Sync()
    {
        var state = _service.State;
        var tag = _service.LastResult?.LatestVersion;
        var notice = _service.StartupNotice;

        // 换了版本（或开始动手）就解除「稍后」的沉默。
        if (state is UpdateState.Downloading or UpdateState.Restarting
            || (tag is not null && tag != _dismissedTag))
        {
            _dismissedTag = null;
        }

        // 1) 启动收尾提示优先：它讲的是上一次更新，与本次检查结果无关。
        if (!_startupNoticeDismissed && notice.Outcome != UpdateStartupOutcome.None)
        {
            Show(
                title: notice.Outcome == UpdateStartupOutcome.Upgraded
                    ? string.Format(Translations.Instance.BannerUpgradedTitle, notice.Version)
                    : Translations.Instance.BannerApplyFailedTitle,
                detail: notice.Outcome == UpdateStartupOutcome.Upgraded
                    ? Translations.Instance.BannerUpgradedDetail
                    : string.Format(Translations.Instance.BannerApplyFailedDetail, notice.BackupPath),
                tone: notice.Outcome == UpdateStartupOutcome.Upgraded ? "success" : "warning",
                primary: null,
                secondary: notice.Outcome == UpdateStartupOutcome.Upgraded
                    ? Translations.Instance.ActionDismiss
                    : Translations.Instance.ActionRevealBackup);
            return;
        }

        switch (state)
        {
            case UpdateState.Downloading:
                Show(
                    title: string.Format(Translations.Instance.BannerDownloadingTitle, tag),
                    detail: null,
                    tone: "info",
                    primary: null,
                    secondary: Translations.Instance.ActionCancelDownload,
                    progress: Progress,
                    progressVisible: true);
                return;

            case UpdateState.ReadyToRestart:
                if (_dismissedTag is not null && _error is null)
                {
                    Hide();
                    return;
                }

                Show(
                    title: Translations.Instance.BannerReadyTitle,
                    detail: _error is null
                        ? string.Format(Translations.Instance.BannerReadyDetail, tag)
                        : string.Format(Translations.Instance.BannerFailedDetail, _error),
                    tone: _error is null ? "success" : "danger",
                    primary: Translations.Instance.ActionRestartNow,
                    secondary: Translations.Instance.ActionLater);
                return;

            case UpdateState.Restarting:
                Show(
                    title: Translations.Instance.BannerRestartingTitle,
                    detail: null,
                    tone: "info",
                    primary: null,
                    secondary: null,
                    progress: 0,
                    indeterminate: true);
                return;

            case UpdateState.Available when _dismissedTag is null:
                Show(
                    title: string.Format(Translations.Instance.BannerAvailableTitle, tag),
                    detail: _error is null
                        ? string.Format(Translations.Instance.BannerAvailableDetail, _service.CurrentVersion)
                        : string.Format(Translations.Instance.BannerFailedDetail, _error),
                    tone: _error is null ? "info" : "danger",
                    primary: Translations.Instance.ActionUpdateNow,
                    secondary: Translations.Instance.ActionLater);
                return;
        }

        Hide();
    }

    private void Show(string title, string? detail, string tone, string? primary, string? secondary,
        double progress = 0, bool indeterminate = false, bool progressVisible = false)
    {
        IsVisible = true;
        Title = title;
        Detail = detail ?? string.Empty;
        HasDetail = !string.IsNullOrEmpty(detail);
        Tone = tone;
        Progress = progress;
        IsProgressVisible = progressVisible || indeterminate;
        IsProgressIndeterminate = indeterminate;
        PrimaryLabel = primary ?? string.Empty;
        IsPrimaryVisible = primary is not null;
        SecondaryLabel = secondary ?? string.Empty;
        IsSecondaryVisible = secondary is not null;
    }

    private void Hide() => IsVisible = false;
}
