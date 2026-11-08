using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 模型计价器（I-CALC）headless 验证：窗口能 Show 出来、AutoCompleteBox 接住 VM 的候选清单、
/// 输入驱动实时重算并落到明细区、启动器单例语义（重复 Show 激活同一窗、关窗后重建）。
/// 断言不涉及任何界面文案（语言无关纪律）；数据用本地小目录，不触 DI 与真实用户数据。
/// </summary>
public class PriceCalculatorWindowTests
{
    private static ModelCatalog TestCatalog()
    {
        return new ModelCatalog(snapshotEntries: new[]
        {
            new ModelPriceEntry
            {
                ModelId = "test-model",
                Provider = "vendor-x",
                InputPerMillion = 3m,
                OutputPerMillion = 15m,
                CacheReadPerMillion = 0.3m,
            },
        });
    }

    [AvaloniaFact]
    public void Window_shows_and_renders_live_estimate()
    {
        var window = new PriceCalculatorWindow
        {
            DataContext = new PriceCalculatorViewModel(TestCatalog()),
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var picker = window.FindControl<AutoCompleteBox>("ModelPicker");
            Assert.NotNull(picker);
            Assert.NotEmpty(picker!.ItemsSource!.Cast<object>());

            var vm = (PriceCalculatorViewModel)window.DataContext!;
            vm.SelectedModel = vm.ModelOptions.Single(o => o.Id == "test-model");
            vm.InputTokensText = "1,000,000";
            vm.OutputTokensText = "1M";
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.True(vm.HasResult);
            // 明细区（panel Border）已随 HasResult 挂到可视树上，费用行随输入实时刷新。
            var panel = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.IsVisible && b.Classes.Contains("panel"));
            Assert.True(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Launcher_reuses_window_and_rebuilds_after_close()
    {
        var launcher = new PriceCalculatorLauncher(TestCatalog());
        try
        {
            launcher.Show();
            Dispatcher.UIThread.RunJobs();

            var first = launcher.CurrentWindow;
            Assert.NotNull(first);
            Assert.True(first!.IsVisible);

            launcher.Show(); // 已开 → 激活同一实例，不开第二扇
            Dispatcher.UIThread.RunJobs();
            Assert.Same(first, launcher.CurrentWindow);

            first.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(launcher.CurrentWindow); // 关窗即弃

            launcher.Show(); // 重建新窗（拿到目录最新快照）
            Dispatcher.UIThread.RunJobs();
            var second = launcher.CurrentWindow;
            Assert.NotNull(second);
            Assert.NotSame(first, second);

            second!.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            // 收尾：若断言中途失败，清掉挂着的计价器窗（不污染后续用例）。
            launcher.CurrentWindow?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
