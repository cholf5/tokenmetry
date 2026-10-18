using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 弹窗壳层毛玻璃成形 interop（B6，macOS 专属）：Avalonia 的 AcrylicBlur 在窗口矩形内铺满一块
/// NSVisualEffectView（AutoFitContentView._blurBehind，BehindWindow + wantsLayer，12.1.4 源码取证；
/// SetTransparencyMode(Blur) 只是把它的 hidden 打开），窗口本身方形无边框——直接呈现就是
/// "方形毛玻璃板"（B5 用户目检否决）。本 interop 把 blur 视图层裁圆角、窗口背景垫色置透明、
/// 开原生投影，得到 CodexBar 同款"整个面板即一块原生毛玻璃"。
/// <para>手法与 MacTrayInterop 同纪律：raw objc_msgSend（全指针/double 传参——Intel 也兼容，
/// 无 NSRect stret 问题）、UI 主线程、全 try/catch，失败只留诊断不改行为（观感回落方形毛玻璃）。
/// 仅裁"非隐藏"的 NSVisualEffectView：AutoFitContentView 另有一个常驻隐藏的标题栏材质视图。</para>
/// </summary>
internal static unsafe class MacPopoverBlurInterop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, ulong arg1);

    /// <summary>把窗口的 blur 视图裁成 cornerRadius 圆角并清理窗口垫色。成功 true；任何一步不可得
    /// 返回 false（观感保持方形，功能不受影响）。幂等可重入（OnOpened 与协商变更都会调）。</summary>
    public static bool TryShapeBlurBehind(Window window, double cornerRadius)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1")
            {
                return false; // headless 宿主的平台句柄不是 NSView（B2 双轨纪律：测试宿主打此标记）
            }

            var handle = window.TryGetPlatformHandle();
            if (handle is null || !handle.HandleDescriptor.Contains("NSView", StringComparison.Ordinal))
            {
                return false;
            }

            var wnd = MsgSend(handle.Handle, Sel("window"));
            var content = wnd != IntPtr.Zero ? MsgSend(wnd, Sel("contentView")) : IntPtr.Zero;
            var subviews = content != IntPtr.Zero ? MsgSend(content, Sel("subviews")) : IntPtr.Zero;
            if (subviews == IntPtr.Zero)
            {
                return false;
            }

            var effectClass = objc_getClass("NSVisualEffectView");
            var count = (long)MsgSend(subviews, Sel("count"));
            for (var i = 0L; i < count; i++)
            {
                var view = MsgSend(subviews, Sel("objectAtIndex:"), (ulong)i);
                if (view == IntPtr.Zero
                    || MsgSend(view, Sel("isKindOfClass:"), effectClass) == IntPtr.Zero
                    || MsgSend(view, Sel("isHidden")) != IntPtr.Zero)
                {
                    continue;
                }

                var layer = MsgSend(view, Sel("layer"));
                if (layer == IntPtr.Zero)
                {
                    MsgSend(view, Sel("setWantsLayer:"), (IntPtr)1);
                    layer = MsgSend(view, Sel("layer"));
                }

                if (layer == IntPtr.Zero)
                {
                    return false;
                }

                MsgSend(layer, Sel("setCornerRadius:"), cornerRadius);
                MsgSend(layer, Sel("setMasksToBounds:"), (IntPtr)1);
                // Avalonia 对 Blur 模式给窗口垫了 windowBackgroundColor（WindowBaseImpl
                // SetTransparencyMode）——blur 视图裁圆角后四角会露出该实色，置透明消除
                MsgSend(wnd, Sel("setBackgroundColor:"), MsgSend(objc_getClass("NSColor"), Sel("clearColor")));
                // 无边框窗默认不投影；开系统投影（跟随内容 alpha 走圆角形状，替代被去掉的 BoxShadow）
                MsgSend(wnd, Sel("setHasShadow:"), (IntPtr)1);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 毛玻璃圆角 interop 失败（观感回落方形毛玻璃）：{ex.Message}");
            return false;
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);
}
