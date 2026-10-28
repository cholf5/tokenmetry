using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Tokenmetry.App.Services;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// S-005 关窗行为分派（MainWindow.OnClosing）的 headless 验证：
/// 1. 决策纯函数 ResolveUserCloseAction 按设置映射三态（托盘 / 首关选择弹窗 / 退出）；
/// 2. 首次关窗（默认设置）：弹一次性模态选择窗（直接退出=默认 / 缩到托盘），选择前不落任何
///    状态位；选「缩到托盘」→ 写入 MinimizeToTrayOnClose=true + 状态位并落盘，弹窗关闭后主窗隐藏；
/// 3. 选「直接退出」→ 写入 MinimizeToTrayOnClose=false + 状态位并落盘，主窗走生命周期退出
///    （headless 宿主无桌面生命周期，ExitViaLifetime 为 no-op——与 TrayServiceTests 对真机退出
///    路径的人工核验约定一致，这里只验证不隐藏 + 落盘）；
/// 4. 弹窗被直接关掉（标题栏 X，无回执）视为未选择：不落状态位、主窗保持可见，下次关窗再问；
/// 5. 选择已做出：关窗按设置直接隐藏 / 退出，不再弹窗。
/// 弹窗定位走 MainWindow.ActiveFirstCloseDialog、按钮走 FirstCloseExitButton / FirstCloseTrayButton
/// 测试接缝——本宿主 ApplicationLifetime 为 null（无桌面生命周期），无法枚举窗口，代码建窗亦无
/// NameScope 注册可 FindControl。
/// </summary>
public class CloseToTrayBehaviorTests
{
    [Fact]
    public void ResolveUserCloseAction_MapsSettingsToStates()
    {
        // 无设置注入（headless / 设计期构造）= 旧行为：隐藏
        Assert.Equal(MainWindow.UserCloseAction.MinimizeToTray, MainWindow.ResolveUserCloseAction(null));
        // 默认（开关开 + 首关选择未做出）= 先弹选择窗
        Assert.Equal(MainWindow.UserCloseAction.ShowFirstCloseDialog, MainWindow.ResolveUserCloseAction(new AppSettings()));
        // 选择已做出（开关保持默认开）= 直接隐藏
        Assert.Equal(
            MainWindow.UserCloseAction.MinimizeToTray,
            MainWindow.ResolveUserCloseAction(new AppSettings { FirstCloseChoiceMade = true }));
        // 开关关闭 = 退出（无论首关选择是否做出——开关是用户在设置页的最终出口）
        Assert.Equal(
            MainWindow.UserCloseAction.Exit,
            MainWindow.ResolveUserCloseAction(new AppSettings { MinimizeToTrayOnClose = false }));
        Assert.Equal(
            MainWindow.UserCloseAction.Exit,
            MainWindow.ResolveUserCloseAction(new AppSettings { MinimizeToTrayOnClose = false, FirstCloseChoiceMade = true }));
    }

    [AvaloniaFact]
    public void First_user_close_tray_choice_persists_then_hides()
    {
        var settings = new AppSettings();
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();
            Assert.True(window.IsVisible);

            window.Close(); // 用户关窗被拦截：先弹首关选择窗

            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is { IsVisible: true });
            var dialog = window.ActiveFirstCloseDialog!;
            Assert.True(window.IsVisible); // 选择期间主窗保持可见（OnLastWindowClose 不被误触）
            Assert.False(settings.FirstCloseChoiceMade); // 未选择不置位
            Assert.Equal(0, store.SaveCalls);            // 未选择不落盘

            window.FirstCloseTrayButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // 选「缩到托盘」

            Assert.True(settings.FirstCloseChoiceMade);            // 状态位即时落位（内存）
            Assert.True(settings.MinimizeToTrayOnClose);           // 托盘语义写入关窗设置
            Assert.Equal(1, store.SaveCalls);                      // 选择即时落盘
            DispatcherPump.PumpUntil(() => !window.IsVisible && window.ActiveFirstCloseDialog is null);
            Assert.False(window.IsVisible); // 弹窗关闭后主窗才隐藏（托盘常驻语义不变）

            window.Show(); // 托盘「打开主窗」可唤回——窗口未被销毁
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void First_user_close_exit_choice_persists_and_does_not_hide()
    {
        var settings = new AppSettings();
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();
            window.Close(); // 弹首关选择窗

            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is { IsVisible: true });

            window.FirstCloseExitButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // 选「直接退出」（默认按钮）

            Assert.True(settings.FirstCloseChoiceMade);
            Assert.False(settings.MinimizeToTrayOnClose); // 退出语义写入关窗设置（下次关窗直接退出）
            Assert.Equal(1, store.SaveCalls);
            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is null);
            Assert.True(window.IsVisible); // 退出走生命周期收尾（headless no-op），主窗不隐藏
        }
        finally
        {
            // 此刻 MinimizeToTrayOnClose=false：finally 的 Close 走退出分派（headless no-op），
            // 窗口保留到宿主回收，无清理需求。
        }
    }

    [AvaloniaFact]
    public void First_close_dialog_dismissed_without_choice_asks_again_next_close()
    {
        var settings = new AppSettings();
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();
            window.Close();

            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is { IsVisible: true });
            window.ActiveFirstCloseDialog!.Close(); // 标题栏 X：无回执 = 未选择

            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is null);
            Assert.True(window.IsVisible);              // 主窗保持可见（关窗未完成）
            Assert.False(settings.FirstCloseChoiceMade); // 未选择不置位、不落盘
            Assert.Equal(0, store.SaveCalls);

            window.Close(); // 下次关窗再问
            DispatcherPump.PumpUntil(() => window.ActiveFirstCloseDialog is { IsVisible: true });
            Assert.NotNull(window.FirstCloseTrayButton);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Subsequent_user_close_hides_directly_without_dialog()
    {
        var settings = new AppSettings { FirstCloseChoiceMade = true };
        var store = new CountingSettingsStore();
        var window = new MainWindow(settings, store);
        try
        {
            window.Show();

            window.Close();

            Assert.False(window.IsVisible); // 直接隐藏（首关选择已做出）
            Assert.Null(window.ActiveFirstCloseDialog); // 不再弹窗
            Assert.Equal(0, store.SaveCalls);           // 隐藏不落盘
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
