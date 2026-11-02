using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>D-008 会话明细页视图（搜索防抖 / 加载更多 / 详情弹层数据全部在 SessionsViewModel）。</summary>
public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        InitializeComponent();

        // 弹层打开即聚焦遮罩：Esc 经焦点元素冒泡路径才能命中遮罩的 KeyDown（行点击后焦点还在
        // ListBox 上，不在遮罩子树内）。随页面装配即挂树（隐藏态），监听遮罩转可见的时刻；
        // 帖到调度器队尾再聚焦（IsVisible 翻转的同步帧里 Focus() 会被拒，同 DataPricingView 配方）。
        DetailOverlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && DetailOverlay.IsVisible)
            {
                Dispatcher.UIThread.Post(() => DetailOverlay.Focus(), DispatcherPriority.Background);
            }
        };
    }

    // 详情弹层遮罩点击 / Esc 关闭：与 DataPricingView 模型编辑弹层同一交互配方——
    // Tapped 的 Source 是遮罩自身才关（点到弹层卡片内部不冒泡误关）。
    private void OnDetailScrimTapped(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)
            && DataContext is SessionsViewModel viewModel)
        {
            viewModel.CloseDetailCommand.Execute(null);
        }
    }

    private void OnDetailOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && DataContext is SessionsViewModel viewModel)
        {
            viewModel.CloseDetailCommand.Execute(null);
            e.Handled = true;
        }
    }
}
