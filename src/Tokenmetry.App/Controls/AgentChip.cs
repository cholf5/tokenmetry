using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tokenmetry.App.Controls;

/// <summary>
/// Agent 图标槽的品牌附加属性（I-034）：把行 VM 给出的品牌变体名
/// （<see cref="ViewModels.AgentVisualMap"/>：claudecode/codex/…/dsh/generic）翻译成挂载控件的
/// 伪类（agent-*），logo/字形由 SharedStyles.axaml 的 agent-* 样式给出——logo 源以 DynamicResource
/// 引用 AgentLogos.axaml 的 AppAgentLogo*（单色官方标按 ActualThemeVariant 明暗自动换黑白）。
/// 机制与 <see cref="ModelChip"/> 完全同构（VM 不解析画刷纪律，同 D-008 审查修复轮 P1 的教训）。
///
/// 历史（D-008）：本类原为 <c>Variant</c> 附加属性，把 blue/green/purple/neutral 软色底变体名
/// 翻译成 chip-* 伪类；I-034 改为品牌 logo 后色块与变体一并退场（散列色对一个 Agent 无语义），
/// 未识别 Agent 落 generic 伪类 → 中性前景的散列字形兜底。
/// </summary>
public static class AgentChip
{
    /// <summary>品牌变体名（null/未知 = 不着色，保持诚实回退）。</summary>
    public static readonly AttachedProperty<string?> LogoProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Logo", typeof(AgentChip));

    /// <summary>
    /// 插件自定义图标（Task 7）：非 null 时给 chip 挂 <c>agent-custom</c> 伪类，SharedStyles 随之把
    /// 内层 <c>Image.agent-logo</c> 的 Source 绑到本属性并隐藏字形。null（内置 Agent 全部如此）时
    /// 伪类清除，brand 的 agent-* 样式照常生效——本属性绝不覆盖内置 logo。
    /// </summary>
    public static readonly AttachedProperty<IImage?> CustomIconProperty =
        AvaloniaProperty.RegisterAttached<Control, IImage?>("CustomIcon", typeof(AgentChip));

    static AgentChip()
    {
        LogoProperty.Changed.AddClassHandler<Control>(OnLogoChanged);
        CustomIconProperty.Changed.AddClassHandler<Control>(OnCustomIconChanged);
    }

    public static string? GetLogo(Control control) => control.GetValue(LogoProperty);

    public static void SetLogo(Control control, string? value) => control.SetValue(LogoProperty, value);

    public static IImage? GetCustomIcon(Control control) => control.GetValue(CustomIconProperty);

    public static void SetCustomIcon(Control control, IImage? value) =>
        control.SetValue(CustomIconProperty, value);

    private static void OnCustomIconChanged(Control control, AvaloniaPropertyChangedEventArgs e) =>
        ((IPseudoClasses)control.Classes).Set("agent-custom", e.NewValue is IImage);

    private static void OnLogoChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var vendor = e.NewValue as string;
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in ViewModels.AgentVisualMap.Vendors)
        {
            classes.Set($"agent-{name}", name == vendor);
        }
    }
}
