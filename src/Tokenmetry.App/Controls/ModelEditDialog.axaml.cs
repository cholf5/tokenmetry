using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

public partial class ModelEditDialog : UserControl
{
    public ModelEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>弹层打开时聚焦 ID 框（设计文档定案：打开即聚焦）。由宿主在遮罩转可见时调用——
    /// 本控件随页面装配即挂入可视树（弹层隐藏态），AttachedToVisualTree 在开弹层时不会重触发，
    /// 在这里挂钩会过早失活。</summary>
    public void FocusIdBox() => IdBox.Focus();
}
