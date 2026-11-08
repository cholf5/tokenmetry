using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

        // 跨页「添加映射」揭示（模型统计页右键跳转）：数据与计价 VM 是 DI 单例，揭示请求
        // 在导航落地前就挂好（外壳先 Prepare 再 GoToPage），视图可能尚未创建——
        // AttachedToVisualTree 与 DataContext 就绪两个时机都查一次，命中即消费（幂等，
        // HasPendingAliasReveal 在检查里同步清零，双触发不会滚两次）。
        AttachedToVisualTree += (_, _) => TryRevealPendingAliasForm();
        DataContextChanged += (_, _) => TryRevealPendingAliasForm();
    }

    /// <summary>
    /// 消费待办揭示请求（<see cref="DataPricingViewModel.HasPendingAliasReveal"/>）：滚动到别名映射区
    /// 并把光标落进规范名输入框。动作帖到 Background 优先级——晚于导航落地后的首帧布局
    /// （ScrollViewer 量出内容范围）BringIntoView 才有得滚；早于它排队的话 Loaded/Layout 都可能抢跑。
    /// 聚焦落到 AutoCompleteBox 模板内的 TextBox part（控件本身不收键盘焦点）；模板未就绪时
    /// 回落控件自身，不做二次等待。
    /// </summary>
    private void TryRevealPendingAliasForm()
    {
        if (DataContext is not DataPricingViewModel { HasPendingAliasReveal: true } viewModel)
        {
            return;
        }

        viewModel.HasPendingAliasReveal = false;
        Dispatcher.UIThread.Post(() =>
        {
            AliasPanel.BringIntoView();
            (AliasTargetBox.FindDescendantOfType<TextBox>() ?? (Control)AliasTargetBox).Focus();
        }, DispatcherPriority.Background);
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
