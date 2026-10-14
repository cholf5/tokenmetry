using Avalonia.Controls;

namespace TokenPulse.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A-0 常驻体验（v0.5 规格）：用户关窗（点 X / Alt+F4 / 程序内 Close()）= 隐藏到托盘，
    /// 进程常驻；真正退出只走托盘菜单「退出」→ desktop.Shutdown()。生命周期驱动的关闭
    /// （托盘退出的 ApplicationShutdown、OS 会话结束的 OSShutdown）不在此列，天然放行。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.OwnerWindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
