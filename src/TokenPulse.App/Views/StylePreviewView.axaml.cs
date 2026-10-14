using Avalonia.Controls;
using Avalonia.Interactivity;
using TokenPulse.App.DependencyInjection;
using TokenPulse.App.Services;
using TokenPulse.App.ViewModels;
using TokenPulse.Core.Abstractions;

namespace TokenPulse.App.Views;

/// <summary>
/// [I-027] dev-only 样式预览页（Q-002 定案保留）：设计令牌与共享样式的目检载体，
/// 支持就地切换明暗主题与弹出 toast；[D-004/D-005] 图表控件演示区
/// （ChartPreviewViewModel：fixture ↔ 真实 IUsageQueryService 开关）。
/// 定位：截图/测试基建 + 开发预览——<b>无生产入口</b>（不在外壳导航与路由中，唯一打开方式
/// 为 TOKENPULSE_PREVIEW_WINDOW=1 的开发预览窗）；真机目检与明暗截图留档（TOKENPULSE_STYLE_PREVIEW_DIR）
/// 继续依赖本页。生产视图不再依赖它时可删。
/// </summary>
public partial class StylePreviewView : UserControl
{
    public StylePreviewView()
    {
        InitializeComponent();
        DataContext = new ChartPreviewViewModel(AppServices.Get<IUsageQueryService>());
    }

    private void OnLightThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("light");

    private void OnDarkThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("dark");

    private void OnSystemThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("system");

    private void OnToastClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IToastService>().Show("Toast 演示：I-027 共享反馈样式", this);

    private void OnReloadChartsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChartPreviewViewModel vm)
        {
            _ = vm.LoadAsync();
        }
    }
}
