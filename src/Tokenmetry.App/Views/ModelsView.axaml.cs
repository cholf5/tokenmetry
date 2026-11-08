using Avalonia.Controls;
using Avalonia.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>D-010 模型统计页视图（明细表 / 未知成本 chip / 合计口径逻辑全部在 ModelsViewModel）。</summary>
public partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 行右键（未知成本行限定）：成本列呈「未知」chip 的缺价模型才给修复入口——已知模型
    /// （有价 / 无成本数据）右键不弹菜单。菜单在 code-behind 现建：Avalonia 的 ContextMenu
    /// 不从 placement target 继承 DataContext，XAML 里声明菜单项绑不到页面 VM 的命令；
    /// 此处直接用页面 VM 的 RelayCommands 装配（文案开菜单时取 Translations 当前值——菜单是
    /// 瞬态面，出现时语言，不回溯重译，同 toast 纪律）。打开经 Open(placement target) 并置
    /// Handled：ContextMenu 属性的自动开启路径吃不到「仅未知行才可弹」的门禁，不适用。
    /// </summary>
    private void OnModelRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border rowBorder
            || rowBorder.DataContext is not ModelDetailRowViewModel row
            || DataContext is not ModelsViewModel viewModel)
        {
            return;
        }

        if (!row.IsUnknownCost)
        {
            return; // 已知模型：不处理不置 Handled，让请求照常走完（页面上无其它菜单宿主，等于无事发生）
        }

        var menu = BuildUnknownModelRowMenu(viewModel, row.Name);
        // 挂到行上作测试发现落点；本 handler 先于自动开启路径订阅（属性在运行期才首次赋值），
        // 且总是重建 + 置 Handled，自动开启路径（查 !e.Handled）不会再开一份旧菜单。
        rowBorder.ContextMenu = menu;
        menu.Open(rowBorder);
        e.Handled = true;
    }

    /// <summary>未知模型行菜单：「新增此模型」（弹层补价）/「添加映射」（归并到规范名）。internal 供测试对账。</summary>
    internal static ContextMenu BuildUnknownModelRowMenu(ModelsViewModel viewModel, string modelName)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = Translations.Instance.ModelRowAddModel,
            Command = viewModel.RequestAddModelCommand,
            CommandParameter = modelName,
        });
        menu.Items.Add(new MenuItem
        {
            Header = Translations.Instance.ModelRowAddMapping,
            Command = viewModel.RequestAddMappingCommand,
            CommandParameter = modelName,
        });
        return menu;
    }
}
