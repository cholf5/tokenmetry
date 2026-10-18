using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Tokenmetry.App.Tray;

/// <summary>
/// D-016 弹窗壳（无边框 / 透明 / 失焦收起 / Esc 收起）：交互分工 = 托盘左键 Toggle 弹层、
/// 右键既有菜单（macOS interop 路径：右键原生 NSMenu；Avalonia 路径：NativeMenu）；内容层 100%
/// 来自 D-015（DataContext 即 StatusPopoverViewModel）。
/// <para>投影形态（协商失败回落态）：窗口宽 = 卡片 + 两侧投影留白，<see cref="ShadowMargins"/> 是
/// BoxShadow 的渲染留白（控制器锚定换算的单一来源）；投影色走令牌 AppPopoverShadowColor（随明暗
/// 翻转）——XAML 无法把 BoxShadow 的颜色部分交给 DynamicResource，故在代码 Behind 按
/// ActualThemeVariant 取值应用（视图零硬编码色纪律由令牌层承接）。</para>
/// <para>毛玻璃形态（B5/B6，协商成功即 ActualTransparencyLevel == AcrylicBlur）：窗口收缩到卡片
/// 大小（B6——消除"窗口矩形铺满毛玻璃板 + 卡片浮在上面"的两层皮 fringe），去 BoxShadow，卡片切
/// 半透明面（popover-card.acrylic 类），圆角交给平台原生：mac interop 裁 blur 视图 15px
/// （MacPopoverBlurInterop）、Win11 DWM 原生 8px（Win10 无该属性保持方形 = Win10 原生 flyout 观感）。
/// 协商失败（系统透明效果关闭 / RDP / 旧 Win10 / 平台不支持）回落投影形态，观感与 B5 之前一致。</para>
/// </summary>
public partial class StatusPopoverWindow : Window
{
    /// <summary>投影渲染留白（左,上,右,下）：可见卡片 = 窗口内容矩形内缩该边距。
    /// 上留白 6 = macOS AppKit constrainFrameRect 把窗口顶压到菜单栏底之后的贴合间隙
    /// （弹窗"贴着菜单栏下方 6px 弹出"），同时给投影留出少量向上晕染。
    /// 毛玻璃形态不使用（窗口贴合卡片，见 <see cref="EffectiveShadowMargins"/>）。</summary>
    internal static readonly Thickness ShadowMargins = new(30, 6, 30, 34);

    /// <summary>内容卡宽（StatusPopoverView 的 popover-card 同值）：毛玻璃形态窗口宽 = 卡宽。</summary>
    private const double CardWidth = 438;
    private const double ShadowedWindowWidth = CardWidth + 60;

    /// <summary>macOS blur 视图层圆角半径（与内容卡 CornerRadius 同值，需同步改）。</summary>
    private const double AcrylicCornerRadius = 15;

    public StatusPopoverWindow()
    {
        InitializeComponent();
        ShadowHost.Margin = ShadowMargins;
        Deactivated += (_, _) => Hide();
        Activated += OnActivated;
        ApplyAcrylic(); // XAML inflate 期 hint 即设即报（B5 踩坑留证），ctor 末尾协商结果已可用
    }

    /// <summary>激活后再成形一次：平台可能在 Show/激活序列里重申透明模式（重新垫
    /// windowBackgroundColor，B6 四角灰底的嫌疑路径），此处幂等重跑把垫色压回透明。</summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (IsAcrylic)
        {
            ApplyAcrylic();
        }
    }

    /// <summary>控制器锚定用边距：毛玻璃形态窗口已贴合卡片（零边距），否则用投影留白。</summary>
    internal Thickness EffectiveShadowMargins => IsAcrylic ? default : ShadowMargins;

    internal bool IsAcrylic => ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;

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
        ApplyAcrylic(); // 已上屏：mac blur 视图 layer 此刻就绪，interop 成形在这一步落地
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ActualTransparencyLevelProperty)
        {
            // 透明度协商在平台窗首次 Show 时定，运行中系统设置（如透明效果开关）变化也走这里
            ApplyAcrylic();
        }
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!IsAcrylic)
        {
            ApplyShadow();
        }
    }

    /// <summary>毛玻璃形态切换（幂等）：切卡片类、窗口几何、投影与平台圆角。失败回落投影形态，
    /// 对比度不破；卡片面本体是内容层样式（popover-card.acrylic），壳层只切类与几何。</summary>
    private void ApplyAcrylic()
    {
        if (ShadowHost is null)
        {
            return; // XAML inflate 期 ActualTransparencyLevel 即可能到达（B5 踩坑留证），字段未就绪跳过
        }

        var acrylic = IsAcrylic;
        var card = ShadowHost.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("popover-card"));
        if (card is { } c)
        {
            SetClass(c, "acrylic", acrylic);
            SetClass(c, "acrylic-square", acrylic && OperatingSystem.IsWindows());
        }

        ShadowHost.Margin = acrylic ? default : ShadowMargins;
        Width = acrylic ? CardWidth : ShadowedWindowWidth;
        if (acrylic)
        {
            ShadowHost.BoxShadow = default; // 毛玻璃 + 原生圆角自带深度（CodexBar 同款无绘制投影）
            if (OperatingSystem.IsMacOS())
            {
                MacPopoverBlurInterop.TryShapeBlurBehind(this, AcrylicCornerRadius);
            }
            else if (OperatingSystem.IsWindows())
            {
                TryApplyDwmRoundCorners();
            }
        }
        else
        {
            ApplyShadow();
        }
    }

    private static void SetClass(Border card, string name, bool on)
    {
        if (on && !card.Classes.Contains(name))
        {
            card.Classes.Add(name);
        }
        else if (!on)
        {
            card.Classes.Remove(name);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

    /// <summary>Win11 原生圆角（DWMWA_WINDOW_CORNER_PREFERENCE=33 + DWMWCP_ROUND=2，系统半径）。
    /// Win10 无此属性（返回错误码忽略）→ 方形即 Win10 原生 flyout 观感，不另造圆角。</summary>
    private void TryApplyDwmRoundCorners()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1")
            {
                return; // headless 宿主句柄不是真 HWND（B2 双轨纪律）
            }

            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch
        {
            // DWM 不可用（旧系统/RDP）：方形兜底
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
