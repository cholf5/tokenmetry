using System;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-018 应用图标接线回归锁：
/// 1. 主窗 Window.Icon 在构造期来自内嵌资产（app-icon-window.png，全出血 256）——
///    覆盖窗口标题栏 / 任务栏 / Alt-Tab / Linux WM（_NET_WM_ICON）；
/// 2. 图标三资产随 csproj 的 Assets/** AvaloniaResource 进包（exe ico / macOS Dock
///    Apple 网格 PNG / 窗口 PNG）——防「文件在目录里但没进包」的静默回归。
/// Windows exe 文件图标（csproj ApplicationIcon）与 macOS Dock interop 无法在 headless
/// 断言，真机目检移交用户（与托盘手感目检同一约定）。
/// </summary>
public class WindowIconTests
{
    [AvaloniaFact]
    public void MainWindow_sets_window_icon_from_embedded_asset()
    {
        var window = new MainWindow();
        try
        {
            Assert.NotNull(window.Icon);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Icon_assets_are_embedded_resources()
    {
        foreach (var asset in new[]
                 {
                     "Assets/app-icon.ico",
                     "Assets/app-icon.png",
                     "Assets/app-icon-window.png",
                 })
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Tokenmetry.App/{asset}"));
            Assert.True(stream.Length > 0, asset);
        }
    }
}
