using Avalonia.Controls;
using Avalonia.Input;

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
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }
}
