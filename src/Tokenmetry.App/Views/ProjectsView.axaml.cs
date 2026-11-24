using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// D-011 项目统计页视图（排行表 / 均值 / 行跳转入口在 ProjectsViewModel；
/// 行右键菜单由 <see cref="ProjectRowContextMenu"/> 与总览项目排行共用同一构建器）。
/// </summary>
public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        // Enter 必须在隧道阶段截获：ListBoxItem 自己会处理方向键/回车（选择语义），冒泡到 ListBox
        // 之前就被标记 Handled。隧道在 UserControl 层先于容器看到按键。
        AddHandler(KeyDownEvent, OnProjectRowsKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 左键单击行 = 跳会话页（原 SelectedItem 触发方式退役：右键菜单会顺带改选中项，
    /// 选择即跳会让「想看菜单」变成「已被带去会话页」）。Tapped 是 primary button 语义，
    /// 右键走 RightTapped / ContextRequested，不会误触。
    /// </summary>
    private void OnProjectRowsTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectsViewModel viewModel)
        {
            return;
        }

        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is ProjectRankRowViewModel row)
        {
            viewModel.OpenSessionsFor(row);
        }
    }

    /// <summary>键盘可达性：方向键只移动选中项，Enter 才跳转（避免浏览列表时连续跳页）。</summary>
    private void OnProjectRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ProjectsViewModel viewModel)
        {
            return;
        }

        var list = this.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(candidate => candidate.Classes.Contains("data-table"));
        if (list?.SelectedItem is ProjectRankRowViewModel row)
        {
            viewModel.OpenSessionsFor(row);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 行右键：始终给菜单（可打开行给动作，不可打开行给置灰说明）。菜单内容与门禁在共享的
    /// <see cref="ProjectRowContextMenu"/>，本页只提供行与动作源（<see cref="ProjectsViewModel.Actions"/>）。
    /// 挂到行上作测试发现落点；本 handler 先于自动开启路径订阅（属性在运行期才首次赋值），
    /// 且总是重建 + 置 Handled，自动开启路径不会再开一份旧菜单。
    /// </summary>
    private void OnProjectRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border rowBorder
            || rowBorder.DataContext is not ProjectRankRowViewModel row
            || DataContext is not ProjectsViewModel viewModel)
        {
            return;
        }

        var menu = ProjectRowContextMenu.Build(viewModel.Actions, row);
        rowBorder.ContextMenu = menu;
        menu.Open(rowBorder);
        e.Handled = true;
    }
}
