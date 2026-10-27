using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Headless.Tests.Shell;
using Tokenmetry.App.Services;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// I-027 设计令牌与共享样式的自动化验证（headless，真实 App.axaml）：
/// 1. 暗色色板逐键等于原型原值经 Q-019 降饱和后的现值（大面积表层去蓝：保色相、保亮度）；
/// 2. 亮色协调值可解析；
/// 3. 7 个图表系列色与 mini 填充渐变不随明暗翻转（普通资源语义）；
/// 4. 全部新令牌键与 Semi 资源零键名冲突；
/// 5. 共享样式（panel/kpi/chip）在窗口内真实应用令牌画刷；
/// 6. mini 进度条按 Value 填充轨道（复用内置 ProgressBar 的 PART_Track/PART_Indicator）；
/// 7. ToastService 能在宿主窗口弹出 NotificationCard；
/// 8. 样式预览页在 Light/Dark 两变体下可离屏渲染（PNG 留档）；
/// 9. ToastService 的后台线程调用被 marshal 到 UI 线程（2026-10-20 启动闪退回归）。
/// </summary>
public class DesignTokenTests
{
    /// <summary>主题翻转画刷键 → (Light, Dark) 期望色值。Dark 列 = 原型原值经 Q-019 降饱和
    /// （保色相、保亮度，语义色与文字色不动），与 Styles/Tokens.axaml 保持同步。</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> PaletteBrushes = new()
    {
        // :root --bg/--panel/--panel2/--line/--text/--muted
        ["AppBackgroundBrush"] = ("#f4f6fa", "#0f1118"),
        ["AppPanelBrush"] = ("#ffffff", "#171c25"),
        ["AppPanelAltBrush"] = ("#eef1f6", "#1b212c"),
        ["AppLineBrush"] = ("#d4dbe6", "#2c3440"),
        ["AppTextBrush"] = ("#1c2735", "#e8eef9"),
        ["AppMutedTextBrush"] = ("#546783", "#8293ad"),
        // :root 六个强调色变量
        ["AppAccentBlueBrush"] = ("#2e6bdc", "#4c8dff"),
        ["AppAccentCyanBrush"] = ("#1d9bc4", "#57c7e8"),
        ["AppAccentPurpleBrush"] = ("#7d4fe0", "#a779ff"),
        ["AppAccentGreenBrush"] = ("#0e9e77", "#38d6a4"),
        ["AppAccentOrangeBrush"] = ("#c77700", "#ffb454"),
        ["AppAccentRedBrush"] = ("#d9374f", "#ff7185"),
        // 卡片描边（.kpi/.panel 的 #22324a）/ 链接（.linkbtn）/ chip
        ["AppCardLineBrush"] = ("#d8e0eb", "#2a323d"),
        ["AppLinkBrush"] = ("#2f6bd8", "#80adff"),
        ["AppChipBackgroundBrush"] = ("#e3ecfb", "#232c39"),
        ["AppChipTextBrush"] = ("#2b5d9f", "#a8c8ff"),
        // mini 进度条轨道（.mini-track）
        ["AppMiniTrackBrush"] = ("#e2e8f0", "#2a3340"),
        // KPI 卡顶标签（.kpi-top；D-006，Light 为协调补值）
        ["AppKpiTitleBrush"] = ("#5d6f86", "#b7c5d9"),
        // 图标/头像软色底（.kpi-icon 四变体 + .agenticon.gray）
        ["AppIconChipBlueBackgroundBrush"] = ("#dce9fb", "#20395b"),
        ["AppIconChipBlueForegroundBrush"] = ("#2f66c0", "#8db8ff"),
        ["AppIconChipAmberBackgroundBrush"] = ("#f7ecd4", "#3c3321"),
        ["AppIconChipAmberForegroundBrush"] = ("#a06a00", "#ffcb64"),
        ["AppIconChipPurpleBackgroundBrush"] = ("#ece4fb", "#33284f"),
        ["AppIconChipPurpleForegroundBrush"] = ("#7a4fd4", "#c0a0ff"),
        ["AppIconChipGreenBackgroundBrush"] = ("#d9f3ec", "#173d3b"),
        ["AppIconChipGreenForegroundBrush"] = ("#0f8f6c", "#67e3bd"),
        ["AppIconChipNeutralBackgroundBrush"] = ("#e8ecf2", "#263143"),
        ["AppIconChipNeutralForegroundBrush"] = ("#5a6b80", "#bac8da"),
        // D-003 应用外壳（.sidebar/.topbar/.nav 各选择器色值；Light 为协调补值）
        ["AppSidebarBackgroundBrush"] = ("#edf1f7", "#12161e"),
        ["AppTopbarBackgroundBrush"] = ("#f0f4f9", "#13171f"),
        ["AppNavForegroundBrush"] = ("#3d4c63", "#8293ad"),
        ["AppNavActiveBackgroundBrush"] = ("#dbe7fb", "#242d3c"),
        ["AppNavActiveForegroundBrush"] = ("#2b5d9f", "#9dc2ff"),
        ["AppNavActiveIndicatorBrush"] = ("#2e6bdc", "#5794ff"),
        ["AppNavHoverBackgroundBrush"] = ("#e3eaf4", "#1e2531"),
        // 导航项静息态：与 hover 同 RGB 的 0 alpha 画刷（BrushTransition 起点，不能是 Transparent）
        ["AppNavRestBackgroundBrush"] = ("#00e3eaf4", "#001e2531"),
        // D-004 图表 chrome（.chart .gridline/.axis 色值；Light 为协调补值）
        ["AppChartGridLineBrush"] = ("#d9e1ec", "#2c343f"),
        ["AppChartAxisLineBrush"] = ("#b9c5d7", "#3d4651"),
        // 首次初始化全窗遮罩底（MainWindow.axaml；页面底色 90% 不透明，Light 为协调补值）
        ["AppOverlayScrimBrush"] = ("#e6f4f6fa", "#e60f1118"),
        // 滚动条滑块（overlay 细条：闲置隐藏/贴边/无箭头；无原型对应，Light 为协调补值）
        ["AppScrollBarThumbBrush"] = ("#c2cbd9", "#475164"),
        ["AppScrollBarThumbHoverBrush"] = ("#93a5bd", "#7386a6"),
        // D-016 弹窗卡片毛玻璃半透明面（AppPanelBrush 同基调 90% 不透明，acrylic 生效时启用）
        ["AppPopoverCardBackgroundBrush"] = ("#e6ffffff", "#e6171c25"),
    };

    /// <summary>图表系列色（原型 JS colors[] 数组，索引自 0）→ 期望色值，不随明暗翻转。</summary>
    private static readonly (string Key, string Hex)[] SeriesColors =
    [
        ("AppSeries0Color", "#4c8dff"),
        ("AppSeries1Color", "#12b886"),
        ("AppSeries2Color", "#ffad4d"),
        ("AppSeries3Color", "#a779ff"),
        ("AppSeries4Color", "#48bde3"),
        ("AppSeries5Color", "#8394ae"),
        ("AppSeries6Color", "#f06bc4"),
    ];

    /// <summary>Token 类型构成环形固定三色（D-007，原型 typeDonut 三元组）→ 期望色值，不随明暗翻转。</summary>
    private static readonly (string Key, string Hex)[] TokenTypeColors =
    [
        ("AppTokenTypeInputColor", "#4c8dff"),
        ("AppTokenTypeOutputColor", "#a779ff"),
        ("AppTokenTypeCacheColor", "#38d6a4"),
    ];

    /// <summary>全部新令牌键（冲突检测用）：主题画刷 + 卡片/填充渐变 + 系列色/类型色 Color/Brush
    /// + 模型厂商 logo（I-033，ModelLogos.axaml）+ Agent 品牌 logo（I-034，AgentLogos.axaml）
    /// 的 DrawingImage 键 + 界面图标（2026-10-24，Icons.axaml 的 AppIcon* StreamGeometry）。</summary>
    private static readonly string[] AllTokenKeys =
    [
        .. PaletteBrushes.Keys,
        "AppCardBackgroundBrush",
        "AppMiniFillBrush",
        .. SeriesColors.Select(s => s.Key),
        .. SeriesColors.Select(s => s.Key.Replace("Color", "Brush", StringComparison.Ordinal)),
        .. TokenTypeColors.Select(s => s.Key),
        .. TokenTypeColors.Select(s => s.Key.Replace("Color", "Brush", StringComparison.Ordinal)),
        .. ModelLogoTests.LogoKeys.Values,
        .. AgentLogoTests.LogoKeys.Values,
        .. IconResourceTests.GeometryKeys.Values,
    ];

    [AvaloniaFact]
    public void Dark_palette_matches_prototype_values_exactly()
    {
        var app = Application.Current!;

        foreach (var (key, (_, dark)) in PaletteBrushes)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var value),
                $"Dark 变体缺少资源 {key}");
            Assert.Equal(Color.Parse(dark), Assert.IsAssignableFrom<ISolidColorBrush>(value).Color);
        }
    }

    [AvaloniaFact]
    public void Light_palette_resolves_coordinated_values()
    {
        var app = Application.Current!;

        foreach (var (key, (light, _)) in PaletteBrushes)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var value),
                $"Light 变体缺少资源 {key}");
            Assert.Equal(Color.Parse(light), Assert.IsAssignableFrom<ISolidColorBrush>(value).Color);
        }
    }

    [AvaloniaFact]
    public void Card_and_mini_fill_gradients_match_prototype()
    {
        var app = Application.Current!;

        // 卡片渐变 ← 原型 .kpi,.panel linear-gradient(145deg,#121e31,#101a2b)（Dark 经 Q-019 降饱和 / Light 协调值）
        Assert.True(app.TryGetResource("AppCardBackgroundBrush", ThemeVariant.Dark, out var darkCard));
        var darkGradient = Assert.IsType<LinearGradientBrush>(darkCard);
        Assert.Equal(Color.Parse("#181e27"), darkGradient.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#151a22"), darkGradient.GradientStops[1].Color);

        Assert.True(app.TryGetResource("AppCardBackgroundBrush", ThemeVariant.Light, out var lightCard));
        var lightGradient = Assert.IsType<LinearGradientBrush>(lightCard);
        Assert.Equal(Color.Parse("#ffffff"), lightGradient.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#f7f9fc"), lightGradient.GradientStops[1].Color);

        // mini 填充 ← 原型 .mini-fill linear-gradient(90deg,#3e7df0,#65b9ff)；90deg = 左→右，普通资源不翻转
        Assert.True(app.TryGetResource("AppMiniFillBrush", ThemeVariant.Dark, out var darkFill));
        var darkFillGradient = Assert.IsType<LinearGradientBrush>(darkFill);
        Assert.Equal(new Point(0, 0.5), darkFillGradient.StartPoint.Point);
        Assert.Equal(new Point(1, 0.5), darkFillGradient.EndPoint.Point);
        Assert.Equal(Color.Parse("#3e7df0"), darkFillGradient.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#65b9ff"), darkFillGradient.GradientStops[1].Color);

        Assert.True(app.TryGetResource("AppMiniFillBrush", ThemeVariant.Light, out var lightFill));
        Assert.Same(darkFill, lightFill); // 同一实例 ⇒ 与明暗无关
    }

    [AvaloniaFact]
    public void Series_colors_are_theme_invariant_and_match_prototype_js_array()
    {
        var app = Application.Current!;

        foreach (var (key, hex) in SeriesColors)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var lightColor));
            Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<Color>(lightColor));

            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var darkColor));
            Assert.Same(lightColor, darkColor); // 同一实例 ⇒ 与明暗无关

            // 派生画刷与 Color 键同值（XAML 圆点/图例直接用 Brush）
            Assert.True(app.TryGetResource(key.Replace("Color", "Brush", StringComparison.Ordinal), ThemeVariant.Dark, out var brush));
            Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
        }
    }

    [AvaloniaFact]
    public void Token_type_colors_are_theme_invariant_and_match_prototype_type_donut()
    {
        var app = Application.Current!;

        foreach (var (key, hex) in TokenTypeColors)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var lightColor),
                $"Light 变体缺少资源 {key}");
            Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<Color>(lightColor));

            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var darkColor),
                $"Dark 变体缺少资源 {key}");
            Assert.Same(lightColor, darkColor); // 同一实例 ⇒ 与明暗无关

            // 派生画刷与 Color 键同值（XAML 图例/圆点直接用 Brush）
            Assert.True(app.TryGetResource(key.Replace("Color", "Brush", StringComparison.Ordinal), ThemeVariant.Dark, out var brush));
            Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
        }
    }

    [AvaloniaFact]
    public void New_tokens_have_no_key_collision_with_semi_theme()
    {
        // 用独立 SemiTheme 实例查询 Semi 自身资源：若撞名，App 资源会遮蔽 Semi 控件模板配色（方法同 I-026）。
        var semi = new Semi.Avalonia.SemiTheme();

        foreach (var key in AllTokenKeys)
        {
            Assert.False(semi.TryGetResource(key, ThemeVariant.Light, out _),
                $"Semi 定义了同名资源 {key}（Light）");
            Assert.False(semi.TryGetResource(key, ThemeVariant.Dark, out _),
                $"Semi 定义了同名资源 {key}（Dark）");
        }

        // 空转保护：查询通道本身有效
        Assert.True(semi.TryGetResource("SemiGrey0Color", ThemeVariant.Light, out var semiColor));
        Assert.NotNull(semiColor);
    }

    [AvaloniaFact]
    public void Shared_styles_apply_token_brushes_to_panel_kpi_chip()
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppCardBackgroundBrush", ThemeVariant.Dark, out var cardBrush));

        var panel = new Border { Classes = { "panel" } };
        var kpi = new Border { Classes = { "kpi" } };
        var chip = new Border { Classes = { "chip" }, Child = new TextBlock { Text = "claude-code" } };
        var window = new Window
        {
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new StackPanel { Children = { panel, kpi, chip } },
        };
        window.Show(); // 挂入视觉树触发样式应用
        Dispatcher.UIThread.RunJobs();

        // panel/kpi 的表面 = AppCardBackgroundBrush（DynamicResource 解析到 App 资源同一实例）
        Assert.Same(cardBrush, panel.Background);
        Assert.Same(cardBrush, kpi.Background);
        Assert.Equal(new CornerRadius(11), panel.CornerRadius);
        Assert.Equal(new Thickness(1), panel.BorderThickness);
        Assert.Equal(new Thickness(15), panel.Padding); // 原型 .panel{padding:15px}
        Assert.Equal(new Thickness(15, 15, 13, 13), kpi.Padding); // 原型 .kpi{padding:15px 15px 13px}
        Assert.Equal(124, kpi.MinHeight); // 原型 .kpi{min-height:124px}
        Assert.Equal(new CornerRadius(5), chip.CornerRadius); // 原型 .chip{border-radius:5px}
        Assert.True(app.TryGetResource("AppChipTextBrush", ThemeVariant.Dark, out var chipText));
        Assert.Same(chipText, chip.Child!.GetValue(TextBlock.ForegroundProperty)); // attached 属性经样式落在 Border 上

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Mini_progress_bar_fills_track_by_value()
    {
        var bar = new ProgressBar { Classes = { "mini" }, Width = 300, Value = 40 };
        var window = new Window { Content = bar, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var track = bar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Track");
        var indicator = bar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Indicator");

        Assert.Equal(4, bar.Height); // 原型 .mini-track{height:4px}
        // track 底色确为 AppMiniTrackBrush 同一实例：直接证明 mini 模板（而非原生 ProgressBar 模板）已生效
        Assert.True(Application.Current!.TryGetResource("AppMiniTrackBrush", bar.ActualThemeVariant, out var miniTrack));
        Assert.Same(miniTrack, track.Background);
        Assert.InRange(indicator.Bounds.Width, track.Bounds.Width * 0.4 - 1.5, track.Bounds.Width * 0.4 + 1.5);

        bar.Value = 80;
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(indicator.Bounds.Width, track.Bounds.Width * 0.8 - 1.5, track.Bounds.Width * 0.8 + 1.5);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Data_table_rows_hover_with_shared_nav_hover_brush()
    {
        // Q-002 跨页统一：数据表行（共享 data-table 类，原 rank-table/session-table 收编）
        // hover 底色 = AppNavHoverBackgroundBrush（原型 tr:hover #17253a 同源令牌，与外壳导航一致）。
        var listBox = new ListBox { Classes = { "data-table" }, ItemsSource = new[] { "a", "b" } };
        var window = new Window { Width = 400, Height = 300, Content = listBox };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var item = Assert.IsType<ListBoxItem>(listBox.GetRealizedContainers().First());
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Dispatcher.UIThread.RunJobs();

        Assert.True(item.IsPointerOver, "悬停应命中行容器");
        Assert.True(Application.Current!.TryGetResource("AppNavHoverBackgroundBrush", window.ActualThemeVariant, out var hover));
        Assert.Same(hover, item.Background);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Toast_service_shows_notification_card_in_host_window()
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AppServices.Get<IToastService>().Show("I-027 toast 测试", window);
        Dispatcher.UIThread.RunJobs();

        var cards = window.GetVisualDescendants().OfType<NotificationCard>().ToList();
        Assert.NotEmpty(cards); // NotificationCard 已挂入宿主窗口（Semi 样式提供外观）

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Toast_from_background_thread_is_marshaled_to_ui_thread()
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 回归（2026-10-20 启动闪退）：后台刷新线程（BackgroundRefreshWiring 的 Task.Run，A-5）会
        // 走到 ToastService.Show（如设置页重扫的完成 toast）；在非 UI 线程 new WindowNotificationManager
        // 会把归属线程池 Dispatcher 的宿主 AdornerLayer 挂进主窗视觉树，下一次 UI 线程布局测量即抛
        // InvalidOperationException——修复 = Show 入口把整段 body marshal 到 UI 线程。
        await Task.Run(() => AppServices.Get<IToastService>().Show("background toast", window));

        // Post 是异步：泵到卡片出现为止。回归复发时，InvalidOperationException 会在 Task.Run 内
        // 同步抛出（WindowNotificationManager 构造即触跨线程附树），await 处直接失败——泵与本断言
        // 只负责修复后的正向路径（卡片经 UI 线程张贴出现）。
        DispatcherPump.PumpUntil(() => window.GetVisualDescendants().OfType<NotificationCard>().Any());

        Assert.NotEmpty(window.GetVisualDescendants().OfType<NotificationCard>());

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Style_preview_page_renders_in_light_and_dark_variants()
    {
        // 截图留档目录由环境变量指定（本仓库验证时传 docs/design）；未设置时仅断言可渲染。
        var dir = Environment.GetEnvironmentVariable("TOKENMETRY_STYLE_PREVIEW_DIR");
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Light, out var lightBg));
        Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var darkBg));

        foreach (var (variant, bg, fileName) in new (ThemeVariant, IBrush, string)[]
                 {
                     (ThemeVariant.Light, (IBrush)lightBg!, "style-preview-light.png"),
                     (ThemeVariant.Dark, (IBrush)darkBg!, "style-preview-dark.png"),
                 })
        {
            var window = new Window
            {
                Width = 880,
                Height = 720,
                Content = new StylePreviewView(),
                RequestedThemeVariant = variant,
                Background = bg, // 页底用 AppBackgroundBrush，令 PNG 体现页面底色
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // headless 渲染定时器不随 RunJobs 自动走帧，须手动触发一帧后才能截到合成结果
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame(); // headless 合成帧（整窗，含子树）
            Assert.NotNull(frame);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                frame.Save(path, new PngBitmapEncoderOptions());
                Assert.True(File.Exists(path), $"截图未生成：{path}");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── 动效令牌（motion design）：资源值与 Styles/Motion.cs 常量镜像，两处必须同步 ──

    [AvaloniaFact]
    public void Motion_duration_tokens_mirror_code_constants()
    {
        Assert.Equal(120d, GetMotionMs("AppMotionFastMs"));
        Assert.Equal(180d, GetMotionMs("AppMotionNormalMs"));
        Assert.Equal(240d, GetMotionMs("AppMotionSlowMs"));
    }

    private static double GetMotionMs(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Default, out var value),
            $"缺少动效令牌 {key}");
        Assert.IsType<double>(value);
        return (double)value!;
    }
}
