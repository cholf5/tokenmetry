using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Tokenmetry.App.Services;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// I-027 设计令牌与共享样式的自动化验证（headless，真实 App.axaml）：
/// 1. 暗色色板逐键等于原型原值（:root 变量与 .kpi/.chip/.mini-track 等选择器色值）；
/// 2. 亮色协调值可解析；
/// 3. 7 个图表系列色与 mini 填充渐变不随明暗翻转（普通资源语义）；
/// 4. 全部新令牌键与 Semi 资源零键名冲突；
/// 5. 共享样式（panel/kpi/chip）在窗口内真实应用令牌画刷；
/// 6. mini 进度条按 Value 填充轨道（复用内置 ProgressBar 的 PART_Track/PART_Indicator）；
/// 7. ToastService 能在宿主窗口弹出 NotificationCard；
/// 8. 样式预览页在 Light/Dark 两变体下可离屏渲染（PNG 留档）。
/// </summary>
public class DesignTokenTests
{
    /// <summary>主题翻转画刷键 → (Light, Dark) 期望色值。Dark 列 = 原型原值，与 Styles/Tokens.axaml 保持同步。</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> PaletteBrushes = new()
    {
        // :root --bg/--panel/--panel2/--line/--text/--muted
        ["AppBackgroundBrush"] = ("#f4f6fa", "#0b1120"),
        ["AppPanelBrush"] = ("#ffffff", "#111c2e"),
        ["AppPanelAltBrush"] = ("#eef1f6", "#142137"),
        ["AppLineBrush"] = ("#d4dbe6", "#24344d"),
        ["AppTextBrush"] = ("#1c2735", "#e8eef9"),
        ["AppMutedTextBrush"] = ("#66788f", "#8293ad"),
        // :root 六个强调色变量
        ["AppAccentBlueBrush"] = ("#2e6bdc", "#4c8dff"),
        ["AppAccentCyanBrush"] = ("#1d9bc4", "#57c7e8"),
        ["AppAccentPurpleBrush"] = ("#7d4fe0", "#a779ff"),
        ["AppAccentGreenBrush"] = ("#0e9e77", "#38d6a4"),
        ["AppAccentOrangeBrush"] = ("#c77700", "#ffb454"),
        ["AppAccentRedBrush"] = ("#d9374f", "#ff7185"),
        // 卡片描边（.kpi/.panel 的 #22324a）/ 链接（.linkbtn）/ chip
        ["AppCardLineBrush"] = ("#d8e0eb", "#22324a"),
        ["AppLinkBrush"] = ("#2f6bd8", "#80adff"),
        ["AppChipBackgroundBrush"] = ("#e3ecfb", "#1b2c44"),
        ["AppChipTextBrush"] = ("#2b5d9f", "#a8c8ff"),
        // mini 进度条轨道（.mini-track）
        ["AppMiniTrackBrush"] = ("#e2e8f0", "#23334b"),
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
        ["AppSidebarBackgroundBrush"] = ("#edf1f7", "#0d1626"),
        ["AppTopbarBackgroundBrush"] = ("#f0f4f9", "#0e1728"),
        ["AppNavActiveBackgroundBrush"] = ("#dbe7fb", "#1b2d49"),
        ["AppNavActiveForegroundBrush"] = ("#2b5d9f", "#9dc2ff"),
        ["AppNavActiveIndicatorBrush"] = ("#2e6bdc", "#5794ff"),
        ["AppNavHoverBackgroundBrush"] = ("#e3eaf4", "#17253b"),
        // D-004 图表 chrome（.chart .gridline/.axis 色值；Light 为协调补值）
        ["AppChartGridLineBrush"] = ("#d9e1ec", "#23344b"),
        ["AppChartAxisLineBrush"] = ("#b9c5d7", "#35465e"),
        // 首次初始化全窗遮罩底（MainWindow.axaml；页面底色 90% 不透明，Light 为协调补值）
        ["AppOverlayScrimBrush"] = ("#e6f4f6fa", "#e60b1120"),
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
    /// 的 DrawingImage 键。</summary>
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

        // 卡片渐变 ← 原型 .kpi,.panel linear-gradient(145deg,#121e31,#101a2b)（Dark 原值 / Light 协调值）
        Assert.True(app.TryGetResource("AppCardBackgroundBrush", ThemeVariant.Dark, out var darkCard));
        var darkGradient = Assert.IsType<LinearGradientBrush>(darkCard);
        Assert.Equal(Color.Parse("#121e31"), darkGradient.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#101a2b"), darkGradient.GradientStops[1].Color);

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
}
