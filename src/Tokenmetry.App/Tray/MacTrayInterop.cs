using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Tokenmetry.App.Tray;

/// <summary>
/// macOS 托盘 interop（D-016 升级台阶，用户拍板"点图标直接弹层"后实施，卡内留证）：Avalonia 的
/// <c>TrayIcon.Clicked</c> 在 macOS 从不 raise（官方文档 + trayicon.mm 无点击回调），点图标直接
/// 弹层必须绕开 Avalonia 托盘、经 ObjC runtime 自管状态项。
/// <para><b>机制</b>：自建 NSStatusItem → 取其 button 的实际类 → objc_allocateClassPair 派生
/// 子类 + object_setClass 装到实例上（KVO 同款经典手法），重写 <c>rightMouseDown:</c>（右键弹
/// 自有 NSMenu）。<b>左键走 target/action</b>：button 的 target 指向动态类实例、action 为
/// <c>tokenmetryTrayButtonAction:</c>——NSButton 在 mouseDown 跟踪循环内消费 mouse-up，
/// <c>mouseUp:</c> 重写根本不会被派发（B3 留证），target/action 是点击的唯一正规出口。菜单项
/// 同指该实例，按 tag 回派托管命令；只用无 ivar 的类对（实例尺寸不变）+ [UnmanagedCallersOnly]
/// 静态 IMP——不涉 ObjectiveCMarshal，无 GC 对象生命周期耦合。</para>
/// <para><b>坐标</b>：Cocoa 屏幕坐标纵轴向上，与 Avalonia（macOS 上为 Cocoa 点、纵轴向下）的
/// 换算对齐 Avalonia.Native 的 ConvertPointY 公式：<c>avaloniaY = 主屏 cocoa maxY − cocoaY</c>
/// （main.mm:560）。据此拿到图标精确 frame，弹窗锚定不再是"工作区右上近似"。</para>
/// <para><b>约束</b>：仅 Apple Silicon（arm64）验证过 ABI（结构体经寄存器传返；Intel 的
/// objc_msgSend_stret 路径未实现）；全部调用必须在主线程（Avalonia UI 线程），后台回写由
/// TrayService marshal；安装失败时调用方回落 Avalonia 托盘路径（菜单项入口兜底）。</para>
/// </summary>
internal static unsafe class MacTrayInterop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    // ── ObjC runtime 基础 ──
    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(LibObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(LibObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(LibObjC)] private static extern IntPtr object_setClass(IntPtr obj, IntPtr cls);
    [DllImport(LibObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(LibObjC)] private static extern IntPtr class_getSuperclass(IntPtr cls);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, ulong arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, NSPoint arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, NSPoint arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, NSSize arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern long MsgSendLong(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint MsgSendPoint(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSRect MsgSendRect(IntPtr receiver, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, Width, Height; }

    // ── 缓存的 selector ──
    private static IntPtr SelButton, SelStatusItemWithLength, SelFrame, SelWindow, SelScreens,
        SelObjectAtIndex, SelSetImage, SelSetTemplate, SelSetToolTip, SelSetTarget, SelSetAction,
        SelSetTag, SelSetEnabled, SelSetTitle, SelAddItem, SelSeparatorItem, SelSetAutoenables,
        SelAlloc, SelInit, SelInitWithUtf8, SelRelease, SelRightMouseDown, SelMenuAction,
        SelButtonAction, SelTag, SelMouseLocation, SelDataWithBytesLength, SelInitWithData,
        SelMenuFontOfSize, SelPointSize, SelSystemStatusBar, SelInitWithTitle,
        SelInitWith3, SelPopUpMenu, SelGetSize,
        SelSharedApp, SelSetAppIconImage, SelDelegate, SelShouldHandleReopen, SelUnhide;

    // ── 运行时状态（单状态项，进程生命周期）──
    private static IntPtr _button, _menu, _target;
    private static readonly List<IntPtr> NativeItems = new();
    private static IReadOnlyList<MacTrayItem>? _spec;
    private static Action? _leftClick;
    private static Action? _dockReopen;

    /// <summary>是否已安装（TrayService 据此走 interop 分支）。</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>菜单项描述（TrayService 持有同一份做语言切换标题重译；Command 为 null 且
    /// 非 separator = 纯标签行）。</summary>
    public sealed class MacTrayItem
    {
        public MacTrayItem(Func<string> title, Action? command = null, bool enabled = true, bool isSeparator = false)
        {
            Title = title;
            Command = command;
            Enabled = enabled;
            IsSeparator = isSeparator;
        }

        public Func<string> Title { get; }
        public Action? Command { get; }
        public bool Enabled { get; }
        public bool IsSeparator { get; }
    }

    // ── 安装 ──

    /// <summary>安装自定义状态项（图标 + tooltip + 左键接管 + 右键菜单）。任何异常捕获后返回
    /// false，调用方回落 Avalonia 托盘路径（托盘常驻不打折；macOS 该路径 Clicked 不 raise，
    /// 弹层无入口）。</summary>
    public static bool TryInstall(byte[] iconPng, string tooltip, IReadOnlyList<MacTrayItem> items, Action leftClick)
    {
        try
        {
            Install(iconPng, tooltip, items, leftClick);
            IsInstalled = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] macOS 托盘 interop 安装失败，回落 Avalonia 托盘：{ex.Message}");
            return false;
        }
    }

    private static void Install(byte[] iconPng, string tooltip, IReadOnlyList<MacTrayItem> items, Action leftClick)
    {
        _leftClick = leftClick;
        _spec = items;
        CacheSelectors();

        // 1. 状态项与按钮（方形单元格，同 Avalonia trayicon.mm 的选择：NSSquareStatusItemLength = -2）
        var statusBar = MsgSend(objc_getClass("NSStatusBar"), SelSystemStatusBar);
        var statusItem = MsgSend(statusBar, SelStatusItemWithLength, -2.0);
        _button = MsgSend(statusItem, SelButton);

        // 2. 从按钮的实际类派生子类（不假设恰好是 NSStatusBarButton），重写右键；左键走 target/action
        var buttonClass = objc_allocateClassPair(object_getClass(_button), "TokenmetryTrayStatusButton", 0);
        if (buttonClass == IntPtr.Zero)
        {
            throw new InvalidOperationException("objc_allocateClassPair failed");
        }

        var impRightDown = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ButtonRightMouseDownImp;
        var impMenuAction = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MenuItemActionImp;
        var impButtonAction = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&TrayButtonActionImp;
        if (!class_addMethod(buttonClass, SelRightMouseDown, impRightDown, "v@:@")
            || !class_addMethod(buttonClass, SelMenuAction, impMenuAction, "v@:@")
            || !class_addMethod(buttonClass, SelButtonAction, impButtonAction, "v@:@"))
        {
            throw new InvalidOperationException("class_addMethod failed");
        }

        objc_registerClassPair(buttonClass);

        // 3. 图标：模板资产（setTemplate YES，随明暗菜单栏自动反色，语义同 Avalonia SetIsTemplateIcon）
        var nsData = MsgSend(objc_getClass("NSData"), SelDataWithBytesLength, AllocBytes(iconPng), (ulong)iconPng.Length);
        var image = MsgSend(MsgSend(objc_getClass("NSImage"), SelAlloc), SelInitWithData, nsData);
        var menuFont = MsgSend(objc_getClass("NSFont"), SelMenuFontOfSize, 0.0);
        var iconHeight = Math.Floor(MsgSendDouble(menuFont, SelPointSize) * 1.333333);
        MsgSend(image, SelGetSize, new NSSize { Width = iconHeight, Height = iconHeight });
        MsgSend(image, SelSetTemplate, (IntPtr)1);
        MsgSend(_button, SelSetImage, image);
        MsgSend(image, SelRelease);

        SetTooltip(tooltip);

        // 4. 装子类到按钮实例，并设左键 target/action（NSButton 在 mouseDown 跟踪循环内消费
        //    mouse-up，mouseUp: 重写不会被派发——target/action 是点击的唯一正规出口，B3 留证）
        _target = MsgSend(MsgSend(buttonClass, SelAlloc), SelInit);
        _ = object_setClass(_button, buttonClass);
        MsgSend(_button, SelSetTarget, _target);
        MsgSend(_button, SelSetAction, SelButtonAction);

        // 5. 菜单（右键）：与 Avalonia 路径同构的成本行 + 分隔 + 功能项；autoenables 关闭——
        //    target 不在 responder 链上，默认自动启用会误灰，可用性由 setEnabled 显式控制
        _menu = MsgSend(MsgSend(objc_getClass("NSMenu"), SelAlloc), SelInitWithTitle, CreateNSString("Tokenmetry"));
        MsgSend(_menu, SelSetAutoenables, (IntPtr)0);
        for (var index = 0; index < items.Count; index++)
        {
            var spec = items[index];
            if (spec.IsSeparator)
            {
                var separator = MsgSend(objc_getClass("NSMenuItem"), SelSeparatorItem);
                NativeItems.Add(separator);
                MsgSend(_menu, SelAddItem, separator);
            }
            else
            {
                var item = MsgSend(MsgSend(objc_getClass("NSMenuItem"), SelAlloc),
                    SelInitWith3,
                    CreateNSString(spec.Title()),
                    spec.Command is null ? IntPtr.Zero : SelMenuAction,
                    CreateNSString(string.Empty));
                MsgSend(item, SelSetTag, (IntPtr)index);
                MsgSend(item, SelSetEnabled, spec.Command is not null ? (IntPtr)1 : (IntPtr)0);
                if (spec.Command is not null)
                {
                    MsgSend(item, SelSetTarget, _target);
                }

                NativeItems.Add(item);
                MsgSend(_menu, SelAddItem, item);
                MsgSend(item, SelRelease); // 菜单已 retain，释放我们的引用
            }
        }
    }

    /// <summary>幂等：TrySetDockIcon 与 Install 都可能首次触发，重复调用无害。</summary>
    private static void CacheSelectors()
    {
        if (SelButton != IntPtr.Zero)
        {
            return;
        }

        SelButton = Sel("button");
        SelStatusItemWithLength = Sel("statusItemWithLength:");
        SelFrame = Sel("frame");
        SelWindow = Sel("window");
        SelScreens = Sel("screens");
        SelObjectAtIndex = Sel("objectAtIndex:");
        SelSetImage = Sel("setImage:");
        SelSetTemplate = Sel("setTemplate:");
        SelSetToolTip = Sel("setToolTip:");
        SelSetTarget = Sel("setTarget:");
        SelSetAction = Sel("setAction:");
        SelSetTag = Sel("setTag:");
        SelSetEnabled = Sel("setEnabled:");
        SelSetTitle = Sel("setTitle:");
        SelAddItem = Sel("addItem:");
        SelSeparatorItem = Sel("separatorItem");
        SelSetAutoenables = Sel("setAutoenablesItems:");
        SelAlloc = Sel("alloc");
        SelInit = Sel("init");
        SelInitWithUtf8 = Sel("initWithUTF8String:");
        SelRelease = Sel("release");
        SelRightMouseDown = Sel("rightMouseDown:");
        SelMenuAction = Sel("tokenmetryMenuItemAction:");
        SelButtonAction = Sel("tokenmetryTrayButtonAction:");
        SelTag = Sel("tag");
        SelMouseLocation = Sel("mouseLocation");
        SelDataWithBytesLength = Sel("dataWithBytes:length:");
        SelInitWithData = Sel("initWithData:");
        SelGetSize = Sel("setSize:");
        SelMenuFontOfSize = Sel("menuFontOfSize:");
        SelPointSize = Sel("pointSize");
        SelSystemStatusBar = Sel("systemStatusBar");
        SelInitWithTitle = Sel("initWithTitle:");
        SelInitWith3 = Sel("initWithTitle:action:keyEquivalent:");
        SelPopUpMenu = Sel("popUpMenuPositioningItem:atLocation:inView:");
        SelSharedApp = Sel("sharedApplication");
        SelSetAppIconImage = Sel("setApplicationIconImage:");
        SelDelegate = Sel("delegate");
        SelShouldHandleReopen = Sel("applicationShouldHandleReopen:hasAnyVisibleWindows:");
        SelUnhide = Sel("unhide:");
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // ── IMP（仅主线程进入；异常必须在托管侧吃干净，跨 ObjC 边界抛出即崩进程）──

    /// <summary>左键 action（button target-action，跟踪循环结束发 action；仅主线程进入，
    /// 异常必须在托管侧吃干净——跨 ObjC 边界抛出即崩进程）。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void TrayButtonActionImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            _leftClick?.Invoke();
        }
        catch (Exception ex)
        {
            // 堆栈必须留：B4 曾遇生产独有 NRE（headless 全绿、真机炸），只有 Message 无法定位
            Console.Error.WriteLine($"[Tokenmetry] 托盘左键处理失败：{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ButtonRightMouseDownImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            PopUpMenu();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 托盘右键处理失败：{ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MenuItemActionImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            var tag = MsgSendLong(sender, SelTag);
            if (_spec is { } spec && tag >= 0 && tag < spec.Count)
            {
                spec[(int)tag].Command?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 托盘菜单项处理失败：{ex.Message}");
        }
    }

    // ── 运行时操作（主线程）──

    /// <summary>右键弹菜单（光标位置，阻塞至菜单关闭；与原生菜单栏菜单同一弹出机制）。</summary>
    public static void PopUpMenu()
    {
        if (_menu == IntPtr.Zero)
        {
            return;
        }

        var location = MsgSendPoint(objc_getClass("NSEvent"), SelMouseLocation);
        MsgSend(_menu, SelPopUpMenu, IntPtr.Zero, location, IntPtr.Zero);
    }

    /// <summary>更新按钮 tooltip。</summary>
    public static void SetTooltip(string tooltip)
    {
        if (_button != IntPtr.Zero)
        {
            SetNSString(_button, SelSetToolTip, tooltip);
        }
    }

    /// <summary>按安装时的 items 序号更新菜单项标题（重译 / 成本行回写）。</summary>
    public static void SetItemTitle(int index, string title)
    {
        if (index >= 0 && index < NativeItems.Count)
        {
            SetNSString(NativeItems[index], SelSetTitle, title);
        }
    }

    /// <summary>设置 Dock 图标（D-018）：裸可执行（无 .app bundle）运行时 Avalonia 不设 Dock
    /// 图标（Window.Icon 只影响窗口语义），经 ObjC 通道把 Apple 网格 PNG（app-icon.png）喂给
    /// <c>NSApplication.applicationIconImage</c>。必须主线程调用；失败返回 false 由调用方静默
    /// 降级（Dock 图标缺失不影响功能）。若未来改走 .app bundle + Info.plist icns，本调用应删除
    /// （bundle 场景 Dock 图标归 Info.plist，setApplicationIconImage: 反而会覆盖它）。</summary>
    public static bool TrySetDockIcon(byte[] iconPng)
    {
        try
        {
            // 先于托盘 Install 执行（App 启动序），selector 必须先就位：空 selector 在
            // objc_msgSend 的缓存查找里会命中空缓存桶 {imp=0, sel=0}——sel 地址即缓存键，
            // 0 与空桶比较相等，直接跳 imp=0 → PC=0x0 段错误（而非 unrecognized selector）。
            CacheSelectors();
            var app = MsgSend(objc_getClass("NSApplication"), SelSharedApp);
            if (app == IntPtr.Zero)
            {
                return false;
            }

            var nsData = MsgSend(objc_getClass("NSData"), SelDataWithBytesLength,
                AllocBytes(iconPng), (ulong)iconPng.Length);
            var image = MsgSend(MsgSend(objc_getClass("NSImage"), SelAlloc), SelInitWithData, nsData);
            MsgSend(app, SelSetAppIconImage, image);
            MsgSend(image, SelRelease);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] Dock 图标 interop 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>标准状态单元尺寸（Apple Human Interface Guidelines：菜单栏图标 24×24 可视区）。
    /// NSStatusBarButton 的 frame 往往更高（含命中扩展区，实测高于可视单元十余磅），直接用它锚定
    /// 弹窗会悬空——钳到可视单元。</summary>
    private const double VisibleCellSide = 24;

    /// <summary>Dock 图标点击唤回主窗（B-018）。真机取证推翻了"挂 delegate 即可"的初始假设，
    /// 两条实锤（对照实验与运行时埋点留证于卡内）：<list type="number">
    /// <item>上游 AvnAppDelegate 实现的是拼写错误的 <c>applicationShouldHandleReopen:hasVisibleWindows:</c>
    /// （缺 "Any"，app.mm 死代码），AppKit 按正确名字查找永远落空；</item>
    /// <item>macOS 26 上裸可执行进程（dotnet run / 非 bundle）即使补挂正确 selector，AppKit 的
    /// reopen 处理也不咨询 delegate——原生对照应用同样复现，故委托通道只能作纵深。</item></list>
    /// 因此主通道为自有 NSAppleEventManager 'rapp' handler：事件一到直接回调 ShowWindow。
    /// <para><b>时序约束</b>：AEM 同一事件只保有一个 handler，注册即替换；NSApplication 在
    /// finishLaunching 才自装 'rapp' handler，会顶掉更早注册的 ours——调用方必须把本方法
    /// Post 到 run loop 首个空闲点（晚于 finishLaunching），不得在启动期同步调用。</para>
    /// <para>回调即 <see cref="TrayButtonActionImp"/> 同约定：主线程（AEM 主线程派发 = Avalonia
    /// UI 线程）直调、异常托管侧吃干净；失败只降级为现状（Dock 死点）不阻塞启动。</para></summary>
    public static bool TryInstallDockReopen(Action onReopen)
    {
        try
        {
            // selector 先就位（同 TrySetDockIcon 的空 selector 段错误教训）
            CacheSelectors();
            var app = MsgSend(objc_getClass("NSApplication"), SelSharedApp);
            var delegateObject = app == IntPtr.Zero ? IntPtr.Zero : MsgSend(app, SelDelegate);
            if (delegateObject == IntPtr.Zero)
            {
                Console.Error.WriteLine("[Tokenmetry] Dock reopen 接线跳过：app delegate 未就位");
                return false;
            }

            _dockReopen = onReopen;
            var delegateClass = object_getClass(delegateObject);

            // 主通道：AEM 'rapp' handler 挂在 delegate 对象的类上（其生命周期与进程一致）。
            var selAEMHandler = Sel("tokenmetryReopenAppleEvent:withReplyEvent:");
            var impAE = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DockReopenAEImp;
            if (!class_addMethod(delegateClass, selAEMHandler, impAE, "v@:@@"))
            {
                Console.Error.WriteLine("[Tokenmetry] Dock reopen AEM handler 挂载失败（降级为现状）");
                return false;
            }

            var eventManager = MsgSend(objc_getClass("NSAppleEventManager"), Sel("sharedAppleEventManager"));
            // kCoreEventClass 'aevt' = 0x61657674；kAEReopenApplication 'rapp' = 0x72617070
            MsgSend(eventManager, Sel("setEventHandler:andSelector:forEventClass:andEventID:"),
                delegateObject, selAEMHandler, (IntPtr)0x61657674, (IntPtr)0x72617070);

            // 纵深通道：正确拼写的 reopen 委托方法。上游只有拼错的死实现，add 必成；若未来
            // Avalonia 自己接对了名字，add 失败即跳过，不替换他人语义。
            var imp = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte, byte>)&DockReopenImp;
            if (!class_addMethod(delegateClass, SelShouldHandleReopen, imp, "B@:@B"))
            {
                Console.Error.WriteLine("[Tokenmetry] Dock reopen：delegate 已有 reopen 实现（Avalonia 更新？），跳过委托通道");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] Dock reopen interop 失败（降级为现状）：{ex.Message}");
            return false;
        }
    }

    /// <summary>reopen IMP（仅主线程进入；返回 YES = 已处理）。hasVisibleWindows 参数不区分对待：
    /// 回调统一走 ShowWindow（隐藏 → 唤回 / 可见 → 带到前台 / 最小化 → 脱最小化），与托盘
    /// 「打开主窗」、B-013 第二实例激活同一语义；弹层开着时主窗激活引发既有失焦通道自然收起。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte DockReopenImp(IntPtr self, IntPtr sel, IntPtr sender, byte hasVisibleWindows)
    {
        try
        {
            _dockReopen?.Invoke();
        }
        catch (Exception ex)
        {
            // 堆栈必须留：跨 ObjC 边界抛出即崩进程（同 TrayButtonActionImp 纪律）
            Console.Error.WriteLine($"[Tokenmetry] Dock reopen 处理失败：{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return 1; // YES
    }

    /// <summary>reopen Apple Event IMP（NSAppleEventManager 通道，AEM 主线程派发）。签名固定
    /// (event, reply) 双对象参数——setEventHandler:andSelector: 的回调约定。委托通道在 macOS 26
    /// 裸可执行进程整体失灵（对照实验在案），此通道是 B-018 的实际生效路径。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DockReopenAEImp(IntPtr self, IntPtr sel, IntPtr eventDescr, IntPtr replyDescr)
    {
        try
        {
            // 通道替换掉了 NSApplication 自装的 'rapp' handler，应用级隐藏（cmd-H）的默认唤回
            // 也随之丢失——unhide 对未隐藏进程是无害 no-op，补上覆盖。
            var app = MsgSend(objc_getClass("NSApplication"), SelSharedApp);
            if (app != IntPtr.Zero)
            {
                MsgSend(app, SelUnhide, IntPtr.Zero);
            }

            _dockReopen?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] Dock reopen 处理失败：{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>图标按钮在 Avalonia 屏幕坐标系中的矩形（Cocoa 点 ≈ Avalonia DIP，macOS Scaling=1）。
    /// 取宿主状态窗口 frame 后**顶对齐钳到可视单元**：状态窗口含命中扩展（实测比可视单元高十余磅、
    /// 悬垂到菜单栏之下），直接用会让弹窗悬空；窗口顶 = 可视图标顶（均为栏内上缘），钳高即可。
    /// 锚定"图标正下方、左缘对齐右弹"（B4；窗口左缘与 AX 可视图标左缘实测差 7px，目检基准）。</summary>
    public static bool TryGetStatusItemFrame(out PixelRect frame)
    {
        frame = default;
        if (!IsInstalled || _button == IntPtr.Zero)
        {
            return false;
        }

        var window = MsgSend(_button, SelWindow);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var winFrame = MsgSendRect(window, SelFrame);   // 屏幕 cocoa 坐标（纵轴向上）
        var rect = ConvertCocoaFrameToAvaloniaCoords(
            winFrame.X, winFrame.Y, winFrame.Width, winFrame.Height, GetPrimaryScreenCocoaMaxY());
        frame = new PixelRect(rect.X, rect.Y, rect.Width, (int)Math.Min(rect.Height, VisibleCellSide));
        return true;
    }

    /// <summary>Cocoa（纵轴向上）→ Avalonia（macOS 上同为 Cocoa 点、纵轴向下）frame 换算，
    /// 公式对齐 Avalonia.Native ConvertPointY：<c>avaloniaY = 主屏 cocoa maxY − cocoaY</c>。
    /// internal 纯函数供测试对账（double 入参——NSRect 为私有布局类型）。</summary>
    internal static PixelRect ConvertCocoaFrameToAvaloniaCoords(
        double x, double y, double width, double height, double primaryScreenCocoaMaxY)
    {
        var top = primaryScreenCocoaMaxY - (y + height);
        return new PixelRect(
            (int)Math.Round(x),
            (int)Math.Round(top),
            (int)Math.Round(width),
            (int)Math.Round(height));
    }

    private static double GetPrimaryScreenCocoaMaxY()
    {
        var screens = MsgSend(objc_getClass("NSScreen"), SelScreens);
        var first = MsgSend(screens, SelObjectAtIndex, (IntPtr)0);
        var frame = MsgSendRect(first, SelFrame);
        return frame.Y + frame.Height; // NSMaxY
    }

    // ── 内存 / NSString 小助手 ──

    private static IntPtr AllocBytes(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static IntPtr CreateNSString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var buffer = Marshal.AllocHGlobal(utf8.Length + 1); // +1：NUL 结尾
        try
        {
            Marshal.Copy(utf8, 0, buffer, utf8.Length);
            Marshal.WriteByte(buffer, utf8.Length, 0);
            return MsgSend(MsgSend(objc_getClass("NSString"), SelAlloc), SelInitWithUtf8, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>临时 NSString 调用后立即 release（手工配对，不依赖外层 autorelease pool）。</summary>
    private static void SetNSString(IntPtr receiver, IntPtr selector, string value)
    {
        var nsstring = CreateNSString(value);
        try
        {
            MsgSend(receiver, selector, nsstring);
        }
        finally
        {
            MsgSend(nsstring, SelRelease);
        }
    }
}
