using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Views;

public partial class MainWindow : Window
{
    /// <summary>用户关窗的分派动作（<see cref="ResolveUserCloseAction"/> 的结果）。</summary>
    internal enum UserCloseAction
    {
        /// <summary>隐藏到托盘（A-0 常驻语义，可从托盘再唤回）。</summary>
        MinimizeToTray,

        /// <summary>首次"关窗=托盘"：先弹一次性模态提示窗告知应用去了哪里、如何退出，关闭后照常隐藏。</summary>
        ShowFirstCloseHint,

        /// <summary>真退出：desktop.Shutdown() 生命周期收尾（与托盘「退出」同一通道）。</summary>
        Exit,
    }

    private readonly AppSettings? _settings;
    private readonly IAppSettingsStore? _settingsStore;

    /// <summary>当前打开的首关提示窗（S-005）。headless 测试宿主无桌面生命周期（ApplicationLifetime
    /// 为 null，无从枚举窗口），测试只能经此属性定位提示窗；正常运行无其他消费者，提示流结束即清空。</summary>
    internal Window? ActiveHintWindow { get; private set; }

    public MainWindow() : this(null, null)
    {
    }

    /// <summary>关窗行为依赖（App 启动注入 settings + 落盘 store；headless 测试 / 设计期用无参构造——
    /// 设置未注入时维持旧行为：隐藏、不提示、不退出）。</summary>
    internal MainWindow(AppSettings? settings, IAppSettingsStore? settingsStore)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;

        // 页面进场（motion design）：Content 替换即重播；只动 Opacity/RenderTransform，
        // 不做退出动画（固定信息架构没有方向语义）
        PageHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty && PageHost.Content is not null)
            {
                RunPageEntrance();
            }
        };
    }

    // ── 页面进场过渡（motion design §5）──

    /// <summary>时间线代数：Content 连续替换时新进场作废旧进场。</summary>
    private int _pageEntranceGeneration;

    /// <summary>
    /// 页面进场：淡入 + 轻上浮（克制取向：只有进场无退出）。TopLevel.RequestAnimationFrame
    /// 帧回调驱动（Avalonia 12 的 Animation 管线无法对 Transform 实例 RunAsync），180ms ease-out；
    /// 结束清 RenderTransform 不残留。无宿主 TopLevel（首屏绑定早于挂树）直接落终态。
    /// </summary>
    private void RunPageEntrance()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var generation = ++_pageEntranceGeneration;
        PageHost.Opacity = 0;
        PageHost.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(8px)");
        var start = (System.TimeSpan?)null;
        Action<System.TimeSpan> frame = null!;
        frame = timestamp =>
        {
            if (generation != _pageEntranceGeneration)
            {
                return; // 连续换页：新进场接管，旧时间线自杀
            }

            start ??= timestamp;
            var t = Math.Clamp((timestamp - start.Value).TotalMilliseconds / Tokenmetry.App.Styles.Motion.NormalMs, 0, 1);
            var eased = Tokenmetry.App.Styles.Motion.EaseOut.Ease(t);
            PageHost.Opacity = eased;
            PageHost.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse($"translateY({8 - 8 * eased:0.###}px)");
            if (t < 1)
            {
                topLevel.RequestAnimationFrame(frame);
            }
            else
            {
                PageHost.Opacity = 1;
                PageHost.RenderTransform = null;
            }
        };
        topLevel.RequestAnimationFrame(frame);
    }

    /// <summary>
    /// A-0 常驻体验（v0.5 规格）：用户关窗（点 X / Alt+F4 / 程序内 Close()）= 隐藏到托盘，
    /// 进程常驻；真正退出只走托盘菜单「退出」→ desktop.Shutdown()。生命周期驱动的关闭
    /// （托盘退出的 ApplicationShutdown、OS 会话结束的 OSShutdown）不在此列，天然放行。
    /// S-005：行为可由设置 MinimizeToTrayOnClose 关闭（关窗即退出，仍走生命周期收尾）；
    /// 首次"关窗=托盘"弹一次性模态提示窗——ToastSystemNotification 的 in-app toast 在主窗隐藏后
    /// 无处渲染，Avalonia 亦无跨平台系统通知 API（balloon 需 Win32 内部句柄、macOS 通知需 interop
    /// + 权限流程），模态窗是唯一零 interop 且"关窗时点告知"的方案。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.OwnerWindowClosing)
        {
            e.Cancel = true;
            HandleUserClose();
        }

        base.OnClosing(e);
    }

    private void HandleUserClose()
    {
        switch (ResolveUserCloseAction(_settings))
        {
            case UserCloseAction.Exit:
                ExitViaLifetime();
                break;
            case UserCloseAction.ShowFirstCloseHint:
                ShowFirstCloseHint();
                break;
            default:
                Hide();
                break;
        }
    }

    /// <summary>关窗决策（纯函数，headless 可测）：设置未注入 = 旧行为隐藏；开关关闭 = 退出；
    /// 开关开启且提示未展示过 = 先提示；其余 = 直接隐藏。</summary>
    internal static UserCloseAction ResolveUserCloseAction(AppSettings? settings) => settings switch
    {
        { MinimizeToTrayOnClose: false } => UserCloseAction.Exit,
        { CloseToTrayHintShown: false } => UserCloseAction.ShowFirstCloseHint,
        _ => UserCloseAction.MinimizeToTray,
    };

    private void ShowFirstCloseHint()
    {
        // 状态位内存先行 + best-effort 落盘：提示只出现这一次；落盘失败则下次启动会再提示一次（可接受）。
        // 与设置页自动保存并发写同一份共享 settings.json：两侧写的是同一 AppSettings 单例且保存为
        // 临时文件 + 原子 Move，最坏 last-writer-wins 且内容一致，无需加锁。
        _settings!.CloseToTrayHintShown = true;
        _ = SaveHintFlagAsync();

        // 提示窗模态挂主窗（关窗已被拦截，此刻主窗仍可见）：提示窗关闭后再隐藏主窗——提示期间
        // 应用始终有可见窗口，默认 ShutdownMode（OnLastWindowClose）不会把这次关窗误判成退出。
        var hint = BuildHintWindow();
        ActiveHintWindow = hint;
        _ = ShowHintThenHideAsync(hint);
    }

    private async Task ShowHintThenHideAsync(Window hint)
    {
        try
        {
            await hint.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 首关托盘提示窗展示失败（主窗照常隐藏）：{ex.Message}");
        }
        finally
        {
            ActiveHintWindow = null;
            Hide();
        }
    }

    private Window BuildHintWindow()
    {
        // 代码建窗（同 App.ShowStartupError 先例）：一次性提示，无需 XAML 视图；文案经 Translations
        // 取串。颜色/字号不硬编码——复用共享样式类（page-subtitle 随明暗走令牌），按钮走 Semi Primary。
        var okButton = new Button
        {
            Content = Translations.Instance.TrayHintOk,
            IsDefault = true,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var title = new TextBlock
        {
            Text = Translations.Instance.TrayHintTitle,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        title.Classes.Add("panel-title");

        var body = new TextBlock
        {
            Text = Translations.Instance.TrayHintBody,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        body.Classes.Add("page-subtitle");

        var window = new Window
        {
            Title = Translations.Instance.TrayHintTitle,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children = { title, body, okButton },
            },
        };
        okButton.Click += (_, _) => window.Close();
        return window;
    }

    private async Task SaveHintFlagAsync()
    {
        try
        {
            await _settingsStore!.SaveAsync(_settings!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 首关提示状态位落盘失败（下次关窗会再次提示）：{ex.Message}");
        }
    }

    private static void ExitViaLifetime()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 与托盘「退出」同一退出通道：生命周期正常收尾（App 在 desktop.Exit 停后台刷新定时器），
            // 不用 Environment.Exit，也不依赖 OnLastWindowClose 的隐式退出。
            desktop.Shutdown();
        }
    }
}
