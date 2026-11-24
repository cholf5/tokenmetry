using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// 项目行右键菜单的唯一构建器（项目统计页排行行 / 总览项目排行行共用）：
/// 可打开行 = 文件管理器 + 本机首选终端 + 探测到的编辑器 / Git 客户端；不可打开行 = 一条置灰说明。
/// 动作与外部应用目录来自 <see cref="ProjectRowActions"/>，本类只管装配菜单与图标回填，不持有页面状态。
///
/// 菜单在 code-behind 现建：Avalonia 的 ContextMenu 不从 placement target 继承 DataContext，
/// XAML 里声明菜单项绑不到页面 VM 的命令；文案开菜单时取当前语言（瞬态面，不回溯重译，同 toast 纪律）。
/// 测试可直接调用（internal）核对菜单项与命令，无需起窗口。
/// </summary>
internal static class ProjectRowContextMenu
{
    /// <summary>
    /// 项目行菜单：<paramref name="row"/> 不可打开时只给一条置灰说明——不让用户「点了没反应」，
    /// 也绝不猜测 / 解码编码目录名。
    /// </summary>
    public static ContextMenu Build(ProjectRowActions actions, IProjectPathRow row)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(row);

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
            Command = actions.OpenFolderCommand,
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
            Command = actions.OpenTerminalCommand,
            CommandParameter = row,
        };
        menu.Items.Add(terminalItem);
        var terminalSource = TerminalLauncher.PreferredIconSource();
        AttachIcon(terminalItem, actions.IconProvider, "terminal|" + terminalSource, terminalSource);

        AddExternalAppGroup(menu, actions, row, ExternalAppKind.Editor);
        AddExternalAppGroup(menu, actions, row, ExternalAppKind.GitClient);
        return menu;
    }

    private static void AddExternalAppGroup(
        ContextMenu menu,
        ProjectRowActions actions,
        IProjectPathRow row,
        ExternalAppKind kind)
    {
        var apps = actions.ExternalApps.Where(app => app.Kind == kind).ToList();
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
                Command = new AsyncRelayCommand(() => actions.OpenInAppAsync(app, row)),
            };
            menu.Items.Add(item);
            AttachIcon(item, actions.IconProvider, $"ext|{app.Id}|{app.IconSource}", app.IconSource);
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
