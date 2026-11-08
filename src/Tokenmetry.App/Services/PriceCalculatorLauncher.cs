using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Services;

/// <summary>
/// 模型计价器窗口的启动器（I-CALC，独立工具入口）：应用级单例（DI），持有一个懒建窗口——
/// 已开则激活置前（不重开），已关则重建（关窗即弃：VM 订阅 Translations 随 Detach 释放，
/// 重开拿到模型目录的最新快照）。托盘菜单与 macOS Tools 菜单共用同一入口；
/// 调用方都在 UI 线程（托盘菜单命令 / 原生菜单回调），Show 内不做线程 marshal。
/// </summary>
public sealed class PriceCalculatorLauncher
{
    private readonly ModelCatalog _catalog;
    private PriceCalculatorWindow? _window;

    /// <summary>当前挂着的计价器窗口（null = 关窗即弃后的空档）；headless 单例语义测试出口。</summary>
    internal PriceCalculatorWindow? CurrentWindow => _window;

    public PriceCalculatorLauncher(ModelCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>打开（或激活）计价器窗口。开窗在 UI 线程执行；非 UI 线程调用方自行 Post。</summary>
    public void Show()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Show);
            return;
        }

        if (_window is { } existing)
        {
            existing.Show();
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new PriceCalculatorWindow
        {
            DataContext = new PriceCalculatorViewModel(_catalog),
        };
        window.Closed += (_, _) =>
        {
            if (window.DataContext is PriceCalculatorViewModel viewModel)
            {
                viewModel.Detach();
            }

            _window = null;
        };
        _window = window;
        window.Show();
    }
}
