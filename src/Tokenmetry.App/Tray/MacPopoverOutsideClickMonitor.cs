using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Tray;

/// <summary>
/// mac 弹层「点击外部收起」监听（macOS 专属）：收起通道原本只有 Esc / 再点托盘 / 失焦
/// （Deactivated），而点击菜单栏空白处事件归 WindowServer、没有任何窗口变 key——失焦通道
/// 不触发，弹窗留在原地（Windows 点外部必失焦关闭，平台行为不对齐）。借 NSEvent 全局监听
/// （addGlobalMonitorForEventsMatchingMask:handler:，只收本进程之外的鼠标按下）补通道：
/// 点别的 app / Dock / 菜单栏空白处 → 回调收起。无需辅助功能权限（系统公开的无特权 API）。
/// <para>自家进程的事件（弹层卡片、托盘按钮、主窗）不进全局监听，与既有三通道零重叠；
/// 监听回调先到时消费即拆（once），随后才到的 Deactivated 由控制器可见性门控吞掉，不重复
/// OnHidden。右/中键一并收——Windows 失焦通道对任何点击抢焦点都收起，此处口径对齐。</para>
/// <para>手法与 MacTrayInterop 同纪律：raw objc_msgSend、UI 主线程挂载/拆除、全 try/catch。
/// handler 块用「全局块字面量手工构造」（isa=_NSConcreteGlobalBlock + 静态 invoke，零捕获
/// 无 copy/dispose 助手），HGlobal 常驻布点，无 GC 生命周期耦合。headless 宿主
/// （TOKENMETRY_HEADLESS=1）与非 macOS 平台只记账不安装。</para>
/// </summary>
internal static unsafe class MacPopoverOutsideClickMonitor
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    // NSEventTypeLeftMouseDown=1 / RightMouseDown=3 / OtherMouseDown=25，掩码按类型号左移
    private const ulong MouseDownMask = (1UL << 1) | (1UL << 3) | (1UL << 25);
    private const int BlockIsGlobal = 1 << 29; // Block_private.h BLOCK_IS_GLOBAL

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, ulong arg1, IntPtr arg2);

    // ── 全局块字面量（clang Block_private.h 布局，arm64/x64 通用）──
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public IntPtr Reserved;
        public IntPtr Size;
    }

    private static IntPtr _app, _monitor, _block, _descriptor;
    private static Action? _onOutsideClick;
    private static bool _armed;

    /// <summary>armed 状态（headless 测试断言挂载/拆除接线；headless / 非 macOS 只记账不安装，
    /// 该标记仍如实反映「弹层可见期点击外部应收起」的意图）。</summary>
    public static bool IsArmed => _armed;

    /// <summary>进入弹层可见期：记账回调并确保全局监听已挂（幂等；安装失败静默回落——
    /// Esc / 再点 / 失焦三通道仍在，下次 Show 重试）。必须在 UI 线程调用（监听回调派发回挂载线程）。</summary>
    public static void Arm(Action onOutsideClick)
    {
        _onOutsideClick = onOutsideClick;
        _armed = true;
        EnsureInstalled();
    }

    /// <summary>离开可见期：拆回调与监听（幂等；系统级事件钩子不在弹层不可见期继续挂着）。</summary>
    public static void Disarm()
    {
        _armed = false;
        _onOutsideClick = null;
        if (_monitor != IntPtr.Zero)
        {
            try
            {
                MsgSend(_app, sel_registerName("removeMonitor:"), _monitor);
            }
            catch
            {
                // 拆除失败只留一个无效 token——下方 _monitor 复位后不会重复 remove
            }

            _monitor = IntPtr.Zero;
        }
    }

    /// <summary>全局监听回调（block invoke 直达；headless 测试亦经此直发模拟真实点击）。
    /// 消费制：先摘记账（_armed/回调）再执行——后续点击落空、不会重复收起；监听本体由
    /// 控制器 Hide → Disarm 拆除，回调异常也不影响该收尾。</summary>
    internal static void HandleOutsideMouseDown()
    {
        if (!_armed)
        {
            return;
        }

        var callback = _onOutsideClick;
        _armed = false;
        _onOutsideClick = null;
        try
        {
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 点击外部收起回调失败：{ex.Message}");
        }
    }

    private static bool EnsureInstalled()
    {
        if (_monitor != IntPtr.Zero)
        {
            return true;
        }

        if (!OperatingSystem.IsMacOS()
            || Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1")
        {
            return false; // 只记账：测试宿主/非 macOS 不碰 AppKit，真机通道由下次 Arm 重试
        }

        try
        {
            if (_block == IntPtr.Zero)
            {
                BuildGlobalBlock();
            }

            var app = MsgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero)
            {
                return false;
            }

            var monitor = MsgSend(
                app,
                sel_registerName("addGlobalMonitorForEventsMatchingMask:handler:"),
                MouseDownMask,
                _block);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            _app = app;
            _monitor = monitor;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 点击外部监听安装失败（失焦/Esc/再点通道兜底）：{ex.Message}");
            return false;
        }
    }

    /// <summary>构造零捕获全局块：invoke 指向静态 IMP，descriptor 常驻 HGlobal（进程生命周期，
    /// 与窗口复用策略同寿命，无需释放）。</summary>
    private static void BuildGlobalBlock()
    {
        var descriptor = (BlockDescriptor*)Marshal.AllocHGlobal(sizeof(BlockDescriptor));
        descriptor->Reserved = IntPtr.Zero;
        descriptor->Size = (IntPtr)sizeof(BlockLiteral);
        var block = (BlockLiteral*)Marshal.AllocHGlobal(sizeof(BlockLiteral));
        *block = new BlockLiteral
        {
            Isa = objc_getClass("_NSConcreteGlobalBlock"),
            Flags = BlockIsGlobal,
            Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OutsideMouseDownImp,
            Descriptor = (IntPtr)descriptor,
        };
        _descriptor = (IntPtr)descriptor;
        _block = (IntPtr)block;
    }

    /// <summary>block invoke（签名 void (^)(NSEvent *)：第 0 参块自身、第 1 参事件——事件内容不用）。
    /// 仅主线程进入（回调派发回挂载线程 = UI 线程），异常托管侧吃干净。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OutsideMouseDownImp(IntPtr block, IntPtr nsevent)
    {
        HandleOutsideMouseDown();
    }
}
