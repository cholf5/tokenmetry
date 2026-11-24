using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 项目行「打开」动作的共享实现（项目统计页排行 / 总览项目排行共用）：
/// 可打开路径判定、外部应用目录、图标提供者与 toast 全部收在这里，视图只按
/// <see cref="IProjectPathRow"/> 建菜单（共用菜单构建器在视图层），两个页面不再各持一套命令。
///
/// 线程 / I/O 纪律：路径判定（<see cref="CanOpen"/>）是纯字符串规则、加载期调用；
/// 目录存在性在点击时放线程池确认（网络盘 / 失效挂载的 stat 可能阻塞，不能卡 UI 线程）；
/// 启动失败经 toast 如实反馈，不静默吞掉。
///
/// 外部应用目录默认走 <see cref="ExternalAppCatalog.DetectOnce"/>（进程级懒探测）：
/// 首次开菜单才扫本机，总览首屏与页面重建都不承担探测成本。测试注入固定列表与假图标提供者。
/// </summary>
public sealed partial class ProjectRowActions
{
    private readonly IToastService? _toastService;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _openFolder;
    private readonly Func<string, bool> _openTerminal;
    private readonly Func<string, bool> _isOpenablePath;
    private readonly Func<IReadOnlyList<ExternalApp>> _externalAppsLoader;
    private IReadOnlyList<ExternalApp>? _externalApps;

    /// <summary>
    /// 生产入口只传 toast（<paramref name="toastService"/>）；其余参数是测试接缝——
    /// 目录存在性 / 两个启动器 / 路径门禁 / 外部应用目录 / 图标提取全部可注入，避免测试碰真实系统。
    /// </summary>
    public ProjectRowActions(
        IToastService? toastService = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? openFolder = null,
        Func<string, bool>? openTerminal = null,
        Func<string, bool>? isOpenablePath = null,
        Func<IReadOnlyList<ExternalApp>>? externalAppsLoader = null,
        IAppIconProvider? iconProvider = null)
    {
        _toastService = toastService;
        _directoryExists = directoryExists ?? (path => Directory.Exists(path));
        _openFolder = openFolder ?? ShellLauncher.TryOpenFolder;
        _openTerminal = openTerminal ?? TerminalLauncher.TryOpen;
        _isOpenablePath = isOpenablePath ?? ProjectPathRules.IsLocalAbsolutePath;
        _externalAppsLoader = externalAppsLoader ?? ExternalAppCatalog.DetectOnce;
        IconProvider = iconProvider ?? AppIconProvider.Shared;
    }

    /// <summary>本机探测到的编辑器 / Git 客户端（懒加载；默认进程级一次，测试注入固定列表）。</summary>
    public IReadOnlyList<ExternalApp> ExternalApps => _externalApps ??= _externalAppsLoader();

    /// <summary>外部应用图标（macOS 从已安装 .app 提取；测试注入假提供者）。</summary>
    public IAppIconProvider IconProvider { get; }

    /// <summary>键是否为当前平台的本地绝对路径（行级门禁，加载期调用，不做 I/O）。</summary>
    public bool CanOpen(string path) => _isOpenablePath(path);

    /// <summary>右键菜单「在文件管理器中打开」：绝对路径行才执行，点击时确认目录存在。</summary>
    [RelayCommand]
    private Task OpenFolderAsync(IProjectPathRow? row) =>
        OpenPathAsync(row, _openFolder, Translations.Instance.ToastProjectOpenFailed);

    /// <summary>右键菜单「在终端中打开」：同上，终端启动失败单独提示。</summary>
    [RelayCommand]
    private Task OpenTerminalAsync(IProjectPathRow? row) =>
        OpenPathAsync(row, _openTerminal, Translations.Instance.ToastTerminalUnavailable);

    /// <summary>右键菜单「在 {app} 中打开」：目录存在才启动，失败 toast（带应用名）。</summary>
    public async Task OpenInAppAsync(ExternalApp app, IProjectPathRow? row)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (row is not { CanOpen: true, RawPath: { Length: > 0 } path })
        {
            return;
        }

        if (!await DirectoryExistsAsync(path))
        {
            _toastService?.Show(Translations.Instance.ToastProjectPathMissing);
            return;
        }

        if (!app.TryOpen(path))
        {
            _toastService?.Show(string.Format(
                Translations.Instance.ToastExternalAppOpenFailedFormat, app.DisplayName));
        }
    }

    private async Task OpenPathAsync(
        IProjectPathRow? row,
        Func<string, bool> open,
        string failureMessage)
    {
        if (row is not { CanOpen: true, RawPath: { Length: > 0 } path })
        {
            return;
        }

        if (!await DirectoryExistsAsync(path))
        {
            _toastService?.Show(Translations.Instance.ToastProjectPathMissing);
            return;
        }

        if (!open(path))
        {
            _toastService?.Show(failureMessage);
        }
    }

    // 存在性检查放线程池：网络盘/失效挂载的 stat 可能阻塞，不能卡 UI 线程。
    private Task<bool> DirectoryExistsAsync(string path) => Task.Run(() => _directoryExists(path));
}
