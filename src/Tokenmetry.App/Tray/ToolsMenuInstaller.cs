using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tray;

/// <summary>
/// macOS 应用菜单栏 Tools 菜单（I-CALC）：顶层「工具」→「模型计价器」——计价器是与统计
/// 无关的独立工具，不进主窗口信息架构，入口按 macOS 惯例挂菜单栏（Windows/Linux 无此
/// 菜单栏，走托盘右键菜单，见 <see cref="TrayService"/>）。仅 macOS 真机调用：headless 宿主
/// 无菜单栏，Windows 上 NativeMenu.SetMenu 会渲染成窗体内菜单条（非产品语义）。
/// <para>菜单实例纪律与托盘同款：macOS 的 NativeMenu 导出器缓存首个挂载实例，语言切换
/// 只就地改 Header，绝不重建 / 替换菜单实例。</para>
/// </summary>
public static class ToolsMenuInstaller
{
    public static void Install(Window window, PriceCalculatorLauncher launcher)
    {
        var calculatorItem = new NativeMenuItem
        {
            Header = Translations.Instance.TrayPriceCalculator,
            Command = new RelayCommand(launcher.Show),
        };
        var toolsItem = new NativeMenuItem
        {
            Header = Translations.Instance.ToolsMenuTitle,
            Menu = new NativeMenu { Items = { calculatorItem } },
        };
        var menu = new NativeMenu();
        menu.Items.Add(toolsItem);

        // 语言切换重译：两级标题就地改写（NativeMenuItem.Header 不支持绑定）。
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Translations.CurrentLanguage))
            {
                return;
            }

            toolsItem.Header = Translations.Instance.ToolsMenuTitle;
            calculatorItem.Header = Translations.Instance.TrayPriceCalculator;
        };

        NativeMenu.SetMenu(window, menu);
    }
}
