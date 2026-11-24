using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tokenmetry.App.Styles;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// D-006 总览页视图（上半）：纯绑定壳，逻辑在 <see cref="ViewModels.OverviewViewModel"/>
/// （筛选订阅 / 序号防护 / 未知成本文案）与图表控件（TrendChart / DonutChart）。
/// </summary>
public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 项目排行行右键：与项目统计页共用同一个菜单构建器（<see cref="ProjectRowContextMenu"/>）和
    /// 动作源（<see cref="OverviewViewModel.ProjectActions"/>）——门禁、置灰说明、外部应用列表全在
    /// 共享层，本页只把行递过去。其他表格不接此事件。
    /// </summary>
    private void OnProjectRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border rowBorder
            || rowBorder.DataContext is not DimensionRowViewModel { RawPath: not null } row
            || DataContext is not OverviewViewModel viewModel)
        {
            return;
        }

        var menu = ProjectRowContextMenu.Build(viewModel.ProjectActions, row);
        rowBorder.ContextMenu = menu;
        menu.Open(rowBorder);
        e.Handled = true;
    }

    /// <summary>
    /// KPI 卡首次进场 stagger（motion design §6）：仅本页做逐卡错落（30ms 间隔），其余页吃
    /// 页面级过渡；页面复用（视图重建）即自然重播一次，数据刷新不动视图树、不重播。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var cards = this.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
            .Where(b => b.Classes.Contains("kpi"))
            .ToList();
        if (cards.Count > 0)
        {
            Motion.StaggerFadeIn(cards, Motion.NormalMs, 30);
        }
    }
}
