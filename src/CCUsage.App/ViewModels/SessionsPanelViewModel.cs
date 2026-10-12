using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;

namespace CCUsage.App.ViewModels;

/// <summary>右栏快速筛选的单个 Agent 勾选项（骨架：仅本地状态，真实接线在 D-008）。</summary>
public partial class AgentQuickFilterViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isChecked;

    public string Label { get; }

    public AgentQuickFilterViewModel(string label, bool isChecked = true)
    {
        Label = label;
        IsChecked = isChecked;
    }
}

/// <summary>
/// 右侧最近会话栏 VM（D-003 骨架）：列表结构 + 快速筛选入口。
/// 会话数据与筛选逻辑的真实接线是 D-008 的工作，本 VM 只保证外壳结构就位与诚实的空态。
/// </summary>
public partial class SessionsPanelViewModel : ViewModelBase
{
    private readonly IToastService _toastService;

    public IReadOnlyList<AgentQuickFilterViewModel> AgentFilters { get; }

    /// <summary>模型筛选占位选项（D-008 接入真实模型清单，避免伪造数据）。</summary>
    public IReadOnlyList<string> ModelFilterOptions { get; } = ["全部模型"];

    /// <summary>项目筛选占位选项（同上）。</summary>
    public IReadOnlyList<string> ProjectFilterOptions { get; } = ["全部项目"];

    [ObservableProperty]
    private string _modelFilter = "全部模型";

    [ObservableProperty]
    private string _projectFilter = "全部项目";

    public SessionsPanelViewModel(IToastService toastService)
    {
        _toastService = toastService;
        AgentFilters = AgentOption.Options
            .Where(a => a.Id != AgentOption.AllId)
            .Select(a => new AgentQuickFilterViewModel(a.Label))
            .ToList();
    }

    [RelayCommand]
    private void ApplyQuickFilters()
    {
        // 骨架反馈：本面板的筛选尚不驱动任何查询，等 D-008 接入会话查询后生效。
        _toastService.Show("快速筛选将在会话明细页（D-008）接入真实数据");
    }

    [RelayCommand]
    private void ClearQuickFilters()
    {
        foreach (var agent in AgentFilters)
        {
            agent.IsChecked = true;
        }
        ModelFilter = ModelFilterOptions[0];
        ProjectFilter = ProjectFilterOptions[0];
    }
}
