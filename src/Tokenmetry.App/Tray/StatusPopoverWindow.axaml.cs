using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

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
    /// <summary>投影渲染留白（左,上,右,下）：可见卡片 = 窗口内容矩形内缩该边距。
    /// 上留白 6 = macOS AppKit constrainFrameRect 把窗口顶压到菜单栏底之后的贴合间隙
    /// （弹窗"贴着菜单栏下方 6px 弹出"），同时给投影留出少量向上晕染。</summary>
    internal static readonly Thickness ShadowMargins = new(30, 6, 30, 34);

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
        UpdateAcrylicCard();
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ActualTransparencyLevelProperty)
        {
            // 透明度协商在平台窗首次 Show 时定，运行中系统设置（如透明效果开关）变化也走这里
            UpdateAcrylicCard();
        }
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => ApplyShadow();

    /// <summary>毛玻璃协商（B5）：AcrylicBlur 生效 → 内容卡切半透明面（popover-card.acrylic 类，
    /// 面本体是内容层样式 + 令牌 AppPopoverCardBackgroundBrush）；协商失败（系统透明效果关闭 /
    /// RDP / 旧 Win10 / 平台不支持 → 回落 Transparent/None）不切类，卡片保持不透明默认，对比度不破。
    /// 壳层只负责切类，内容层文件零硬编码色纪律不变。
    /// <para>时机：Avalonia.Native 对 TransparencyLevelHint 即设即报——XAML inflate 期（构造函数内，
    /// ShadowHost 尚未赋值）ActualTransparencyLevel 就可能经 OnPropertyChanged 到达，空守卫跳过；
    /// OnOpened 与其后每次真实变更都会用当前值重评，初始协商不丢。</para></summary>
    private void UpdateAcrylicCard()
    {
        var acrylic = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        var card = ShadowHost?.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("popover-card"));
        if (card is null)
        {
            return;
        }

        if (acrylic && !card.Classes.Contains("acrylic"))
        {
            card.Classes.Add("acrylic");
        }
        else if (!acrylic)
        {
            card.Classes.Remove("acrylic");
        }
    }

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
