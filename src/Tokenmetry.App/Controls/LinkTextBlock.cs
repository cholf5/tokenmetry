using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 句中嵌一个链接词的 TextBlock：Avalonia 没有 WPF 式内联 Hyperlink（12.x 仍只有 HyperlinkButton
/// 控件，嵌不进文本流），本控件按 <see cref="LinkText"/> 把 <see cref="SourceText"/> 拆三段渲染，
/// 链接段吃 AppLinkBrush + 下划线，命中测试点落在链接段内才经系统浏览器打开 <see cref="LinkUrl"/>。
/// 整句是真实 TextBlock 排版，换行与实时重译（SourceText 绑定 Translations 随语言切换重建）都按
/// 普通语义工作。StyleKey 指回 TextBlock，让 TextBlock.* 的共享样式（如 setting-hint）照常命中。
/// </summary>
public class LinkTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<LinkTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<string?> LinkTextProperty =
        AvaloniaProperty.Register<LinkTextBlock, string?>(nameof(LinkText));

    public static readonly StyledProperty<string?> LinkUrlProperty =
        AvaloniaProperty.Register<LinkTextBlock, string?>(nameof(LinkUrl));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private bool _hoveringLink;

    static LinkTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<LinkTextBlock>((control, _) => control.RebuildInlines());
        LinkTextProperty.Changed.AddClassHandler<LinkTextBlock>((control, _) => control.RebuildInlines());
    }

    /// <summary>整句文案（i18n 后的完整句子）。</summary>
    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }

    /// <summary>句中被渲染成链接的词（GitHub 专名「Star」全语种恒同形，TranslationsTests 有不变式）。</summary>
    public string? LinkText { get => GetValue(LinkTextProperty); set => SetValue(LinkTextProperty, value); }

    /// <summary>链接段点击后经系统浏览器打开的地址。</summary>
    public string? LinkUrl { get => GetValue(LinkUrlProperty); set => SetValue(LinkUrlProperty, value); }

    /// <summary>链接段在整句里的字符区间（重建 inlines 时更新；句中无链接词时为 -1）。</summary>
    public int LinkStart { get; private set; } = -1;

    public int LinkLength { get; private set; }

    /// <summary>StyleKey 指回 TextBlock：<c>TextBlock.setting-hint</c> 等类型限定选择器才能命中本控件。</summary>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    private void RebuildInlines()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        var source = SourceText ?? string.Empty;
        var link = LinkText ?? string.Empty;
        var index = link.Length > 0 ? source.IndexOf(link, StringComparison.Ordinal) : -1;
        (LinkStart, LinkLength) = index < 0 ? (-1, 0) : (index, link.Length);

        if (index < 0)
        {
            // 没找到链接词（按理会被 TranslationsTests 的不变式拦住）：整句按普通文本渲染，不崩
            if (source.Length > 0)
            {
                Inlines.Add(new Run(source));
            }

            return;
        }

        if (index > 0)
        {
            Inlines.Add(new Run(source[..index]));
        }

        // 不能写裸 TextDecorations.Underline：TextElement 的同名实例属性（TextDecorationCollection）
        // 会把静态类名遮蔽掉，.Underline 直接 CS1061
        var linkRun = new Run(link) { TextDecorations = Avalonia.Media.TextDecorations.Underline };
        linkRun.Bind(Run.ForegroundProperty, this.GetResourceObservable("AppLinkBrush"));
        Inlines.Add(linkRun);

        var after = source[(index + link.Length)..];
        if (after.Length > 0)
        {
            Inlines.Add(new Run(after));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsOverLink(e.GetCurrentPoint(this).Position))
        {
            return;
        }

        e.Handled = true;
        if (Uri.TryCreate(LinkUrl, UriKind.Absolute, out var uri))
        {
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovering(IsOverLink(e.GetCurrentPoint(this).Position));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovering(false);
    }

    private void SetHovering(bool hovering)
    {
        if (hovering == _hoveringLink)
        {
            return;
        }

        _hoveringLink = hovering;
        Cursor = hovering ? HandCursor : null;
    }

    private bool IsOverLink(Point position)
    {
        return LinkStart >= 0
            && TextLayout is { } layout
            && layout.HitTestPoint(position) is { } hit
            && hit.TextPosition >= LinkStart
            && hit.TextPosition < LinkStart + LinkLength;
    }
}
