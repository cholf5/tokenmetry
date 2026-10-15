using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 模型图标槽的厂商附加属性：把行 VM 给出的厂商变体名（<see cref="ViewModels.ModelVisualMap"/>，
/// claude/openai/…/generic）翻译成挂载控件的伪类（model-*），logo/字形由 SharedStyles.axaml 的
/// model-* 样式给出——logo 源以 DynamicResource 引用 ModelLogos.axaml 的 AppModelLogo*
/// （openai/grok 单色官方标按 ActualThemeVariant 明暗自动换黑白）。机制与 <see cref="AgentChip"/>
/// 完全同构（VM 不解析画刷纪律）。
/// </summary>
public static class ModelChip
{
    /// <summary>厂商变体名（null/未知 = 不着色，保持诚实回退）。</summary>
    public static readonly AttachedProperty<string?> VendorProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Vendor", typeof(ModelChip));

    static ModelChip()
    {
        VendorProperty.Changed.AddClassHandler<Control>(OnVendorChanged);
    }

    public static string? GetVendor(Control control) => control.GetValue(VendorProperty);

    public static void SetVendor(Control control, string? value) => control.SetValue(VendorProperty, value);

    private static void OnVendorChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var vendor = e.NewValue as string;
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in ViewModels.ModelVisualMap.Vendors)
        {
            classes.Set($"model-{name}", name == vendor);
        }
    }
}
