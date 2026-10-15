using Avalonia.Controls;

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
}
