using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CCUsage.App.ViewModels;

/// <summary>外壳导航的 8 个固定页面（D-003，与原型 .nav data-page 一一对应）。</summary>
public enum PageId
{
    Overview,
    Sessions,
    Agents,
    Models,
    Projects,
    Time,
    Cost,
    Settings,
}

/// <summary>页面描述：导航标签 + 占位页展示信息（真实落地卡号与规划内容，供占位视图展示，不含假数据）。</summary>
public sealed record PageDescriptor(
    PageId Id,
    string Label,
    string Icon,
    string Title,
    string Subtitle,
    string CardRef,
    IReadOnlyList<string> PlannedSections);

/// <summary>8 个页面的固定注册表（顺序即导航顺序，文案对齐原型 .nav / .pagehead）。</summary>
public static class ShellPages
{
    public static IReadOnlyList<PageDescriptor> All { get; } =
    [
        new(PageId.Overview, "总览", "⌂",
            "Token 使用概览",
            "统一查看不同 Agent 的使用量、成本与工作负载",
            "D-006 / D-007",
            ["KPI 指标行（Token · 成本 · 会话数 · 缓存读取占比）",
             "Token 使用趋势（按日堆叠柱 + 折线）",
             "Agent 使用占比环形图",
             "模型分布 · Token 构成 · 项目排行 · 成本趋势"]),
        new(PageId.Sessions, "会话明细", "▤",
            "会话明细",
            "从汇总下钻到单次 Agent 会话，支持搜索与组合筛选",
            "D-008",
            ["会话搜索与 Agent / 排序组合筛选",
             "会话明细表（Token · 估算成本 · 最后活动）",
             "与右侧最近会话栏联动"]),
        new(PageId.Agents, "Agent 统计", "◉",
            "Agent 统计",
            "比较不同工具的使用强度、成本与会话规模",
            "D-009",
            ["Agent KPI 行",
             "Agent 排行表（会话数 · 输入 / 输出 Token · 成本 · 占比）"]),
        new(PageId.Models, "模型统计", "⬡",
            "模型统计",
            "模型维度的使用量与成本，不把价格差异隐藏在总量中",
            "D-010",
            ["模型明细表（输入 · 输出 · 缓存读取 · 成本）",
             "价格缺失时成本显示「未知」而非 0"]),
        new(PageId.Projects, "项目统计", "▣",
            "项目统计",
            "按工作目录聚合，观察 Token 消耗集中在哪些代码库或任务",
            "D-011",
            ["项目 / 目录排行表（会话数 · Token · 成本 · 占比）",
             "路径归一化后的项目名"]),
        new(PageId.Time, "时间分析", "◷",
            "时间分析",
            "识别使用高峰、工作时段分布与长会话",
            "D-012",
            ["按小时分布图",
             "活跃日历热力图",
             "高峰时段 · 会话长度 · 数据边界观察项"]),
        new(PageId.Cost, "成本分析", "＄",
            "成本分析",
            "区分真实计费、模型价格估算与无法估价的数据",
            "D-013",
            ["成本 KPI 行（估算总成本 · 输入 / 输出占比 · 待确认价格）",
             "计费口径说明（实际计费 · 价格表估算 · 未知成本）"]),
        new(PageId.Settings, "数据源与设置", "⚙",
            "数据源与设置",
            "多 Agent 统一统计的关键是来源适配、去重和口径透明",
            "S-002",
            []),
    ];

    public static PageDescriptor For(PageId id) => All.First(p => p.Id == id);
}

/// <summary>侧栏导航项 VM：图标 + 标签。视图选中态由 ListBox 的 SelectedItem 与
/// ListBoxItem:selected 伪类样式驱动（单一真相源，D-003 审查后移除冗余 IsActive 状态）。</summary>
public partial class NavigationItemViewModel : ViewModelBase
{
    public PageDescriptor Page { get; }

    public NavigationItemViewModel(PageDescriptor page)
    {
        Page = page;
    }

    public static IReadOnlyList<NavigationItemViewModel> CreateDefaults() =>
        ShellPages.All.Select(p => new NavigationItemViewModel(p)).ToList();
}
