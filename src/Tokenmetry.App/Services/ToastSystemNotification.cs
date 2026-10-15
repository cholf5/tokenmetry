using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.Core.Abstractions;

namespace Tokenmetry.App.Services;

/// <summary>
/// A-11：异常检测通知的真实出口——接既有 in-app toast（I-027 WindowNotificationManager，
/// 底部中央 2.5s 自动消失）。修复前 DI 注册的是 NullSystemNotification，设置页「启用异常通知」
/// 开关是假开关（检测管线在 DashboardViewModel 真实运行，通知却被吞）。
/// 主窗隐藏（托盘常驻）时 toast 无处渲染——通知仅在主窗可见期有效；系统级通知留给 v0.5。
/// </summary>
public sealed class ToastSystemNotification(IToastService toastService) : ISystemNotification
{
    public Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        toastService.Show($"{title}：{message}");
        return Task.CompletedTask;
    }
}
