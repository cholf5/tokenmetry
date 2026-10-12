using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Media;

namespace CCUsage.App.Services;

/// <summary>
/// 轻量 toast 反馈（I-027）：包装 Avalonia 的 WindowNotificationManager（外观由 Semi 的
/// NotificationCard 样式提供，明暗自适应），对应原型 .toast 的"底部中央、数秒自动消失"语义。
/// 选型结论：采用 Semi 通知而非自绘 overlay——理由记录在 I-027 卡 Development Log。
/// </summary>
public interface IToastService
{
    /// <summary>在宿主窗口（缺省主窗口）底部中央弹一条轻量提示，约 2.5 秒后自动消失。</summary>
    void Show(string message, Visual? host = null);
}

public sealed class ToastService : IToastService
{
    /// <summary>manager 需要挂在 TopLevel 的 adorner 层上，按窗口缓存，避免每次弹条都重建。
    /// ConditionalWeakTable 使缓存条目随 TopLevel 回收（I-027 审查修复：Dictionary 强引用会泄漏已关窗口）。</summary>
    private readonly ConditionalWeakTable<TopLevel, WindowNotificationManager> _managers = new();

    public void Show(string message, Visual? host = null)
    {
        var topLevel = host switch
        {
            TopLevel t => t,
            not null => TopLevel.GetTopLevel(host),
            _ => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow,
        };
        if (topLevel is null)
        {
            return;
        }

        if (!_managers.TryGetValue(topLevel, out var manager))
        {
            manager = new WindowNotificationManager(topLevel)
            {
                // 原型 .toast 定位在页面底部居中，这里对齐；留出底部边距避免贴边。
                Position = NotificationPosition.BottomCenter,
                MaxItems = 3,
                Margin = new Thickness(0, 0, 0, 24),
            };
            _managers.Add(topLevel, manager);
        }

        // manager 的模板承载 PART_Items 面板，卡片在 Show 时加入该面板；Avalonia 按需应用模板，
        // 首次 Show 可能早于模板应用（卡片会进 Show 内部的兜底 List 而永远不显示），这里强制先应用。
        manager.ApplyTemplate();

        manager.Show(new Notification(
            string.Empty,
            message,
            NotificationType.Information,
            TimeSpan.FromSeconds(2.5)));
    }
}
