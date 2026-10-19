using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 弹窗壳层毛玻璃成形 interop（B6，macOS 专属）：Avalonia 的 AcrylicBlur 在窗口矩形内铺满一块
/// NSVisualEffectView（AutoFitContentView._blurBehind，BehindWindow + wantsLayer，12.1.4 源码取证；
/// SetTransparencyMode(Blur) 只是把它的 hidden 打开），窗口本身方形无边框——直接呈现就是
/// "方形毛玻璃板"（B5 用户目检否决）。本 interop 把 blur 视图与内容容器层都裁圆角、窗口背景垫色
/// 置透明、开原生投影，得到 CodexBar 同款"整个面板即一块原生毛玻璃"。
/// <para>手法与 MacTrayInterop 同纪律：raw objc_msgSend（全指针/double 传参——Intel 也兼容，
/// 无 NSRect stret 问题）、UI 主线程、全 try/catch。B6 返工留证：四角灰底问题（用户目检）按
/// 一次性 stderr 诊断定位（描述符/视图命中/各步骤是否落地），日志每进程至多一行。</para>
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

    private static bool _diagnosed; // 一次性诊断：每进程至多一行，防刷屏

    /// <summary>把窗口的 blur 视图与内容容器裁成 cornerRadius 圆角并清理窗口垫色。成功 true；
    /// 任何一步不可得返回 false（观感保持方形，功能不受影响）。幂等可重入（协商变更/Activated 都会调）。</summary>
    public static bool TryShapeBlurBehind(Window window, double cornerRadius)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1")
            {
                return false; // headless 宿主的平台句柄不是 NSView（B2 双轨纪律：测试宿主打此标记）
            }

            var handle = window.TryGetPlatformHandle();
            if (handle is null)
            {
                Diagnose("平台句柄为空，放弃");
                return false;
            }

            // macOS 平台句柄实测 descriptor=NSWindow（12.1.4，B6 留证——B6 首版误判为 NSView 导致
            // 本 interop 整体未跑、四角露出窗口垫色实色，即用户目检的灰底）；兼容 NSView 形态
            var descriptor = handle.HandleDescriptor;
            IntPtr wnd;
            if (descriptor?.Contains("NSWindow", StringComparison.Ordinal) == true)
            {
                wnd = handle.Handle;
            }
            else if (descriptor?.Contains("NSView", StringComparison.Ordinal) == true)
            {
                wnd = MsgSend(handle.Handle, Sel("window"));
            }
            else
            {
                Diagnose($"descriptor={descriptor ?? "(null)"}（未识别，放弃）");
                return false;
            }

            var content = wnd != IntPtr.Zero ? MsgSend(wnd, Sel("contentView")) : IntPtr.Zero;
            var subviews = content != IntPtr.Zero ? MsgSend(content, Sel("subviews")) : IntPtr.Zero;
            if (subviews == IntPtr.Zero)
            {
                Diagnose("window/contentView/subviews 取空，放弃");
                return false;
            }

            // 内容容器层整体裁圆角：内容与 blur 全部收敛到圆角内，窗口 alpha 轮廓随之走圆角
            //（系统投影形状正确）；Avalonia 已给容器 setWantsLayer（AutoFitContentView init）
            RoundLayer(content, cornerRadius);

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

                if (!RoundLayer(view, cornerRadius))
                {
                    Diagnose($"blur 视图 #{i} layer 取不到（wantsLayer 后仍空）");
                    return false;
                }

                // Avalonia 对 Blur 模式给窗口垫了 windowBackgroundColor（WindowBaseImpl
                // SetTransparencyMode）——blur 视图裁圆角后四角会露出该实色，置透明消除
                MsgSend(wnd, Sel("setBackgroundColor:"), MsgSend(objc_getClass("NSColor"), Sel("clearColor")));
                // 无边框窗默认不投影；开系统投影（跟随内容 alpha 走圆角形状，替代被去掉的 BoxShadow）
                MsgSend(wnd, Sel("setHasShadow:"), (IntPtr)1);
                Diagnose($"blur 视图 #{i} 已成形（圆角 {cornerRadius}、垫色置透明、开投影）");
                return true;
            }

            Diagnose($"contentView {count} 个子视图里无非隐藏 NSVisualEffectView");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 毛玻璃圆角 interop 失败（观感回落方形毛玻璃）：{ex.Message}");
            return false;
        }
    }

    private static bool RoundLayer(IntPtr view, double cornerRadius)
    {
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
        return true;
    }

    private static void Diagnose(string message)
    {
        if (_diagnosed)
        {
            return; // 幂等调用下结果不变，一次留证足矣
        }

        _diagnosed = true;
        Console.Error.WriteLine($"[Tokenmetry] 毛玻璃成形：{message}");
    }

    private static IntPtr Sel(string name) => sel_registerName(name);
}
