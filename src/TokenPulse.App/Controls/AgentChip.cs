using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace TokenPulse.App.Controls;

/// <summary>
/// Agent 软色底芯片的变体附加属性（D-008 审查修复轮 P1）：把行 VM 给出的变体名
/// （<see cref="ViewModels.AgentVisualMap.ChipVariant"/>：blue/green/purple/neutral）翻译成
/// 挂载控件的伪类（chip-*），颜色一律由 SharedStyles.axaml 的 chip-* 样式以 DynamicResource
/// 引用 AppIconChip*Hue 令牌——按控件 ActualThemeVariant 解析，明暗翻转与窗口级
/// RequestedThemeVariant 自动生效。
/// 为什么不用 VM 直接解析画刷：AppIconChip* 只在 Tokens.axaml 的 Light/Dark 主题字典
/// （无 Default 兜底），VM 侧 <c>TryGetResource(key, ThemeVariant.Default)</c> 软失败返回 null
/// （原 P1 缺陷）；而窗口级主题覆盖（明暗截图）也无法经 Application 级解析正确呈现。
/// </summary>
public static class AgentChip
{
    /// <summary>变体名（blue/green/purple/neutral；null/未知 = 不着色，保持诚实回退）。</summary>
    public static readonly AttachedProperty<string?> VariantProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Variant", typeof(AgentChip));

    private static readonly string[] Variants = ["blue", "green", "purple", "neutral"];

    static AgentChip()
    {
        VariantProperty.Changed.AddClassHandler<Control>(OnVariantChanged);
    }

    public static string? GetVariant(Control control) => control.GetValue(VariantProperty);

    public static void SetVariant(Control control, string? value) => control.SetValue(VariantProperty, value);

    private static void OnVariantChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var variant = e.NewValue as string;
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in Variants)
        {
            classes.Set($"chip-{name}", name == variant);
        }
    }
}
