using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Tokenmetry.App.Services;

/// <summary>
/// Windows 原生标题栏深色适配（D-026）。Win32 经典 caption 不跟随应用主题：深色界面会顶着
/// 一条系统浅色标题栏。这里向 DWM 声明沉浸式深色（Windows 10 2004+ 属性 20，1809–1903 回落 19），
/// 并随 ActualThemeVariant 重新应用。非 Windows / headless / 空句柄一律静默跳过——headless 句柄
/// 不是真 HWND（B2 双轨纪律，同 StatusPopoverWindow 的 DWM 圆角处理）。
/// 明确不做：自绘标题栏（D-003 沿用系统边框）与 DWMWA_CAPTION_COLOR（会压掉系统 active/inactive
/// 状态与高对比度适配，收益只剩色值完全一致）。
/// </summary>
internal sealed class WindowsCaptionTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    private readonly Func<IntPtr, int, int, bool> _trySetAttribute;
    private readonly Func<bool> _isWindows;
    private readonly Func<string, string?> _getEnvironmentVariable;

    /// <summary>生产实例：真实 DWM + OS 判定 + 进程环境（测试可注入替身）。</summary>
    internal static WindowsCaptionTheme Default { get; } = new(
        TrySetDwmAttribute,
        () => OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable);

    internal WindowsCaptionTheme(
        Func<IntPtr, int, int, bool> trySetAttribute,
        Func<bool> isWindows,
        Func<string, string?> getEnvironmentVariable)
    {
        _trySetAttribute = trySetAttribute;
        _isWindows = isWindows;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    /// <summary>
    /// 窗口生命周期接线：首次上屏与主题切换时应用。处理器挂在窗口自身，随窗口一起回收，无需退订。
    /// </summary>
    internal void Attach(Window window)
    {
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private void Apply(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Apply(hwnd, window.ActualThemeVariant);
    }

    /// <summary>
    /// 对 HWND 应用当前主题：Dark → 1，Light → 0（切回浅色必须显式清零，否则窗口会保留旧声明）。
    /// 平台 / headless / 空句柄在触达 DWM 之前返回。
    /// </summary>
    internal void Apply(IntPtr hwnd, ThemeVariant actualThemeVariant)
    {
        if (!_isWindows()
            || _getEnvironmentVariable("TOKENMETRY_HEADLESS") == "1"
            || hwnd == IntPtr.Zero)
        {
            return;
        }

        // ThemeVariant 是按 Key 值比较的 record（== 与 Equals 同语义）：凡非 Dark 的变体（含 Default）一律写 0。
        var value = actualThemeVariant.Equals(ThemeVariant.Dark) ? 1 : 0;
        if (!_trySetAttribute(hwnd, DwmwaUseImmersiveDarkMode, value))
        {
            // 1809–1903 只认属性 19；更旧的系统 / RDP 上两者都失败即静默放弃（保持系统默认）。
            _trySetAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, value);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

    private static bool TrySetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }
}
