using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CCUsage.App.Localization;
using CCUsage.App.Tray;
using CCUsage.App.Views;
using Xunit;

namespace CCUsage.App.Headless.Tests.Shell;

/// <summary>
/// A-0 托盘常驻的 headless 验证（headless 宿主无桌面生命周期，App 启动的托盘接线块不执行，
/// 由本测试直接构造 TrayService 覆盖同一初始化路径）：
/// 1. Initialize 经 TrayIcon.SetIcons 注册托盘（唯一挂载路径，修复前图标永不出现）；
/// 2. 菜单规格 = 今日成本行 + 打开主窗 / 立即刷新 / 设置 / 退出（v0.5 规格docs/plans §5）；
/// 3. UpdateToolTip 回写 tooltip 与菜单首行（修复前为死方法，托盘永远不显示成本）；
/// 4. 用户关窗 = 隐藏到托盘（可再唤回），不真正销毁窗口（修复前关窗即退出）。
/// 真机托盘出现 / 退出进程不在 headless 能力内，留给发布文档人工核验项。
/// </summary>
public class TrayServiceTests : IDisposable
{
    // SetIcons 会整集合替换 Application.Icons；每个用例自建图标并在收尾移除，避免会话级串扰
    private readonly MainWindow _window = new();

    public void Dispose()
    {
        // GetIcons 在从未 SetIcons 时返回 null（本类不注册托盘的用例）
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons is not null)
        {
            foreach (var icon in icons.ToList())
            {
                icons.Remove(icon);
            }
        }

        _window.Close();
    }

    [AvaloniaFact]
    public void Initialize_registers_tray_icon_with_spec_menu()
    {
        new TrayService(_window).Initialize();

        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);
        Assert.True(icon!.IsVisible);
        Assert.NotNull(icon.Icon); // A-0 #2：图标资产已内嵌，不再是 null 占位

        var headers = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
        Assert.Contains("打开主窗", headers);
        Assert.Contains("立即刷新", headers);
        Assert.Contains("设置", headers);
        Assert.Contains("退出", headers);
        Assert.NotNull(CostItem(icon)); // 今日成本常驻行（macOS 无 tooltip，此行是成本唯一出口）
    }

    [AvaloniaFact]
    public void UpdateToolTip_updates_both_tooltip_and_cost_menu_row()
    {
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);

        tray.UpdateToolTip("今日成本：$12.50");

        Assert.Equal("今日成本：$12.50", icon!.ToolTipText);
        Assert.Equal("今日成本：$12.50", CostItem(icon)!.Header);
    }

    [AvaloniaFact]
    public void User_close_hides_window_to_tray_and_allows_recall()
    {
        var window = new MainWindow();
        window.Show();
        Assert.True(window.IsVisible);

        window.Close(); // 拦截为隐藏：进程常驻（修复前关窗即退出）
        Assert.False(window.IsVisible);

        window.Show(); // 托盘「打开主窗」可唤回——窗口未被销毁
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Language_switch_updates_menu_headers_in_place_without_replacing_menu()
    {
        // 语言切换回归（真机实测）：macOS 的 NativeMenu 导出器缓存首个挂载实例，语言切换
        // 若整体替换 _trayIcon.Menu 会抛 ArgumentException("The menu being updated does not
        // match.") 且沿 PropertyChanged 同步链打断切换——正确做法是就地更新各菜单项 Header。
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);
        var menuBefore = icon!.Menu;

        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);

            Assert.Same(menuBefore, icon.Menu); // 菜单实例不替换
            var headers = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("Open window", headers);
            Assert.Contains("Refresh now", headers);
            Assert.Contains("Quit", headers);

            Translations.Instance.SetLanguage(originalLanguage);
            var headersRestored = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("打开主窗", headersRestored);
            Assert.Contains("退出", headersRestored);
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线
        }
    }

    private static TrayIcon? SingleRegisteredIcon()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        return icons.Count == 1 ? icons[0] : null;
    }

    private static NativeMenuItem? CostItem(TrayIcon icon) =>
        icon.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header?.StartsWith("今日成本") == true);
}
