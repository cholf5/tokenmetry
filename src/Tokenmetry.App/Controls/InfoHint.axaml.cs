using Avalonia;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 设置项悬停说明（S-004 版面收口）：标签/控件旁一个轻量 ⓘ，悬停显示原本常驻在每项下方的
/// 小字解释。只承载「解释」，状态/校验/隐私与法律告知仍留在页面里常驻显示——那些不是可藏进
/// 悬停的装饰性说明。外观（命中区、图标色、ToolTip 令牌底与折行）全在 SharedStyles 的
/// setting-info / setting-tip 类里，本控件只提供 <see cref="Hint"/> 文案。
/// </summary>
public partial class InfoHint : UserControl
{
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<InfoHint, string?>(nameof(Hint));

    public InfoHint()
    {
        InitializeComponent();
    }

    /// <summary>悬停时展示的说明文案（i18n 后的整句）。</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
