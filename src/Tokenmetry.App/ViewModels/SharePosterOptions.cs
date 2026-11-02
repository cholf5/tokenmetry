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

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 海报三组选项的单一出处（设置页与生成对话框共用同一批实例）：值 ↔ 枚举的双向映射也在这里，
/// 避免两处各写一套解析。未知/脏值一律如实回落默认（同 AppSettings.Theme 的处理）。
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

    /// <summary>枚举 → 选项实例（ComboBox 的 SelectedItem 必须是同一批实例）。</summary>
    public static SharePosterOption ForLayout(SharePosterLayout layout) =>
        Layouts.First(o => o.Value == KeyOf(layout));

    public static SharePosterOption ForTheme(SharePosterTheme theme) =>
        Themes.First(o => o.Value == KeyOf(theme));

    public static SharePosterOption ForMask(SharePosterProjectMask mask) =>
        Masks.First(o => o.Value == KeyOf(mask));

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
