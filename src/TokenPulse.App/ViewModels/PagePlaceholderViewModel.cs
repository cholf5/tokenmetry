using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenPulse.App.Services;

namespace TokenPulse.App.ViewModels;

/// <summary>
/// 占位页 VM（D-003）：8 个页面里尚未由页面卡（D-006~D-013 / S-002）实现的页共用本 VM，
/// 展示页面标题、该页规划内容与落地卡号；订阅全局筛选状态，把当前筛选值呈现在界面上
/// （D-003 TC-001 的"筛选变更各占位页可观测"），不含示例数据。
/// 生命周期：每次导航新建实例，旧实例由外壳 Dispose 退订（FilterStateService 是常驻单例，
/// 强引用订阅不退订会随导航次数无界累积——D-003 审查 P1 修复）。
/// </summary>
public partial class PagePlaceholderViewModel : ViewModelBase, IDisposable
{
    private readonly FilterStateService _filters;

    /// <summary>当前全局筛选汇总（随 FilterStateService 变化更新）。</summary>
    [ObservableProperty]
    private string _currentFilterDisplay;

    public PageDescriptor Page { get; }

    public PagePlaceholderViewModel(PageDescriptor page, FilterStateService filters)
    {
        Page = page;
        _filters = filters;
        _currentFilterDisplay = filters.Display;
        // FilterStateService 保证通知在 UI 线程发出，这里直接读属性更新绑定即可。
        filters.PropertyChanged += OnFilterPropertyChanged;
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // null 或空串均表示"全属性变更"（.NET 约定）
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(FilterStateService.Display))
        {
            CurrentFilterDisplay = _filters.Display;
        }
    }

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
