using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Services;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// S-005 关窗行为分派（MainWindow.OnClosing）的 headless 验证：
/// 1. 决策纯函数 ResolveUserCloseAction 按设置映射三态（托盘 / 首关提示 / 退出）；
/// 2. 关窗=托盘（默认）首关：弹一次性模态提示窗，主窗在提示窗关闭后才隐藏，状态位即时落盘；
/// 3. 提示已展示过：关窗直接隐藏，不再弹提示、不落盘。
/// 「关窗=退出」只测决策函数：desktop.Shutdown 会终结 headless 测试宿主的应用生命周期，
/// 与 TrayServiceTests 对真机退出路径的人工核验约定一致。
/// 提示窗定位走 MainWindow.ActiveHintWindow 测试接缝——本宿主 ApplicationLifetime 为 null
/// （无桌面生命周期），无法枚举窗口。
/// </summary>
public class CloseToTrayBehaviorTests
{
    [Fact]
    public void ResolveUserCloseAction_MapsSettingsToThreeStates()
    {
        // 无设置注入（headless / 设计期构造）= 旧行为：隐藏
        Assert.Equal(MainWindow.UserCloseAction.MinimizeToTray, MainWindow.ResolveUserCloseAction(null));
        // 默认（开关开 + 未提示过）= 首关提示
        Assert.Equal(MainWindow.UserCloseAction.ShowFirstCloseHint, MainWindow.ResolveUserCloseAction(new AppSettings()));
        // 提示已展示过 = 直接隐藏
        Assert.Equal(
            MainWindow.UserCloseAction.MinimizeToTray,
            MainWindow.ResolveUserCloseAction(new AppSettings { CloseToTrayHintShown = true }));
        // 开关关闭 = 退出
        Assert.Equal(
            MainWindow.UserCloseAction.Exit,
            MainWindow.ResolveUserCloseAction(new AppSettings { MinimizeToTrayOnClose = false }));
    }

    [AvaloniaFact]
    public void First_user_close_shows_hint_dialog_then_hides_and_persists_flag()
    {
        var settings = new AppSettings();
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();
            Assert.True(window.IsVisible);

            window.Close(); // 用户关窗被拦截：先弹一次性提示窗

            Assert.True(settings.CloseToTrayHintShown); // 状态位即时落位（内存）
            Assert.Equal(1, store.SaveCalls);           // 并 best-effort 落盘

            DispatcherPump.PumpUntil(() => window.ActiveHintWindow is { IsVisible: true });
            var hint = window.ActiveHintWindow!;
            Assert.True(window.IsVisible); // 提示期间主窗保持可见（OnLastWindowClose 不被误触）

            hint.Close(); // 提示窗关闭 → 主窗才隐藏（托盘常驻语义不变）
            DispatcherPump.PumpUntil(() => !window.IsVisible && window.ActiveHintWindow is null);
            Assert.False(window.IsVisible);

            window.Show(); // 托盘「打开主窗」可唤回——窗口未被销毁
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Subsequent_user_close_hides_directly_without_hint()
    {
        var settings = new AppSettings { CloseToTrayHintShown = true };
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();

            window.Close();

            Assert.False(window.IsVisible); // 直接隐藏（提示已展示过）
            Assert.Null(window.ActiveHintWindow); // 不弹提示窗
            Assert.Equal(0, store.SaveCalls);     // 隐藏不落盘
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class CountingSettingsStore : IAppSettingsStore
    {
        public int SaveCalls { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new AppSettings());
        }

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }
    }
}
