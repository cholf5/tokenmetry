using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 海报选项的展示包装（版式 / 主题 / 雾化档共用）：<see cref="Value"/> 是落 settings.json 的稳定键，
/// <see cref="Label"/> 是随语言切换重译的文案。机制同 <see cref="ThemeOption"/> / <see cref="LanguageOption"/>
/// ——VM 不解析文案，XAML 只绑 Label；语言切换由持有方调 <see cref="RefreshLabel"/>（ComboBox 的
/// 选中项是对象实例，文案变化要显式发通知）。
/// </summary>
public sealed class SharePosterOption : ObservableObject
{
    private readonly Func<string> _label;

    public SharePosterOption(string value, Func<string> label)
    {
        Value = value;
        _label = label;
    }

    /// <summary>稳定键（settings.json 里的取值，如 "portrait" / "dark" / "partial"）。</summary>
    public string Value { get; }

    /// <summary>当前语言下的显示文案（每次取都读最新字典）。</summary>
    public string Label => _label();

    private bool _isSelected;

    /// <summary>
    /// 生成对话框的分段控件选中态（RadioButton 双向绑定）。只有对话框自己创建的选项实例会用到；
    /// 设置页共享的静态实例从不读写它——否则设置页与对话框会互相改选中态。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 海报选项的单一出处：设置页三组默认档（版式/主题/雾化）与生成对话框共用同一批实例，
/// 值 ↔ 枚举的双向映射也在这里，避免两处各写一套解析。样式（信息卡/总览）不进设置页，
/// 只有 <see cref="CreateStyles"/> 一条入口（对话框每次打开新建实例）。未知/脏值一律如实回落
/// 默认（同 AppSettings.Theme 的处理）。
/// </summary>
public static class SharePosterOptions
{
    /// <summary>版式：竖版（默认）/ 横版。</summary>
    public static IReadOnlyList<SharePosterOption> Layouts { get; } =
    [
        new("portrait", () => Translations.Instance.SharePosterLayoutPortrait),
        new("landscape", () => Translations.Instance.SharePosterLayoutLandscape),
    ];

    /// <summary>主题：跟随应用（默认）/ 夜间 / 日间。</summary>
    public static IReadOnlyList<SharePosterOption> Themes { get; } =
    [
        new("app", () => Translations.Instance.SharePosterThemeApp),
        new("dark", () => Translations.Instance.SharePosterThemeDark),
        new("light", () => Translations.Instance.SharePosterThemeLight),
    ];

    /// <summary>项目名雾化：显示（默认）/ 局部 / 全部。</summary>
    public static IReadOnlyList<SharePosterOption> Masks { get; } =
    [
        new("off", () => Translations.Instance.SharePosterMaskOff),
        new("partial", () => Translations.Instance.SharePosterMaskPartial),
        new("full", () => Translations.Instance.SharePosterMaskFull),
    ];

    /// <summary>
    /// 样式（信息卡 / 总览）：只有对话框用（设置页不存样式默认档——缺省跟随打开时的范围，
    /// 用户在对话框里改）。与版式是两个维度：信息卡固定竖版，总览竖/横皆可。
    /// </summary>
    public static IReadOnlyList<SharePosterOption> CreateStyles() =>
    [
        new("tray", () => Translations.Instance.SharePosterStyleTray),
        new("overview", () => Translations.Instance.SharePosterStyleOverview),
    ];

    /// <summary>生成对话框专用：每次打开新建一批实例（带独立 IsSelected，不与设置页共享选中态）。</summary>
    public static IReadOnlyList<SharePosterOption> CreateLayouts() =>
    [
        new("portrait", () => Translations.Instance.SharePosterLayoutPortrait),
        new("landscape", () => Translations.Instance.SharePosterLayoutLandscape),
    ];

    /// <summary>生成对话框专用（理由同 <see cref="CreateLayouts"/>）。</summary>
    public static IReadOnlyList<SharePosterOption> CreateThemes() =>
    [
        new("app", () => Translations.Instance.SharePosterThemeApp),
        new("dark", () => Translations.Instance.SharePosterThemeDark),
        new("light", () => Translations.Instance.SharePosterThemeLight),
    ];

    /// <summary>生成对话框专用（理由同 <see cref="CreateLayouts"/>）。</summary>
    public static IReadOnlyList<SharePosterOption> CreateMasks() =>
    [
        new("off", () => Translations.Instance.SharePosterMaskOff),
        new("partial", () => Translations.Instance.SharePosterMaskPartial),
        new("full", () => Translations.Instance.SharePosterMaskFull),
    ];

    /// <summary>
    /// 每样式声明自己支持的版式（能力表，顺序即收口优先级）：对话框按它启用/禁用版式组、
    /// 把非法组合收口到首个受支持版式——新样式在此登记一行即可，对话框不再叠样式特判。
    /// <para>
    /// 信息卡只支持竖版：它本质是一张 438px 竖卡片，横置要重排信息流，已超出"不偏离框架"
    /// 的界线（设计文档 §3.1）；总览竖/横皆可——与时间范围无关。
    /// </para>
    /// </summary>
    public static IReadOnlyList<SharePosterLayout> SupportedLayouts(SharePosterStyle style) => style switch
    {
        SharePosterStyle.Overview => [SharePosterLayout.Portrait, SharePosterLayout.Landscape],
        SharePosterStyle.Tray => [SharePosterLayout.Portrait],
        _ => [SharePosterLayout.Portrait],
    };

    /// <summary>
    /// 样式只支持一种版式时的说明文案（告诉用户为什么，而不是静默禁用）；支持多种版式的
    /// 样式为 null（无说明行）。与 <see cref="SupportedLayouts"/> 一同登记。
    /// </summary>
    public static string? LayoutNoticeOf(SharePosterStyle style) => style switch
    {
        SharePosterStyle.Tray => Translations.Instance.SharePosterTrayPortraitOnly,
        _ => null,
    };

    /// <summary>语言切换后重译全部标签（持有方 = 设置页单例；对话框是短命对象，构造即取当前语言）。</summary>
    public static void RefreshLabels()
    {
        foreach (var option in Layouts.Concat(Themes).Concat(Masks))
        {
            option.RefreshLabel();
        }
    }

    public static SharePosterLayout ParseLayout(string? value) =>
        Normalize(value) == "landscape" ? SharePosterLayout.Landscape : SharePosterLayout.Portrait;

    public static SharePosterTheme ParseTheme(string? value) => Normalize(value) switch
    {
        "dark" => SharePosterTheme.Dark,
        "light" => SharePosterTheme.Light,
        _ => SharePosterTheme.FollowApp,
    };

    public static SharePosterProjectMask ParseMask(string? value) => Normalize(value) switch
    {
        "partial" => SharePosterProjectMask.Partial,
        "full" => SharePosterProjectMask.Full,
        _ => SharePosterProjectMask.Off,
    };

    public static SharePosterStyle ParseStyle(string? value) =>
        Normalize(value) == "overview" ? SharePosterStyle.Overview : SharePosterStyle.Tray;

    public static string KeyOf(SharePosterLayout layout) =>
        layout == SharePosterLayout.Landscape ? "landscape" : "portrait";

    public static string KeyOf(SharePosterTheme theme) => theme switch
    {
        SharePosterTheme.Dark => "dark",
        SharePosterTheme.Light => "light",
        _ => "app",
    };

    public static string KeyOf(SharePosterProjectMask mask) => mask switch
    {
        SharePosterProjectMask.Partial => "partial",
        SharePosterProjectMask.Full => "full",
        _ => "off",
    };

    public static string KeyOf(SharePosterStyle style) =>
        style == SharePosterStyle.Overview ? "overview" : "tray";

    /// <summary>枚举 → 选项实例（ComboBox 的 SelectedItem 必须是同一批实例）。</summary>
    public static SharePosterOption ForLayout(SharePosterLayout layout) =>
        Layouts.First(o => o.Value == KeyOf(layout));

    public static SharePosterOption ForTheme(SharePosterTheme theme) =>
        Themes.First(o => o.Value == KeyOf(theme));

    public static SharePosterOption ForMask(SharePosterProjectMask mask) =>
        Masks.First(o => o.Value == KeyOf(mask));

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
