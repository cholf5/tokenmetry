using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 模型图标槽的头像附加属性：把行 VM 给出的 <see cref="ViewModels.ModelAvatarVisual"/>（厂商变体名 /
/// 字母 / 色块 / 上传图 + 自动推断的收敛结果，见 <see cref="ViewModels.ModelAvatarVisual"/>）翻译成
/// 挂载控件的一组伪类——厂商 logo 走 SharedStyles 的 model-* 行内官方 logo（DynamicResource 给出，
/// VM 不解析画刷纪律同 AgentChip），字母 / 色块 / 上传图走 model-letter / model-color / model-image
/// 伪类驱动的对应层；色块的背景由本属性就地着色（数据是用户固定 hex，不随明暗翻转，属内容非主题）。
/// </summary>
public static class ModelChip
{
    /// <summary>头像视觉（null = 全部伪类清空，保持诚实回退）。</summary>
    public static readonly AttachedProperty<ViewModels.ModelAvatarVisual?> AvatarProperty =
        AvaloniaProperty.RegisterAttached<Control, ViewModels.ModelAvatarVisual?>("Avatar", typeof(ModelChip));

    static ModelChip()
    {
        AvatarProperty.Changed.AddClassHandler<Control>(OnAvatarChanged);
    }

    public static ViewModels.ModelAvatarVisual? GetAvatar(Control control) => control.GetValue(AvatarProperty);

    public static void SetAvatar(Control control, ViewModels.ModelAvatarVisual? value) => control.SetValue(AvatarProperty, value);

    private static void OnAvatarChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var avatar = e.NewValue as ViewModels.ModelAvatarVisual;
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in ViewModels.ModelVisualMap.Vendors)
        {
            classes.Set($"model-{name}", avatar?.Vendor == name);
        }

        classes.Set("model-letter", avatar?.Kind == ModelAvatarKind.Letter);
        classes.Set("model-color", avatar?.Kind == ModelAvatarKind.Color);
        classes.Set("model-image", avatar?.Kind == ModelAvatarKind.Image);

        // 色块头像 = 芯片自身的背景（本地值压得过 model-chip-lg 的中性软底样式 setter）；
        // 其余模式若此前被本属性着过色则清除（ClearValue 后样式值重新生效），不动用户 XAML 的本地背景。
        if (control is Border border
            && avatar?.Kind == ModelAvatarKind.Color
            && avatar.ColorHex is { } hex
            && TryParseColor(hex, out var color))
        {
            border.Background = new SolidColorBrush(color);
        }
        else if (control is Border && control.IsSet(Border.BackgroundProperty))
        {
            control.ClearValue(Border.BackgroundProperty);
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
    }
}
