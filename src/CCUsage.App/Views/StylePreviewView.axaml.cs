using Avalonia.Controls;
using Avalonia.Interactivity;
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;

namespace CCUsage.App.Views;

/// <summary>
/// [I-027] 临时样式预览页（见 StylePreviewView.axaml 头注释）：设计令牌与共享样式的
/// 目检载体，支持就地切换明暗主题与弹出 toast；v0.4 页面卡落地后随本页删除。
/// </summary>
public partial class StylePreviewView : UserControl
{
    public StylePreviewView()
    {
        InitializeComponent();
    }

    private void OnLightThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("light");

    private void OnDarkThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("dark");

    private void OnSystemThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("system");

    private void OnToastClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IToastService>().Show("Toast 演示：I-027 共享反馈样式", this);
}
