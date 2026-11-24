using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>D-011 项目统计页视图（排行表 / 均值 / 行跳转与右键菜单逻辑入口在 ProjectsViewModel）。</summary>
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
    /// 行右键：始终给菜单。可打开行（<see cref="ProjectRankRowViewModel.CanOpen"/>）给两个动作；
    /// 不可打开行（Claude Code 的编码目录名、"Gemini"/"Unknown Project" 等占位串）给一条
    /// 置灰说明——绝不猜路径，但也不让用户点了没反应。菜单在 code-behind 现建（同 ModelsView）：
    /// Avalonia 的 ContextMenu 不从 placement target 继承 DataContext，XAML 里声明菜单项绑不到
    /// 页面 VM 的命令；文案开菜单时取当前语言（瞬态面，不回溯重译）。
    /// </summary>
    private void OnProjectRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border rowBorder
            || rowBorder.DataContext is not ProjectRankRowViewModel row
            || DataContext is not ProjectsViewModel viewModel)
        {
            return;
        }

        var menu = BuildProjectRowMenu(viewModel, row);
        // 挂到行上作测试发现落点；本 handler 先于自动开启路径订阅（属性在运行期才首次赋值），
        // 且总是重建 + 置 Handled，自动开启路径不会再开一份旧菜单。
        rowBorder.ContextMenu = menu;
        menu.Open(rowBorder);
        e.Handled = true;
    }

    /// <summary>
    /// 项目行菜单：可打开行 = 文件管理器 + 本机首选终端 + 探测到的编辑器/Git 客户端；
    /// 不可打开行 = 一条置灰说明。外部应用只来自本机探测（<see cref="ProjectsViewModel.ExternalApps"/>），
    /// 菜单里不会出现「装了没装都列出来、点了报错」的项；图标异步提取，失败保持纯文字。
    /// internal 供测试对账。
    /// </summary>
    internal static ContextMenu BuildProjectRowMenu(ProjectsViewModel viewModel, ProjectRankRowViewModel row)
    {
        var menu = new ContextMenu();
        if (!row.CanOpen)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Translations.Instance.ProjectRowOpenUnavailable,
                IsEnabled = false,
            });
            return menu;
        }

        var folderItem = new MenuItem
        {
            Header = Translations.Instance.ProjectRowOpenFolder,
            Command = viewModel.OpenProjectFolderCommand,
            CommandParameter = row,
        };
        AttachFolderIcon(folderItem);
        menu.Items.Add(folderItem);

        var terminalName = TerminalLauncher.PreferredDisplayName();
        var terminalItem = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(terminalName)
                ? Translations.Instance.ProjectRowOpenTerminal
                : string.Format(Translations.Instance.ProjectRowOpenTerminalNamedFormat, terminalName),
            Command = viewModel.OpenProjectTerminalCommand,
            CommandParameter = row,
        };
        menu.Items.Add(terminalItem);
        var terminalSource = TerminalLauncher.PreferredIconSource();
        AttachIcon(terminalItem, viewModel.IconProvider, "terminal|" + terminalSource, terminalSource);

        AddExternalAppGroup(menu, viewModel, row, ExternalAppKind.Editor);
        AddExternalAppGroup(menu, viewModel, row, ExternalAppKind.GitClient);
        return menu;
    }

    private static void AddExternalAppGroup(
        ContextMenu menu,
        ProjectsViewModel viewModel,
        ProjectRankRowViewModel row,
        ExternalAppKind kind)
    {
        var apps = viewModel.ExternalApps.Where(app => app.Kind == kind).ToList();
        if (apps.Count == 0)
        {
            return;
        }

        menu.Items.Add(new Separator());
        foreach (var app in apps)
        {
            var item = new MenuItem
            {
                Header = string.Format(Translations.Instance.ProjectRowOpenInAppFormat, app.DisplayName),
                Command = new AsyncRelayCommand(() => viewModel.OpenInAppAsync(app, row)),
            };
            menu.Items.Add(item);
            AttachIcon(item, viewModel.IconProvider, $"ext|{app.Id}|{app.IconSource}", app.IconSource);
        }
    }

    /// <summary>文件管理器项用内置 folder 几何图标，和真实应用图标同槽位对齐。</summary>
    private static void AttachFolderIcon(MenuItem item)
    {
        var application = Application.Current;
        if (application is null
            || !application.TryGetResource("AppIconFolder", application.ActualThemeVariant, out var resource)
            || resource is not Geometry geometry)
        {
            return;
        }

        item.Icon = new PathIcon { Data = geometry, Width = 16, Height = 16 };
    }

    /// <summary>图标异步提取；就绪后回填菜单项，失败保持纯文字（不弹错、不阻塞开菜单）。</summary>
    private static void AttachIcon(
        MenuItem item,
        IAppIconProvider iconProvider,
        string cacheKey,
        string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return;
        }

        _ = LoadIconAsync(item, iconProvider, cacheKey, iconSource);
    }

    private static async Task LoadIconAsync(
        MenuItem item,
        IAppIconProvider iconProvider,
        string cacheKey,
        string iconSource)
    {
        try
        {
            var icon = await iconProvider.GetIconAsync(cacheKey, iconSource);
            if (icon is not null)
            {
                item.Icon = new Image { Width = 16, Height = 16, Source = icon };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 回填应用图标失败（{cacheKey}）：{ex.Message}");
        }
    }
}
