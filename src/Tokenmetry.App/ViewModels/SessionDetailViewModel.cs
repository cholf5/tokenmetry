using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 会话详情弹层 VM（下钻最简版定案）：全部数据取自行点击时已加载的 <see cref="SessionListRow"/>
/// 投影——不发新查询、不重读源文件。砍掉的三样（会话内模型分布、轮次/调用次数、上下文内容构成）
/// 都需要新采集侧表或解析会话内容（后者撞隐私红线），见 2026-11-02 头脑风暴结论。
/// 环图三段复用总览页 Token 类型构成口径（输入未缓存 / 输出 / 缓存相关 = 写 + 读，三段之和
/// = TotalTokens），段色回退纪律同 <see cref="OverviewViewModel.TokenTypeColor"/>；标签与段色
/// 在打开时快照——弹层是瞬态视图，语言切换不热刷。全部未知桶为零 → 环图空集（诚实空态）。
/// </summary>
public sealed class SessionDetailViewModel
{
    public SessionDetailViewModel(SessionRowViewModel row)
    {
        Row = row;
        var t = Translations.Instance;
        DonutSegments = BuildSegments(row.Row, t);
        DonutCenterTitle = TrendChart.FormatCompactTokens(row.Row.TotalTokens);
    }

    private SessionRowViewModel Row { get; }

    private SessionListRow Source => Row.Row;

    /// <summary>主文本：项目展示名（与列表行同口径，空则原始路径再空会话标识）。</summary>
    public string TitleText => Row.TitleText;

    /// <summary>完整会话标识（SelectableTextBlock 呈现，可划选复制）。</summary>
    public string SessionId => Source.SessionId;

    public string Agent => Source.Agent;

    public string IconGlyph => Row.IconGlyph;

    public string LogoVendor => Row.LogoVendor;

    public string ModelText => Row.ModelText;

    /// <summary>首次活动（本地时间绝对值；相对时间留给列表列，详情页要可对账的精确时间）。</summary>
    public string FirstSeenText => FormatAbsoluteTime(Source.FirstSeenAt);

    /// <summary>最后活动（本地时间绝对值）。</summary>
    public string LastSeenText => FormatAbsoluteTime(Source.LastSeenAt);

    /// <summary>估算成本文案（未知成本不折 0，同列表列四态）。</summary>
    public string CostText => SessionRowViewModel.FormatCost(Source.CostUsd);

    // ── Token 分项（N0 千分位精确值，对账口径同明细表数字列）──

    public string InputText => FormatTokens(Source.InputTokens);

    public string OutputText => FormatTokens(Source.OutputTokens);

    public string CacheWriteText => FormatTokens(Source.CacheCreationTokens);

    public string CacheReadText => FormatTokens(Source.CacheReadTokens);

    public string TotalText => FormatTokens(Source.TotalTokens);

    /// <summary>5m/1h ephemeral 细分仅 Claude 系日志携带（0 = 无该细分语义，不展示细分行）。</summary>
    public bool Has5mSplit => Source.CacheCreationEphemeral5mTokens > 0;

    /// <summary>同 <see cref="Has5mSplit"/>。</summary>
    public bool Has1hSplit => Source.CacheCreationEphemeral1hTokens > 0;

    public string Split5mText => FormatTokens(Source.CacheCreationEphemeral5mTokens);

    public string Split1hText => FormatTokens(Source.CacheCreationEphemeral1hTokens);

    /// <summary>Token 类型构成三段（输入未缓存 / 输出 / 缓存相关），全零 → 空集。</summary>
    public IReadOnlyList<DonutSegment> DonutSegments { get; }

    /// <summary>环心主文字：紧凑总量（K/M 或 万/亿，随语言）。</summary>
    public string DonutCenterTitle { get; }

    private static IReadOnlyList<DonutSegment> BuildSegments(SessionListRow row, Translations t)
    {
        var cache = row.CacheCreationTokens + row.CacheReadTokens;
        if (row.InputTokens + row.OutputTokens + cache == 0)
        {
            return Array.Empty<DonutSegment>();
        }

        return new[]
        {
            new DonutSegment(t.TokenTypeInput, row.InputTokens, TokenTypeColor(
                "AppTokenTypeInputColor", 0x4c, 0x8d, 0xff)),
            new DonutSegment(t.TokenTypeOutput, row.OutputTokens, TokenTypeColor(
                "AppTokenTypeOutputColor", 0xa7, 0x79, 0xff)),
            new DonutSegment(t.TokenTypeCache, cache, TokenTypeColor(
                "AppTokenTypeCacheColor", 0x38, 0xd6, 0xa4)),
        };
    }

    private static string FormatTokens(long tokens) =>
        tokens.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatAbsoluteTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>类型构成段色：令牌缺资源时回退原型 typeDonut 原值（与 OverviewViewModel 同纪律）。</summary>
    private static Color TokenTypeColor(string key, byte r, byte g, byte b) =>
        Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true
            && value is Color color
                ? color
                : Color.FromRgb(r, g, b);
}
