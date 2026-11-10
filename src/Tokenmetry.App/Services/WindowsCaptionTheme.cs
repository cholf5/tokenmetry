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
///
/// 时机（D-026 后遗症修复）：DWM 把 caption 重组合成推迟到「下一次 frame 事件」，属性写入本身
/// 不保证立即生效——真机（Win10 19045）症状：夜间模式下重启进程，标题栏纯白；按住拖动才半程
/// 变色（白底 + 标题字黑底白字的中间态），最大化才全量变黑。因此：① Attach 在构造期立即应用
/// （Avalonia 在 Window 构造时就建了 HWND，主题变体已由 App 启动接线先于建窗定死——Show 之前
/// 写入，让首次 caption 合成直接用当前声明）；② 属性写入成功后追加一次 SWP_FRAMECHANGED 强制
/// frame 重算，让启动首显与运行期明暗切换都即时落地，不再赌 DWM 的合成时机。Win10 19045 上
/// Avalonia 平台层不代写此属性（其 SetFrameThemeVariant 仅 build≥22000 生效），App 层必须自己
/// 覆盖两个时机。真实视觉效果仍以真机目检为准（D-026 卡纪律），headless/CI 无法断言。
/// </summary>
internal sealed class WindowsCaptionTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    // SetWindowPos 标志位：只请求 frame 重算，不动几何 / Z 序 / 激活状态。
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;

    private readonly Func<IntPtr, int, int, bool> _trySetAttribute;
    private readonly Func<IntPtr, bool> _forceFrameRecompute;
    private readonly Func<bool> _isWindows;
    private readonly Func<string, string?> _getEnvironmentVariable;

    /// <summary>生产实例：真实 DWM + OS 判定 + 进程环境（测试可注入替身）。</summary>
    internal static WindowsCaptionTheme Default { get; } = new(
        TrySetDwmAttribute,
        ForceFrameRecompute,
        () => OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable);

    internal WindowsCaptionTheme(
        Func<IntPtr, int, int, bool> trySetAttribute,
        Func<IntPtr, bool> forceFrameRecompute,
        Func<bool> isWindows,
        Func<string, string?> getEnvironmentVariable)
    {
        _trySetAttribute = trySetAttribute;
        _forceFrameRecompute = forceFrameRecompute;
        _isWindows = isWindows;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    /// <summary>
    /// 窗口生命周期接线：构造期立即应用（Show 前的首写）+ 首次上屏与主题切换时重应用。
    /// 处理器挂在窗口自身，随窗口一起回收，无需退订。
    /// </summary>
    internal void Attach(Window window)
    {
        Apply(window);
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
    /// 写入成功后强制一次 frame 重算（见类型注释）。平台 / headless / 空句柄在触达 DWM 之前返回。
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
        var applied = _trySetAttribute(hwnd, DwmwaUseImmersiveDarkMode, value)
            || _trySetAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, value);
        if (applied)
        {
            _forceFrameRecompute(hwnd);
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    /// <summary>
    /// SWP_FRAMECHANGED 让 DWM 重新计算非客户区并按当前属性重组 caption——属性写入与
    /// 视觉落地之间缺的就是这一步（重启白标题在拖动/最大化后变黑，正是 frame 事件补上了它）。
    /// 隐藏窗口上调用无害（首显时按当前声明合成）。
    /// </summary>
    private static bool ForceFrameRecompute(IntPtr hwnd)
    {
        try
        {
            return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            return false;
        }
    }
}
