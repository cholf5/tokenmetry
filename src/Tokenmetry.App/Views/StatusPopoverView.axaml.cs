using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// D-015 状态栏快捷信息窗的内容层（跨平台共享 UserControl）：纯展示装配，零平台 API——
/// 壳层（PopoverWindow，D-016）与样式预览页（StylePreviewView）共用本视图。
/// 语言切换重译（B-007 约定）：LiveText 是 VM 按 Translations 取串的派生属性，XAML 直绑
/// 不会随字典刷新——挂树订阅 CurrentLanguage 信号经 <see cref="StatusPopoverViewModel.OnLanguageChanged"/>
/// 重算；banner 文案是瞬态（出现时语言），不回溯重译（Translations 既定取舍）。
/// </summary>
public partial class StatusPopoverView : UserControl
{
    public StatusPopoverView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // 先退订再订阅防重复（挂树可能多次发生）；挂树/脱树对称（B-007 同款）
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
    }

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage) && DataContext is StatusPopoverViewModel vm)
        {
            vm.OnLanguageChanged();
        }
    }
}
