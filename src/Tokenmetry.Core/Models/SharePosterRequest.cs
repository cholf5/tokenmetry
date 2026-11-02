namespace Tokenmetry.Core.Models;

/// <summary>海报版式：竖版（1080 宽）与横版（1800 宽）。</summary>
public enum SharePosterLayout
{
    /// <summary>竖版：三行两列，适合朋友圈 / 小红书。</summary>
    Portrait,

    /// <summary>横版：两行三列，适合 X / 桌面端预览。</summary>
    Landscape,
}

/// <summary>海报主题。<see cref="FollowApp"/> 沿用应用当前明暗。</summary>
public enum SharePosterTheme
{
    FollowApp,
    Dark,
    Light,
}

/// <summary>项目名公开程度（模糊而非替换，保留"这里有字"的形状）。</summary>
public enum SharePosterProjectMask
{
    /// <summary>原样显示（默认）。</summary>
    Off,

    /// <summary>前几个字符可见，其余模糊。</summary>
    Partial,

    /// <summary>整名模糊。</summary>
    Full,
}

/// <summary>
/// 一次海报生成的输入：版式/主题/雾化档 + 时间范围。筛选维度本身已体现在传给
/// <see cref="SharePosterSnapshot.Build"/> 的查询结果里，不在此重复。
/// </summary>
/// <param name="RangeStart">范围首日（含）。</param>
/// <param name="RangeEnd">范围末日（含）。</param>
public sealed record SharePosterRequest(
    SharePosterLayout Layout,
    SharePosterTheme Theme,
    SharePosterProjectMask ProjectMask,
    DateOnly RangeStart,
    DateOnly RangeEnd)
{
    /// <summary>是否单日窗口（决定走信息窗基底还是总览页基底）。</summary>
    public bool IsSingleDay => RangeStart == RangeEnd;
}
