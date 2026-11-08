using Avalonia.Controls;
using Avalonia.Input;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.Views;

/// <summary>
/// 模型计价器（I-CALC）：独立工具窗口，与统计零关系。Esc 关闭（工具窗语义）；
/// 生命周期由 <see cref="Services.PriceCalculatorLauncher"/> 接管（单例激活 / 关窗即弃重建）。
/// </summary>
public partial class PriceCalculatorWindow : Window
{
    public PriceCalculatorWindow()
    {
        InitializeComponent();
        // ToolTip 失活守卫：工具窗失活/关窗时把悬浮 Tip 收掉，别压在别的 App 前（同 MainWindow）。
        ToolTipDeactivationGuard.Attach(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }
}
