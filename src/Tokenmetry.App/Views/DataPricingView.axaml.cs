using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

public partial class DataPricingView : UserControl
{
    public DataPricingView()
    {
        InitializeComponent();

        // 弹层打开即聚焦 ID 框（设计文档定案）：对话框随页面装配就挂进可视树（隐藏态），
        // AttachedToVisualTree 在开弹层时不会重触发，故监听遮罩转可见的时刻。
        ModelEditOverlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && ModelEditOverlay.IsVisible)
            {
                // 帖到调度器队尾再聚焦：IsVisible 翻转的同步帧里 Focus() 会被拒（焦点还在遮罩外的按钮上）。
                Dispatcher.UIThread.Post(() => ModelEditDialogControl.FocusIdBox(), DispatcherPriority.Background);
            }
        };
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

    // 头像图标选择器弹层（Q-012）：遮罩点击 / Esc 取消，与模型编辑弹层同一交互配方。
    private void OnAvatarPickerScrimTapped(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)
            && DataContext is DataPricingViewModel viewModel)
        {
            viewModel.CancelAvatarPickerCommand.Execute(null);
        }
    }

    private void OnAvatarPickerOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && DataContext is DataPricingViewModel viewModel)
        {
            viewModel.CancelAvatarPickerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
