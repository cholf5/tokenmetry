using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Tray;

/// <summary>
/// mac 弹层「点击外部收起」监听（macOS 专属）：收起通道原本只有 Esc / 再点托盘 / 失焦
/// （Deactivated）。mac 上点击落在弹层之外并不一定换焦点，失焦通道收不到，弹窗留在原地
/// （Windows 点外部必失焦关闭，平台行为不对齐）。补两条 NSEvent 鼠标按下通道：
/// <list type="number">
/// <item><b>全局监听</b>（addGlobalMonitorForEventsMatchingMask:handler:，只收本进程之外的鼠标
/// 按下）：点别的 app / Dock / 别人的菜单栏图标 → 收起。</item>
/// <item><b>本地监听</b>（addLocalMonitorForEventsMatchingMask:handler:，收本进程内的鼠标按下）：
/// 点菜单栏空白处的事件归<b>最前台 app 自己</b>——<see cref="MacPopoverOutsideClickMonitor"/> 的
/// 首版（27a370f）只有全局监听，真机复测「点别人的图标会收起、点空白处不会」，正是因为这条事件
/// 根本没走出本进程（真机探针留证：空白处 event.window=nil、派给本进程；别人的状态项=别的进程；
/// 自家卡片=自家 NSWindow）。本地通道只在 event.window 为 nil 时收起——那是「菜单栏空白 /
/// 菜单标题」这类无窗口目标；自家卡片、托盘状态项（NSStatusBarWindow）、Avalonia popup 都有
/// 自己的窗口，一律放行给既有通道（Esc / 再点 / 失焦），避免抢走托盘 Toggle 的收起语义
/// （先收起再被 Clicked 重开＝B-017 同款病）。</item>
/// </list>
/// 无需辅助功能权限（两者都是系统公开的无特权 API）。右/中键一并收——Windows 失焦通道对任何
/// 点击抢焦点都收起，此处口径对齐。
/// <para>手法与 MacTrayInterop 同纪律：raw objc_msgSend、UI 主线程挂载/拆除；try/catch 只兜托管
/// 异常——unrecognized selector 的 ObjC 异常跨托管帧捕不住、直接 terminate，所以接收者必须静态
/// 正确：add/remove 都是 <b>NSEvent 类方法</b>（AppKit/NSEvent.h；NSApplication 不响应——
/// 27a370f 首版误发 NSApplication 实例，点托盘 Show→Arm 即崩进程），挂载前 class_getClassMethod
/// 探测，缺 API 静默回落三通道。handler 块用「全局块字面量手工构造」（零捕获、静态 invoke、无
/// copy/dispose 助手），isa 经 dlsym 取 _NSConcreteGlobalBlock 符号地址——它是数据符号不是 ObjC
/// 类，objc_getClass 恒 NULL（真机实测，同版炸点之二）；flags=BLOCK_IS_GLOBAL(1<<28)，与 clang
/// 真块逐字节对照留证（真块实测 flags=0x50000000=IS_GLOBAL|HAS_SIGNATURE，签名位可省），AppKit
/// 接受、[copy] 保指针、removeMonitor 干净。HGlobal 常驻布点，无 GC 生命周期耦合。headless 宿主
/// （TOKENMETRY_HEADLESS=1）与非 macOS 平台只记账不安装。</para>
/// <para><b>拆除时序</b>：两条通道都是「先收起、后拆监听」，而收起（控制器 Hide → Disarm）就发生在
/// 监听回调里面——在 AppKit 正派发监听时调 removeMonitor 属于未定义的自我修改。故回调期内的
/// Disarm 只记挂待拆标记，由回调出口统一执行拆除；回调期外的 Disarm 照旧同步拆。</para>
/// </summary>
internal static unsafe class MacPopoverOutsideClickMonitor
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // NSEventTypeLeftMouseDown=1 / RightMouseDown=3 / OtherMouseDown=25，掩码按类型号左移
    private const ulong MouseDownMask = (1UL << 1) | (1UL << 3) | (1UL << 25);
    // Block_private.h 的 BLOCK_IS_GLOBAL = 1<<28（真机 clang 真块实测 flags=0x50000000 =
    // IS_GLOBAL|HAS_SIGNATURE，签名位可省）；1<<29 是 BLOCK_USE_STRET——27a370f 首版放错位
    private const int BlockIsGlobal = 1 << 28;

    // dlfcn.h RTLD_DEFAULT（macOS = -2）
    private static readonly IntPtr RtldDefault = new IntPtr(-2);

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string selector);
    [DllImport(LibObjC)] private static extern IntPtr class_getClassMethod(IntPtr cls, IntPtr sel);
    [DllImport(LibSystem)] private static extern IntPtr dlsym(IntPtr handle, string symbol);

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

    private static IntPtr _globalMonitor, _localMonitor;
    private static IntPtr _globalBlock, _localBlock;
    private static IntPtr _selWindow;
    private static Action? _onOutsideClick;
    private static bool _armed;
    private static bool _inCallback;   // 正在派发监听回调（此时不得拆监听）
    private static bool _removePending; // 回调期内收到的 Disarm，留到出口执行

    /// <summary>armed 状态（headless 测试断言挂载/拆除接线；headless / 非 macOS 只记账不安装，
    /// 该标记仍如实反映「弹层可见期点击外部应收起」的意图）。</summary>
    public static bool IsArmed => _armed;

    /// <summary>进入弹层可见期：记账回调并确保两条监听已挂（幂等；安装失败静默回落——
    /// Esc / 再点 / 失焦三通道仍在，下次 Show 重试）。必须在 UI 线程调用（监听回调派发回挂载线程）。</summary>
    public static void Arm(Action onOutsideClick)
    {
        _onOutsideClick = onOutsideClick;
        _armed = true;
        EnsureInstalled();
    }

    /// <summary>离开可见期：拆回调与监听（幂等；系统级事件钩子不在弹层不可见期继续挂着）。
    /// 回调期内（点击外部的收起路径正是如此）只记挂待拆标记，由 <see cref="RunOutsideClick"/>
    /// 的出口执行——不在 AppKit 派发监听的过程中 removeMonitor。</summary>
    public static void Disarm()
    {
        _armed = false;
        _onOutsideClick = null;
        if (_inCallback)
        {
            _removePending = true;
            return;
        }

        RemoveMonitors();
    }

    /// <summary>全局监听回调（block invoke 直达；headless 测试亦经此直发模拟真实点击）：
    /// 本进程之外的鼠标按下即收起。消费制见 <see cref="RunOutsideClick"/>。</summary>
    internal static void HandleOutsideMouseDown() => RunOutsideClick();

    /// <summary>本地监听回调（本进程内的鼠标按下）。<paramref name="eventWindow"/> 为
    /// <c>[event window]</c>（nil 传 0）：
    /// <list type="bullet">
    /// <item>0 = 无窗口目标，即菜单栏空白处 / 菜单标题——正是失焦通道与全局监听都漏掉的那类
    /// 点击（真机探针留证），收起。</item>
    /// <item>非 0 = 自家有窗口的目标（弹层卡片、托盘状态项 NSStatusBarWindow、Avalonia popup）：
    /// 放行给既有通道处理（弹层内点击是业务动作，托盘点击是 Toggle），此处不插手。</item>
    /// </list>
    /// internal 供 headless 测试直发（headless 未装真实监听）。</summary>
    internal static void HandleOwnProcessMouseDown(nint eventWindow)
    {
        if (eventWindow != 0)
        {
            return;
        }

        RunOutsideClick();
    }

    /// <summary>统一的收起执行体：先摘记账（_armed/回调）再执行——后续点击落空、不会重复收起；
    /// 监听本体由控制器 Hide → Disarm 拆除（回调期内转为待拆标记，出口处再拆），回调异常也不影响
    /// 该收尾。</summary>
    private static void RunOutsideClick()
    {
        if (!_armed)
        {
            return;
        }

        var callback = _onOutsideClick;
        _armed = false;
        _onOutsideClick = null;
        _inCallback = true;
        try
        {
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 点击外部收起回调失败：{ex.Message}");
        }
        finally
        {
            _inCallback = false;
            if (_removePending)
            {
                _removePending = false;
                if (_armed)
                {
                    EnsureInstalled(); // 回调期间又布防了（极端重入）：监听本就在挂，保在挂
                }
                else
                {
                    RemoveMonitors();
                }
            }
        }
    }

    private static void RemoveMonitors()
    {
        var nsevent = OperatingSystem.IsMacOS() ? objc_getClass("NSEvent") : IntPtr.Zero;
        if (nsevent != IntPtr.Zero)
        {
            var removeMonitor = sel_registerName("removeMonitor:");
            RemoveMonitor(nsevent, removeMonitor, ref _globalMonitor);
            RemoveMonitor(nsevent, removeMonitor, ref _localMonitor);
        }

        _globalMonitor = IntPtr.Zero;
        _localMonitor = IntPtr.Zero;
    }

    private static void RemoveMonitor(IntPtr nsevent, IntPtr removeMonitor, ref IntPtr token)
    {
        if (token == IntPtr.Zero)
        {
            return;
        }

        try
        {
            MsgSend(nsevent, removeMonitor, token);
        }
        catch
        {
            // 拆除失败只留一个无效 token——下方复位后不会重复 remove
        }

        token = IntPtr.Zero;
    }

    private static bool EnsureInstalled()
    {
        if (_globalMonitor != IntPtr.Zero && _localMonitor != IntPtr.Zero)
        {
            return true; // 两条通道都在（只装上一条时下次 Arm 补另一条）
        }

        if (!OperatingSystem.IsMacOS()
            || Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1")
        {
            return false; // 只记账：测试宿主/非 macOS 不碰 AppKit，真机通道由下次 Arm 重试
        }

        try
        {
            // add/removeMonitor: 都是 NSEvent 的类方法（AppKit/NSEvent.h）——发给 NSApplication
            // 实例是 27a370f 首版真机炸点：unrecognized selector 的 ObjC 异常跨托管帧无法被
            // try/catch、直接 terminate（点托盘 Show→Arm 即崩）。class_getClassMethod 探测
            // （+respondsToSelector: 查的是实例方法，对类方法恒 NO）。
            var nsevent = objc_getClass("NSEvent");
            if (nsevent == IntPtr.Zero)
            {
                return false;
            }

            _selWindow = sel_registerName("window"); // 本地监听取 [event window]，缓存避免逐次注册

            if (_globalMonitor == IntPtr.Zero)
            {
                _globalBlock = _globalBlock != IntPtr.Zero
                    ? _globalBlock
                    : BuildBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OutsideMouseDownImp);
                _globalMonitor = AddMonitor(nsevent, "addGlobalMonitorForEventsMatchingMask:handler:", _globalBlock);
            }

            if (_localMonitor == IntPtr.Zero)
            {
                _localBlock = _localBlock != IntPtr.Zero
                    ? _localBlock
                    : BuildBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&OwnProcessMouseDownImp);
                _localMonitor = AddMonitor(nsevent, "addLocalMonitorForEventsMatchingMask:handler:", _localBlock);
            }

            return IsInstalled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 点击外部监听安装失败（失焦/Esc/再点通道兜底）：{ex.Message}");
            return false;
        }
    }

    private static bool IsInstalled() => _globalMonitor != IntPtr.Zero || _localMonitor != IntPtr.Zero;

    /// <summary>挂一条监听（类方法 + 块），失败返回 0 由调用方静默降级。</summary>
    private static IntPtr AddMonitor(IntPtr nsevent, string selector, IntPtr block)
    {
        var addMonitor = sel_registerName(selector);
        if (class_getClassMethod(nsevent, addMonitor) == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return MsgSend(nsevent, addMonitor, MouseDownMask, block);
    }

    /// <summary>构造零捕获全局块：invoke 指向静态 IMP，descriptor 常驻 HGlobal（进程生命周期，
    /// 与窗口复用策略同寿命，无需释放）。</summary>
    private static IntPtr BuildBlock(IntPtr invoke)
    {
        var descriptor = (BlockDescriptor*)Marshal.AllocHGlobal(sizeof(BlockDescriptor));
        descriptor->Reserved = IntPtr.Zero;
        descriptor->Size = (IntPtr)sizeof(BlockLiteral);

        var block = (BlockLiteral*)Marshal.AllocHGlobal(sizeof(BlockLiteral));
        *block = new BlockLiteral
        {
            // _NSConcreteGlobalBlock 是数据符号不是 ObjC 类，objc_getClass 恒 NULL（真机实测）——
            // isa 必须取符号地址本身（clang 真块 isa == dlsym 值，逐字节对照留证）；NULL isa 的块
            // 一经 objc_retain 即段错误（地址 0x20，真机复现）。
            Isa = dlsym(RtldDefault, "_NSConcreteGlobalBlock"),
            Flags = BlockIsGlobal,
            Invoke = invoke,
            Descriptor = (IntPtr)descriptor,
        };
        return (IntPtr)block;
    }

    /// <summary>全局监听块 invoke（签名 void (^)(NSEvent *)：第 0 参块自身、第 1 参事件——内容不用）。
    /// 仅主线程进入（回调派发回挂载线程 = UI 线程），异常托管侧吃干净。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OutsideMouseDownImp(IntPtr block, IntPtr nsevent)
    {
        HandleOutsideMouseDown();
    }

    /// <summary>本地监听块 invoke（签名 NSEvent * (^)(NSEvent *)）：取事件所属窗口后交给
    /// <see cref="HandleOwnProcessMouseDown"/>，并<b>原样返回事件</b>——本地监听可以改写或吞掉
    /// 事件，这里只观察，点击照常派发（返回 nil 会把菜单栏点击一并吃掉）。异常必须托管侧吃干净，
    /// 跨 ObjC 边界抛出即崩进程。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr OwnProcessMouseDownImp(IntPtr block, IntPtr nsevent)
    {
        try
        {
            if (nsevent != IntPtr.Zero)
            {
                HandleOwnProcessMouseDown(MsgSend(nsevent, _selWindow));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 本地鼠标监听处理失败：{ex.GetType().Name}: {ex.Message}");
        }

        return nsevent;
    }
}
