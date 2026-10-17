using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tokenmetry.App.Tray;

/// <summary>
/// D-016 弹窗壳（无边框 / 透明 / 投影 / 失焦收起 / Esc 收起）：交互分工 = 托盘左键 Toggle 弹层、
/// 右键既有菜单（macOS interop 路径：右键原生 NSMenu；Avalonia 路径：NativeMenu）；内容层 100%
/// 来自 D-015（DataContext 即 StatusPopoverViewModel）。
/// 投影色走令牌（AppPopoverShadowColor，随明暗翻转）——XAML 无法把 BoxShadow 的颜色部分交给
/// DynamicResource，故在代码 Behind 按 ActualThemeVariant 取值应用（视图文件零硬编码色值的纪律
/// 由令牌层承接）。四周 Margin（<see cref="ShadowMargins"/>）是 BoxShadow 的渲染留白，控制器
/// 锚定换算（可见卡片 = 窗口矩形内缩边距）使用同一常量。
/// </summary>
public partial class StatusPopoverWindow : Window
{
    /// <summary>投影渲染留白（左,上,右,下）：可见卡片 = 窗口内容矩形内缩该边距。</summary>
    internal static readonly Thickness ShadowMargins = new(30, 16, 30, 34);

    public StatusPopoverWindow()
    {
        InitializeComponent();
        ShadowHost.Margin = ShadowMargins;
        Deactivated += (_, _) => Hide();
    }

    /// <summary>Esc 收起（弹层期间唯一的键盘出口；失焦收起在 Deactivated）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyShadow();
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => ApplyShadow();

    private void ApplyShadow()
    {
        if (Application.Current?.TryGetResource("AppPopoverShadowColor", ActualThemeVariant, out var value) == true
            && value is Color color)
        {
            ShadowHost.BoxShadow = BoxShadows.Parse($"0 10 36 0 {FormatColor(color)}");
        }
    }

    private static string FormatColor(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
