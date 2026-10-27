using System;
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
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Xunit;

// xunit v3 版 Avalonia.Headless.XUnit 的应用配置属性为 assembly 级
[assembly: AvaloniaTestApplication(typeof(Tokenmetry.App.Headless.Tests.Theming.SemiTestAppBuilder))]

namespace Tokenmetry.App.Headless.Tests.Theming;

/// <summary>
/// I-026 Semi.Avalonia 主题迁移的自动化验证（headless，真实 App.axaml），Q-002 随旧视图清理演进：
/// 1. 根主题确为 SemiTheme（zh-CN），且旧 DataGridSemiTheme 已随 TrendSpike(D-002) 临时页撤下；
/// 2. 现存全部视图（8 真实页 + 占位 + dev-only 预览页 + MainWindow）在 Semi 下可实例化、无 XAML 加载错误；
/// 3. ThemeService 三态（light/dark/system）经 RequestedThemeVariant 与 Semi 桥接有效；
/// 4. Q-002 结构锁：旧视图/旧 VM/旧 CSV 导出线类型不再存在于应用程序集（防残留复活）。
/// （I-026 的 10 组旧画刷随旧视图删除，其 Light/Dark 取值与 Semi 键名冲突检测职责由
///   DesignTokenTests 对 App* 令牌的等价断言接管。）
/// </summary>
public class SemiThemeMigrationTests
{
    [AvaloniaFact]
    public void App_root_theme_is_SemiTheme_without_legacy_datagrid_theme()
    {
        var app = Application.Current!;

        Assert.Contains(app.Styles, s => s is Semi.Avalonia.SemiTheme);
        // Q-002 结构锁：DataGridSemiTheme 随 TrendSpike 演示页移除（生产视图不再使用 DataGrid，
        // 测试工程也不再有 Semi.Avalonia.DataGrid 引用——按类型全名断言防回归）；
        // 若未来重新引入 DataGrid，应连同两个 DataGrid 包引用一起恢复并回改本断言。
        Assert.DoesNotContain(app.Styles, s =>
            s.GetType().FullName == "Semi.Avalonia.DataGrid.DataGridSemiTheme");
        // SemiTheme 的 Locale 应为 zh-CN（与界面文案一致）
        var semi = app.Styles.OfType<Semi.Avalonia.SemiTheme>().Single();
        Assert.NotNull(semi.Locale);
        Assert.Equal("zh-CN", semi.Locale!.Name);
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
        // I-027 令牌随三态换色（AppPanelBrush：Light #ffffff / Dark #171c25，见 DesignTokenTests）
        Assert.True(app.TryGetResource("AppPanelBrush", app.ActualThemeVariant, out var darkBrush));
        Assert.Equal(Color.Parse("#171c25"), Assert.IsAssignableFrom<ISolidColorBrush>(darkBrush).Color);
        // Semi 自有资源同样经 ThemeVariant 感知随三态换色（Light #F9F9F9 / Dark #1C1F23）
        Assert.True(app.TryGetResource("SemiGrey0Color", app.ActualThemeVariant, out var darkSemi));
        Assert.Equal(Color.Parse("#1C1F23"), Assert.IsAssignableFrom<Color>(darkSemi));

        // light
        themeService.Apply("light");
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
        Assert.True(app.TryGetResource("AppPanelBrush", app.ActualThemeVariant, out var lightBrush));
        Assert.Equal(Color.Parse("#ffffff"), Assert.IsAssignableFrom<ISolidColorBrush>(lightBrush).Color);
        Assert.True(app.TryGetResource("SemiGrey0Color", app.ActualThemeVariant, out var lightSemi));
        Assert.Equal(Color.Parse("#F9F9F9"), Assert.IsAssignableFrom<Color>(lightSemi));

        // system：RequestedThemeVariant 回到 Default（跟随操作系统，由宿主解析出具体变体）
        themeService.Apply("system");
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.NotEqual(ThemeVariant.Default, app.ActualThemeVariant);
    }

    [AvaloniaTheory]
    [InlineData(typeof(OverviewView))]
    [InlineData(typeof(SessionsView))]
    [InlineData(typeof(AgentsView))]
    [InlineData(typeof(ModelsView))]
    [InlineData(typeof(ProjectsView))]
    [InlineData(typeof(TimeAnalysisView))]
    [InlineData(typeof(CostAnalysisView))]
    [InlineData(typeof(SettingsView))]
    [InlineData(typeof(DataPricingView))]
    [InlineData(typeof(PagePlaceholderView))]
    [InlineData(typeof(StylePreviewView))]
    public void Existing_views_instantiate_without_xaml_errors_under_Semi(Type viewType)
    {
        // Q-002：InlineData 从 4 个旧视图演进为 8 个真实页 + 占位 + dev-only 预览页——
        // 存活结构即本理论锁定的面（StylePreviewView 构造取真实 DI 的 IUsageQueryService）。
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
        // I-026/I-027 时代为 6 个 Tab 的 TabControl；D-003 起替换为三栏外壳，此断言随结构演进：
        // 导航为 9 项 ListBox（原型 .nav 八页 + S-004 拆出的数据与计价页），旧 TabControl 不复存在。
        Assert.Null(window.FindLogicalDescendantOfType<TabControl>());
        var navList = window.FindLogicalDescendantOfType<ListBox>();
        Assert.NotNull(navList);
        Assert.Equal(9, navList!.ItemCount);
    }

    [AvaloniaFact]
    public void Legacy_view_and_dead_service_types_are_removed_from_app_assembly()
    {
        // Q-002 结构锁：旧视图 / 旧 VM / 旧视图专属空态与 CSV 导出线不再存在于应用程序集。
        // 若清理后需要恢复其中任何类型，应连同对应卡（Reports/导出等）一起评审，而不是悄悄加回。
        var removed = new[]
        {
            "DashboardView", "ReportsView", "ModelBreakdownView", "TrendSpikeView", "EmptyStateView",
            "ReportsViewModel", "ModelBreakdownViewModel", "TrendSpikeViewModel",
            "ReportsDailyRow", "ReportCsvFormatter", "ReportCsvExporter", "IReportCsvExporter",
        };
        var present = typeof(App).Assembly.GetTypes().Select(t => t.Name).ToHashSet();

        var residuals = removed.Where(present.Contains).ToList();
        Assert.True(residuals.Count == 0, $"旧类型残留在应用程序集：{string.Join(", ", residuals)}");
    }
}
