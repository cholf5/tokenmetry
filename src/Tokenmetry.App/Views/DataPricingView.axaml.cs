using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

public partial class DataPricingView : UserControl
{
    public DataPricingView()
    {
        InitializeComponent();
    }

    // 点遮罩取消：Tapped 的 Source 是遮罩自身才关（点到弹层卡片内部不冒泡误关）。
    private void OnModelEditScrimTapped(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)
            && DataContext is DataPricingViewModel viewModel)
        {
            viewModel.CancelModelEditCommand.Execute(null);
        }
    }

    private void OnModelEditOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && DataContext is DataPricingViewModel viewModel)
        {
            viewModel.CancelModelEditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
