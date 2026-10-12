using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.App.Views;
using Xunit;

// xunit v3 版 Avalonia.Headless.XUnit 的应用配置属性为 assembly 级
[assembly: AvaloniaTestApplication(typeof(CCUsage.App.Headless.Tests.Theming.SemiTestAppBuilder))]

namespace CCUsage.App.Headless.Tests.Theming;

/// <summary>
/// I-026 Semi.Avalonia 主题迁移的自动化验证（headless，真实 App.axaml）：
/// 1. 根主题确为 SemiTheme + DataGridSemiTheme；
/// 2. 现有全部视图（含 MainWindow）在 Semi 下可实例化、无 XAML 加载错误；
/// 3. 自建 ThemeDictionaries 画刷键与 Semi 资源无键名冲突；
/// 4. ThemeService 三态（light/dark/system）经 RequestedThemeVariant 与 Semi 桥接有效。
/// </summary>
public class SemiThemeMigrationTests
{
    /// <summary>App.axaml 定义的 10 组自建主题画刷键（Light/Dark 各一份）。与 App.axaml 保持同步。</summary>
    public static readonly string[] AppBrushKeys =
    [
        "SecondaryTextBrush",
        "MutedTextBrush",
        "CardBackgroundBrush",
        "CardAccentBackgroundBrush",
        "SeparatorBorderBrush",
        "WarningBannerBackgroundBrush",
        "WarningBannerBorderBrush",
        "WarningBannerTextBrush",
        "DangerTextBrush",
        "AccentLinkTextBrush",
    ];

    /// <summary>自建画刷的 Light/Dark 期望色值（与 App.axaml ThemeDictionaries 同步）。</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> ExpectedBrushColors = new()
    {
        ["SecondaryTextBrush"] = ("#666666", "#a9afbb"),
        ["MutedTextBrush"] = ("#888888", "#8c929c"),
        ["CardBackgroundBrush"] = ("#f8f9fa", "#2a2e36"),
        ["CardAccentBackgroundBrush"] = ("#eef6ff", "#223047"),
        ["SeparatorBorderBrush"] = ("#dddddd", "#3a404c"),
        ["WarningBannerBackgroundBrush"] = ("#fff7e6", "#3b3320"),
        ["WarningBannerBorderBrush"] = ("#ffd591", "#7a6530"),
        ["WarningBannerTextBrush"] = ("#8a5a00", "#e5c878"),
        ["DangerTextBrush"] = ("#bb0000", "#ff8080"),
        ["AccentLinkTextBrush"] = ("#336699", "#82b4e8"),
    };

    [AvaloniaFact]
    public void App_root_theme_is_SemiTheme_and_DataGridSemiTheme()
    {
        var app = Application.Current!;

        Assert.Contains(app.Styles, s => s is Semi.Avalonia.SemiTheme);
        Assert.Contains(app.Styles, s => s is Semi.Avalonia.DataGrid.DataGridSemiTheme);
        // SemiTheme 的 Locale 应为 zh-CN（与界面文案一致）
        var semi = app.Styles.OfType<Semi.Avalonia.SemiTheme>().Single();
        Assert.NotNull(semi.Locale);
        Assert.Equal("zh-CN", semi.Locale!.Name);
    }

    [AvaloniaFact]
    public void App_theme_dictionaries_resolve_all_ten_brushes_for_light_and_dark()
    {
        var app = Application.Current!;

        foreach (var (key, (light, dark)) in ExpectedBrushColors)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var lightValue),
                $"Light 变体缺少资源 {key}");
            Assert.Equal(Color.Parse(light), Assert.IsAssignableFrom<ISolidColorBrush>(lightValue).Color);

            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var darkValue),
                $"Dark 变体缺少资源 {key}");
            Assert.Equal(Color.Parse(dark), Assert.IsAssignableFrom<ISolidColorBrush>(darkValue).Color);
        }
    }

    [AvaloniaFact]
    public void Semi_theme_has_no_resource_key_collision_with_app_brushes()
    {
        // 用独立的 SemiTheme 实例查询 Semi 自身资源：若 Semi 定义了与自建画刷同名的键，
        // Application.Resources 里的自建值会遮蔽它并污染 Semi 控件模板配色。
        var semi = new Semi.Avalonia.SemiTheme();

        foreach (var key in AppBrushKeys)
        {
            Assert.False(semi.TryGetResource(key, ThemeVariant.Light, out _),
                $"Semi 定义了同名资源 {key}（Light），自建画刷将遮蔽 Semi 控件模板");
            Assert.False(semi.TryGetResource(key, ThemeVariant.Dark, out _),
                $"Semi 定义了同名资源 {key}（Dark），自建画刷将遮蔽 Semi 控件模板");
        }

        // 空转保护：确认查询通道本身有效（Semi 资源确实可解析出非空值）
        Assert.True(semi.TryGetResource("SemiAIPurple0Color", ThemeVariant.Light, out var semiColor));
        Assert.NotNull(semiColor);
    }

    [AvaloniaFact]
    public void ThemeService_light_dark_system_bridge_with_SemiTheme()
    {
        var app = Application.Current!;
        var themeService = new ThemeService();

        // dark
        themeService.Apply("dark");
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        Assert.True(app.TryGetResource("CardBackgroundBrush", app.ActualThemeVariant, out var darkBrush));
        Assert.Equal(Color.Parse("#2a2e36"), Assert.IsAssignableFrom<ISolidColorBrush>(darkBrush).Color);
        // Semi 自有资源同样经 ThemeVariant 感知随三态换色（Light #F9F9F9 / Dark #1C1F23）
        Assert.True(app.TryGetResource("SemiGrey0Color", app.ActualThemeVariant, out var darkSemi));
        Assert.Equal(Color.Parse("#1C1F23"), Assert.IsAssignableFrom<Color>(darkSemi));

        // light
        themeService.Apply("light");
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
        Assert.True(app.TryGetResource("CardBackgroundBrush", app.ActualThemeVariant, out var lightBrush));
        Assert.Equal(Color.Parse("#f8f9fa"), Assert.IsAssignableFrom<ISolidColorBrush>(lightBrush).Color);
        Assert.True(app.TryGetResource("SemiGrey0Color", app.ActualThemeVariant, out var lightSemi));
        Assert.Equal(Color.Parse("#F9F9F9"), Assert.IsAssignableFrom<Color>(lightSemi));

        // system：RequestedThemeVariant 回到 Default（跟随操作系统，由宿主解析出具体变体）
        themeService.Apply("system");
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.NotEqual(ThemeVariant.Default, app.ActualThemeVariant);
    }

    [AvaloniaTheory]
    [InlineData(typeof(DashboardView))]
    [InlineData(typeof(ReportsView))]
    [InlineData(typeof(ModelBreakdownView))]
    [InlineData(typeof(SettingsView))]
    [InlineData(typeof(EmptyStateView))]
    [InlineData(typeof(TrendSpikeView))]
    public void Existing_views_instantiate_without_xaml_errors_under_Semi(Type viewType)
    {
        var view = (UserControl)Activator.CreateInstance(viewType)!;
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void MainWindow_instantiates_under_Semi()
    {
        var window = new MainWindow
        {
            DataContext = AppServices.Get<MainWindowViewModel>(),
        };
        Assert.Equal(5, window.FindLogicalDescendantOfType<TabControl>()?.Items.Count);
    }

    [AvaloniaFact]
    public void TrendSpike_dataGrid_demo_resolves_three_columns_under_semi_styles()
    {
        var app = Application.Current!;
        var view = new TrendSpikeView();
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show(); // 挂入视觉树触发样式应用（DataGridSemiTheme 对 DataGrid 生效）
        Dispatcher.UIThread.RunJobs();

        var dataGrid = view.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(3, dataGrid.Columns.Count);
        Assert.All(dataGrid.Columns, c => Assert.NotNull(c.Header));
        Assert.False(dataGrid.AutoGenerateColumns);
        // DataGridLineBrush 只由 DataGridSemiTheme 定义：解析得到即证明 Semi DataGrid 样式确实挂载生效。
        // 注意：Semi 的 Styles 主题字典只在 Application 级资源查询可达（window/控件级 TryGetResource 查不到）
        Assert.True(app.TryGetResource("DataGridLineBrush", ThemeVariant.Light, out var dgBrush));
        Assert.NotNull(dgBrush);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
