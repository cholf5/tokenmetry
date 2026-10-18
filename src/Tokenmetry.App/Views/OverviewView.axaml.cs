using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Tokenmetry.App.Styles;

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
