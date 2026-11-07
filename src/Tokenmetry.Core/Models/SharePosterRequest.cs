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
/// 海报样式（多种基底，与时间范围解耦）：信息卡 = 状态栏信息窗的印刷版，固定竖版；
/// 总览 = 总览页的印刷版，竖/横皆可；打卡热力图 = GitHub 贡献图式的按天格子，仅横版。
/// 范围只决定数据窗口，不再决定用哪张基底——生成对话框里的样式选项是唯一出处
/// （打开时缺省跟随范围，用户可改）。
/// </summary>
public enum SharePosterStyle
{
    /// <summary>信息卡：头部 + 三格 KPI + 构成条 + 构成三卡 + Agent 行 + 模型行，无趋势图。</summary>
    Tray,

    /// <summary>总览：页头 + 四张 KPI + 趋势/Agent 环形 + 模型/类型环形 + 项目/成本。</summary>
    Overview,

    /// <summary>打卡热力图：周为列/星期为行的按天格子，token 量映射色阶深浅，空日如实留白（仅横版）。</summary>
    Heatmap,

    /// <summary>时段节律：星期 × 小时热力矩阵（什么时候在用 AI），粗精度用量计入 unknown 小注（仅横版）。</summary>
    Rhythm,
}

/// <summary>
/// 一次海报生成的输入：样式/版式/主题/雾化档 + 时间范围。筛选维度本身已体现在传给
/// <see cref="SharePosterSnapshot.Build"/> 的查询结果里，不在此重复。
/// </summary>
/// <param name="RangeStart">范围首日（含）。</param>
/// <param name="RangeEnd">范围末日（含）。</param>
public sealed record SharePosterRequest(
    SharePosterLayout Layout,
    SharePosterTheme Theme,
    SharePosterProjectMask ProjectMask,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    SharePosterStyle Style)
{
    /// <summary>是否单日窗口（决定趋势图切不切小时形态、范围文案怎么写；与样式无关）。</summary>
    public bool IsSingleDay => RangeStart == RangeEnd;
}
