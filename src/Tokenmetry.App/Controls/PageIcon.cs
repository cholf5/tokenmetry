using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 页面图标的键名附加属性（2026-10-24 界面图标矢量化的导航接线）：把
/// <see cref="ViewModels.PageDescriptor"/> 的图标键名（house/table/…，即 Icons.axaml 的
/// AppIcon* 几何资源后缀）翻译成挂载 Path 的 pi-* 伪类，Data 由 SharedStyles.axaml 的
/// Path.app-icon.pi-* 样式给出——DynamicResource 不支持动态键名，与
/// <see cref="AgentChip.Logo"/> / <see cref="ModelChip"/> 完全同构（VM 不解析资源纪律）。
/// </summary>
public static class PageIcon
{
    /// <summary>图标键名（ShellPages 的 Icon；无匹配 pi-* 样式时 Path 无 Data 而留空）。</summary>
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Icon", typeof(PageIcon));

    static PageIcon()
    {
        IconProperty.Changed.AddClassHandler<Control>(OnIconChanged);
    }

    public static string? GetIcon(Control control) => control.GetValue(IconProperty);

    public static void SetIcon(Control control, string? value) => control.SetValue(IconProperty, value);

    private static void OnIconChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var key = e.NewValue as string;
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in ViewModels.ShellPages.All.Select(p => p.Icon).Distinct())
        {
            classes.Set($"pi-{name}", name == key);
        }
    }
}
