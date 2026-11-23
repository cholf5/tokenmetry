using Avalonia.Media;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 插件区块的一行（Task 8）：已安装插件（catalog.Installed）或无效目录（catalog.Invalid）。
/// <para>
/// <see cref="IsInvalid"/> 行展示 <see cref="StatusText"/> = <c>PluginInvalidPackageFormat</c>，
/// <see cref="ErrorText"/> 为目录扫描的可读原因，操作按钮在 XAML 侧禁用；插件的显示名 / 作者
/// 原样展示（不翻译，设计 §6.5）。<see cref="Version"/> / <see cref="Author"/> 保存原始值，
/// 展示用字符串由 <see cref="VersionDisplay"/> / <see cref="AuthorDisplay"/> 按当前语言格式化
/// （区块在语言切换时重建行）。
/// </para>
/// </summary>
public sealed record PluginRowViewModel(
    string Id,
    string DisplayName,
    string Version,
    string? Author,
    bool IsEnabled,
    bool IsInvalid,
    string StatusText,
    string? ErrorText,
    IImage? Icon)
{
    /// <summary>「版本 1.0.0」的本地化展示串。</summary>
    public string VersionDisplay => string.Format(Translations.Instance.PluginVersionFormat, Version);

    /// <summary>「作者：Acme（未验证）」；无作者时为空串（XAML 按空串不占行）。</summary>
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author)
        ? string.Empty
        : string.Format(Translations.Instance.PluginAuthorUnverifiedFormat, Author);

    /// <summary>是否有作者可展示（驱动作者行可见性）。</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    /// <summary>无自定义图标时的散列字形兜底（与内置 Agent 的 agent-chip 同款）。</summary>
    public char Glyph => AgentVisualMap.Glyph(DisplayName);
}
