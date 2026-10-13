using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.App.Localization;

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

/// <summary>
/// 页面描述：导航标签 + 占位页展示信息（真实落地卡号与规划内容，供占位视图展示，不含假数据）。
/// Label/Title/Subtitle 不再内联字符串，按 <see cref="Id"/> 经 Translations 实时取串——
/// 语言切换后由持有方（导航项 / 页面 VM）raise 通知重新求值（导航项见
/// <see cref="NavigationItemViewModel"/>；页面 VM 每次导航新建，天然读到当前语言）。
/// PlannedSections 仅占位页渲染（当前 8 页全部有真实 VM，占位路径不可达），保持中文原文不接 i18n。
/// </summary>
public sealed record PageDescriptor(
    PageId Id,
    string Icon,
    string CardRef,
    IReadOnlyList<string> PlannedSections)
{
    public string Label => Id switch
    {
        PageId.Overview => Translations.Instance.NavOverview,
        PageId.Sessions => Translations.Instance.NavSessions,
        PageId.Agents => Translations.Instance.NavAgents,
        PageId.Models => Translations.Instance.NavModels,
        PageId.Projects => Translations.Instance.NavProjects,
        PageId.Time => Translations.Instance.NavTime,
        PageId.Cost => Translations.Instance.NavCost,
        _ => Translations.Instance.NavSettings,
    };

    public string Title => Id switch
    {
        PageId.Overview => Translations.Instance.OverviewTitle,
        PageId.Sessions => Translations.Instance.SessionsTitle,
        PageId.Agents => Translations.Instance.AgentsTitle,
        PageId.Models => Translations.Instance.ModelsTitle,
        PageId.Projects => Translations.Instance.ProjectsTitle,
        PageId.Time => Translations.Instance.TimeTitle,
        PageId.Cost => Translations.Instance.CostTitle,
        _ => Translations.Instance.SettingsTitle,
    };

    public string Subtitle => Id switch
    {
        PageId.Overview => Translations.Instance.OverviewSubtitle,
        PageId.Sessions => Translations.Instance.SessionsSubtitle,
        PageId.Agents => Translations.Instance.AgentsSubtitle,
        PageId.Models => Translations.Instance.ModelsSubtitle,
        PageId.Projects => Translations.Instance.ProjectsSubtitle,
        PageId.Time => Translations.Instance.TimeSubtitle,
        PageId.Cost => Translations.Instance.CostSubtitle,
        _ => Translations.Instance.SettingsSubtitle,
    };
}

/// <summary>8 个页面的固定注册表（顺序即导航顺序）。</summary>
public static class ShellPages
{
    public static IReadOnlyList<PageDescriptor> All { get; } =
    [
        new(PageId.Overview, "⌂",
            "D-006 / D-007",
            ["KPI 指标行（Token · 成本 · 会话数 · 缓存读取占比）",
             "Token 使用趋势（按日堆叠柱 + 折线）",
             "Agent 使用占比环形图",
             "模型分布 · Token 构成 · 项目排行 · 成本趋势"]),
        new(PageId.Sessions, "▤",
            "D-008",
            ["会话搜索与 Agent / 排序组合筛选",
             "会话明细表（Token · 估算成本 · 最后活动）",
             "与右侧最近会话栏联动"]),
        new(PageId.Agents, "◉",
            "D-009",
            ["Agent KPI 行",
             "Agent 排行表（会话数 · 输入 / 输出 Token · 成本 · 占比）"]),
        new(PageId.Models, "⬡",
            "D-010",
            ["模型明细表（输入 · 输出 · 缓存读取 · 成本）",
             "价格缺失时成本显示「未知」而非 0"]),
        new(PageId.Projects, "▣",
            "D-011",
            ["项目 / 目录排行表（会话数 · Token · 成本 · 占比）",
             "路径归一化后的项目名"]),
        new(PageId.Time, "◷",
            "D-012",
            ["按小时分布图",
             "活跃日历热力图",
             "高峰时段 · 会话长度 · 数据边界观察项"]),
        new(PageId.Cost, "＄",
            "D-013",
            ["成本 KPI 行（估算总成本 · 输入 / 输出占比 · 待确认价格）",
             "计费口径说明（实际计费 · 价格表估算 · 未知成本）"]),
        new(PageId.Settings, "⚙",
            "S-002",
            []),
    ];

    public static PageDescriptor For(PageId id) => All.First(p => p.Id == id);
}

/// <summary>侧栏导航项 VM：图标 + 标签。视图选中态由 ListBox 的 SelectedItem 与
/// ListBoxItem:selected 伪类样式驱动（单一真相源，D-003 审查后移除冗余 IsActive 状态）。
/// 标签经 PageDescriptor 实时取串；实例随外壳创建（应用级生命周期），订阅语言切换信号
/// raise Page 使 <c>{Binding Page.Label}</c> 重新求值，实时重译。</summary>
public partial class NavigationItemViewModel : ViewModelBase
{
    public PageDescriptor Page { get; }

    public NavigationItemViewModel(PageDescriptor page)
    {
        Page = page;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            OnPropertyChanged(nameof(Page));
        }
    }

    public static IReadOnlyList<NavigationItemViewModel> CreateDefaults() =>
        ShellPages.All.Select(p => new NavigationItemViewModel(p)).ToList();
}
