using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

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

    static AgentChip()
    {
        LogoProperty.Changed.AddClassHandler<Control>(OnLogoChanged);
    }

    public static string? GetLogo(Control control) => control.GetValue(LogoProperty);

    public static void SetLogo(Control control, string? value) => control.SetValue(LogoProperty, value);

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
