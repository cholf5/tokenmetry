using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform;
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

        /// <summary>首次关窗：先弹一次性模态选择窗（直接退出 = 默认 / 缩到托盘），按选择分派。</summary>
        ShowFirstCloseDialog,

        /// <summary>真退出：desktop.Shutdown() 生命周期收尾（与托盘「退出」同一通道）。</summary>
        Exit,
    }

    private readonly AppSettings? _settings;
    private readonly IAppSettingsStore? _settingsStore;

    /// <summary>用户在首关弹窗做出选择后广播（true = 缩到托盘 / false = 直接退出）。消费者 = App 启动
    /// 接线，把选择写回 SettingsViewModel 的关窗行为开关——该 VM 是 DI 单例、开关只在构造时从 settings
    /// 起值，弹窗直写单例会让设置页显示脱同步，经同一 VM 属性写入即同步（其自动保存为同内容的
    /// 幂等重写，与设置页并发写同一 settings.json 的 last-writer-wins 语义一致）。headless 测试无订阅者，
    /// 弹窗自身直写单例 + 落盘，行为自洽。</summary>
    internal event Action<bool>? FirstCloseChoiceCommitted;

    /// <summary>当前打开的首关选择窗（S-005）。headless 测试宿主无桌面生命周期（ApplicationLifetime
    /// 为 null，无从枚举窗口），测试只能经此属性定位弹窗；正常运行无其他消费者，选择流结束即清空。</summary>
    internal Window? ActiveFirstCloseDialog { get; private set; }

    /// <summary>首关弹窗的两个选择按钮（测试接缝：代码建窗无 NameScope 注册，经属性直取触发 Click）。</summary>
    internal Button? FirstCloseExitButton { get; private set; }
    internal Button? FirstCloseTrayButton { get; private set; }

    public MainWindow() : this(null, null)
    {
    }

    /// <summary>关窗行为依赖（App 启动注入 settings + 落盘 store；headless 测试 / 设计期用无参构造——
    /// 设置未注入时维持旧行为：隐藏、不提示、不退出）。</summary>
    internal MainWindow(AppSettings? settings, IAppSettingsStore? settingsStore)
    {
        InitializeComponent();
        // D-018：窗口/任务栏/Alt-Tab 图标（Linux WM 的 _NET_WM_ICON 同吃这里）。全出血 256
        // 专供窗口语义；Windows exe 文件图标走 csproj ApplicationIcon，macOS Dock 走 interop。
        Icon = new WindowIcon(AssetLoader.Open(
            new Uri("avares://Tokenmetry.App/Assets/app-icon-window.png")));
        // D-026：Windows 原生标题栏跟随应用明暗（系统边框本身不变，只改 caption 配色）。
        WindowsCaptionTheme.Default.Attach(this);
        _settings = settings;
        _settingsStore = settingsStore;

        // 页面进场（motion design）：Content 替换即重播；只动 Opacity/RenderTransform，
        // 不做退出动画（固定信息架构没有方向语义）
        PageHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty && PageHost.Content is not null)
            {
                RunFadeUpEntrance(PageHost);
            }
        };

        // 初始化遮罩卡片进场（motion design §7）：遮罩每次显示时轻上浮淡入；淡出不做
        //（IsVisible 硬切，设计偏差已在计划记录）。纯 XAML 挂载动画在此行不通——
        // Style.Animations 在样式首次挂载（启动、遮罩不可见）即播放完毕
        InitializingOverlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && InitializingOverlay.IsVisible)
            {
                RunFadeUpEntrance(OverlayCard);
            }
        };
    }

    // ── 进场过渡（motion design §5/§7）──

    /// <summary>进场时间线代数（按目标分账）：目标被连续触发时新进场作废旧进场。</summary>
    private readonly Dictionary<Visual, int> _fadeUpGenerations = new();

    /// <summary>
    /// 淡入 + 轻上浮进场（页面主区 / 初始化遮罩卡共用）：Opacity 0→1 + translateY 8px→0，
    /// 180ms ease-out（Motion.NormalMs）。TopLevel.RequestAnimationFrame 帧回调驱动
    /// （Avalonia 12 的 Animation 管线无法对 Transform 实例 RunAsync），结束清 RenderTransform
    /// 不残留；无宿主 TopLevel（首屏绑定早于挂树）直接落终态。
    /// </summary>
    private void RunFadeUpEntrance(Visual target)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var generation = _fadeUpGenerations.TryGetValue(target, out var g) ? g + 1 : 1;
        _fadeUpGenerations[target] = generation;
        target.Opacity = 0;
        target.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(8px)");
        var start = (System.TimeSpan?)null;
        Action<System.TimeSpan> frame = null!;
        frame = timestamp =>
        {
            if (_fadeUpGenerations.TryGetValue(target, out var current) && current != generation)
            {
                return; // 连续触发：新进场接管，旧时间线自杀
            }

            start ??= timestamp;
            var t = Math.Clamp((timestamp - start.Value).TotalMilliseconds / Tokenmetry.App.Styles.Motion.NormalMs, 0, 1);
            var eased = Tokenmetry.App.Styles.Motion.EaseOut.Ease(t);
            target.Opacity = eased;
            target.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse($"translateY({8 - 8 * eased:0.###}px)");
            if (t < 1)
            {
                topLevel.RequestAnimationFrame(frame);
            }
            else
            {
                target.Opacity = 1;
                target.RenderTransform = null;
            }
        };
        topLevel.RequestAnimationFrame(frame);
    }

    /// <summary>
    /// A-0 常驻体验（v0.5 规格）：用户关窗（点 X / Alt+F4 / 程序内 Close()）按设置分派——
    /// 默认首次关窗弹一次性模态选择窗（直接退出 = 默认 / 缩到托盘，选择即写入 MinimizeToTrayOnClose
    /// 并落盘），此后关窗按该设置隐藏到托盘（进程常驻）或退出；真正退出走托盘菜单「退出」或首关
    /// 选「直接退出」→ desktop.Shutdown()。生命周期驱动的关闭（托盘退出的 ApplicationShutdown、
    /// OS 会话结束的 OSShutdown）不在此列，天然放行。
    /// S-005：行为可由设置 MinimizeToTrayOnClose 随时在设置页更改；首关弹窗替代旧的一次性告知提示
    /// ——in-app toast（IToastService）在主窗隐藏后无处渲染，Avalonia 亦无跨平台系统通知 API
    /// （balloon 需 Win32 内部句柄、macOS 通知需 interop + 权限流程），模态窗是唯一零 interop 且
    /// "关窗时点询问"的方案。
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
            case UserCloseAction.ShowFirstCloseDialog:
                ShowFirstCloseDialog();
                break;
            default:
                Hide();
                break;
        }
    }

    /// <summary>关窗决策（纯函数，headless 可测）：设置未注入 = 旧行为隐藏；开关关闭 = 退出；
    /// 开关开启且首关选择未做出 = 先弹选择窗；其余 = 按开关隐藏或退出。</summary>
    internal static UserCloseAction ResolveUserCloseAction(AppSettings? settings) => settings switch
    {
        { MinimizeToTrayOnClose: false } => UserCloseAction.Exit,
        { FirstCloseChoiceMade: false } => UserCloseAction.ShowFirstCloseDialog,
        _ => UserCloseAction.MinimizeToTray,
    };

    private void ShowFirstCloseDialog()
    {
        // 双开守卫：连续两次关窗事件（弹窗打开前的窗口期内）只开一个弹窗。
        if (ActiveFirstCloseDialog is not null)
        {
            return;
        }

        // 状态位此刻不动：只有用户真正做出选择才置位并落盘；弹窗被直接关掉（标题栏 X）视为
        // 未选择，主窗保持可见，下次关窗再问。与设置页自动保存并发写同一份共享 settings.json：
        // 两侧写的是同一 AppSettings 单例且保存为临时文件 + 原子 Move，最坏 last-writer-wins
        // 且内容一致，无需加锁。
        var dialog = BuildFirstCloseDialog();
        ActiveFirstCloseDialog = dialog;
        _ = ShowFirstCloseDialogThenDispatchAsync(dialog);
    }

    private async Task ShowFirstCloseDialogThenDispatchAsync(Window dialog)
    {
        string? choice = null;
        try
        {
            choice = await dialog.ShowDialog<string>(this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 首关选择弹窗展示失败（主窗照常隐藏）：{ex.Message}");
            choice = "tray"; // 弹窗通道坏了不能反复打扰：按托盘常驻默认语义降级
        }
        finally
        {
            ActiveFirstCloseDialog = null;
            FirstCloseExitButton = null;
            FirstCloseTrayButton = null;
        }

        switch (choice)
        {
            case "exit":
                // 直接退出：主窗不隐藏，走生命周期收尾（与托盘「退出」同一通道）。
                ExitViaLifetime();
                break;
            case "tray":
                Hide();
                break;
            // null（标题栏 X / 未决）：视为未选择，主窗保持可见，下次关窗再问。
        }
    }

    private Window BuildFirstCloseDialog()
    {
        // 代码建窗（同 App.ShowStartupError 先例）：一次性选择弹窗，无需 XAML 视图；文案经
        // Translations 取串。颜色/字号不硬编码——复用共享样式类（page-subtitle 随明暗走令牌），
        // 按钮走 Semi 变体类（Primary=默认强调的「直接退出」）。
        var exitButton = new Button
        {
            Content = Translations.Instance.FirstCloseExitButton,
            Classes = { "Primary" },
            IsDefault = true,
            MinWidth = 96,
        };
        FirstCloseExitButton = exitButton;

        var trayButton = new Button
        {
            Content = Translations.Instance.FirstCloseTrayButton,
            Classes = { "Secondary" },
            MinWidth = 96,
        };
        FirstCloseTrayButton = trayButton;

        var title = new TextBlock
        {
            Text = Translations.Instance.FirstCloseDialogTitle,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        title.Classes.Add("panel-title");

        var body = new TextBlock
        {
            Text = Translations.Instance.FirstCloseDialogBody,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        body.Classes.Add("page-subtitle");

        var window = new Window
        {
            Title = Translations.Instance.FirstCloseDialogTitle,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children =
                {
                    title,
                    body,
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 12,
                        Children = { trayButton, exitButton },
                    },
                },
            },
        };
        WindowsCaptionTheme.Default.Attach(window);
        exitButton.Click += (_, _) => CommitFirstCloseChoice(window, "exit", minimizeToTray: false);
        trayButton.Click += (_, _) => CommitFirstCloseChoice(window, "tray", minimizeToTray: true);
        return window;
    }

    /// <summary>首关选择的落账：写关窗行为设置 + 置一次性状态位，best-effort 落盘，随弹窗回执
    /// 分派隐藏/退出；并广播给设置页开关（App 接线订阅，headless 无订阅者时设置页不存在，无需同步）。</summary>
    private void CommitFirstCloseChoice(Window dialog, string result, bool minimizeToTray)
    {
        _settings!.MinimizeToTrayOnClose = minimizeToTray;
        _settings.FirstCloseChoiceMade = true;
        _ = SaveFirstCloseChoiceAsync();
        FirstCloseChoiceCommitted?.Invoke(minimizeToTray);
        dialog.Close(result);
    }

    private async Task SaveFirstCloseChoiceAsync()
    {
        try
        {
            await _settingsStore!.SaveAsync(_settings!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 首关选择落盘失败（下次关窗会再次询问）：{ex.Message}");
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
