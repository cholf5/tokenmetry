using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.ViewModels;

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
/// Label/Title/Subtitle 不内联字符串，按 <see cref="Id"/> 经 Translations 实时取串。
/// <b>类而非 record + INPC</b>：{Binding Page.Title} 这类多级绑定路径在中间段（Page）通知时
/// 值为同一实例，Avalonia 编译绑定不重读叶子——必须由叶子所属对象在语言切换时自行发通知
/// （实测：不发则侧栏标签 / 页头标题在语言切换后保持旧语言，导航重建才更新）。
/// 描述符为 ShellPages.All 进程级单例（应用级生命周期），直订阅语言切换无需退订；
/// 实例间共享同一 Id，切换时各自 raise 自己的三个文案属性。
/// PlannedSections 仅占位页渲染（当前 8 页全部有真实 VM，占位路径不可达），保持中文原文不接 i18n。
/// </summary>
public sealed class PageDescriptor : INotifyPropertyChanged
{
    public PageDescriptor(PageId id, string icon, string cardRef, IReadOnlyList<string> plannedSections)
    {
        Id = id;
        Icon = icon;
        CardRef = cardRef;
        PlannedSections = plannedSections;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public PageId Id { get; }

    public string Icon { get; }

    public string CardRef { get; }

    public IReadOnlyList<string> PlannedSections { get; }

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

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
/// 标签经 <see cref="PageDescriptor"/> 实时取串并在语言切换时由描述符自行发通知刷新
/// （绑定路径为 Page.Label，叶子在描述符上）。</summary>
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
