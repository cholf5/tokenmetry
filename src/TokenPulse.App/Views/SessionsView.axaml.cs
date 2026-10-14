using Avalonia.Controls;

namespace TokenPulse.App.Views;

/// <summary>D-008 会话明细页视图（行点击下钻 / 搜索防抖 / 加载更多逻辑全部在 SessionsViewModel）。</summary>
public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        InitializeComponent();
    }
}
