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
/// 子类 + object_setClass 装到实例上（KVO 同款经典手法），重写 <c>mouseUp:</c>（左键；super 调用
/// 保留系统高亮语义）与 <c>rightMouseDown:</c>（右键弹自有 NSMenu）。菜单项 target-action 指向
/// 同一个动态类实例，按 tag 回派托管命令；只用无 ivar 的类对（实例尺寸不变）+
/// [UnmanagedCallersOnly] 静态 IMP——不涉 ObjectiveCMarshal，无 GC 对象生命周期耦合。</para>
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
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, ulong arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, NSPoint arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, NSPoint arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, NSSize arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern long MsgSendLong(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint MsgSendPoint(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSRect MsgSendRect(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSendSuper")] private static extern void MsgSendSuper(ref ObjCSuper super, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjCSuper { public IntPtr Receiver; public IntPtr SuperClass; }

    // ── 缓存的 selector ──
    private static IntPtr SelButton, SelStatusItemWithLength, SelFrame, SelWindow, SelScreens,
        SelObjectAtIndex, SelSetImage, SelSetTemplate, SelSetToolTip, SelSetTarget, SelSetTag,
        SelSetEnabled, SelSetTitle, SelAddItem, SelSeparatorItem, SelSetAutoenables, SelAlloc,
        SelInit, SelInitWithUtf8, SelRelease, SelMouseUp, SelRightMouseDown, SelMenuAction,
        SelTag, SelMouseLocation, SelDataWithBytesLength, SelInitWithData, SelSetSize,
        SelMenuFontOfSize, SelPointSize, SelSystemStatusBar, SelInitWithTitle, SelInitWith3,
        SelPopUpMenu, SelGetSize;

    // ── 运行时状态（单状态项，进程生命周期）──
    private static IntPtr _button, _menu, _target;
    private static readonly List<IntPtr> NativeItems = new();
    private static IReadOnlyList<MacTrayItem>? _spec;
    private static Action? _leftClick;

    /// <summary>是否已安装（TrayService 据此走 interop 分支）。</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>菜单项描述（TrayService 持有同一份做标题重译 / 成本行回写；Command 为 null 且
    /// 非 separator = 纯标签行（今日成本））。</summary>
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
    /// false，调用方回落 Avalonia 托盘路径（托盘常驻语义不打折，只退回菜单项入口）。</summary>
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

        // 2. 从按钮的实际类派生子类（不假设恰好是 NSStatusBarButton），重写左键 / 右键 / 菜单 action
        var buttonClass = objc_allocateClassPair(object_getClass(_button), "TokenmetryTrayStatusButton", 0);
        if (buttonClass == IntPtr.Zero)
        {
            throw new InvalidOperationException("objc_allocateClassPair failed");
        }

        var impMouseUp = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ButtonMouseUpImp;
        var impRightDown = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ButtonRightMouseDownImp;
        var impMenuAction = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MenuItemActionImp;
        if (!class_addMethod(buttonClass, SelMouseUp, impMouseUp, "v@:@")
            || !class_addMethod(buttonClass, SelRightMouseDown, impRightDown, "v@:@")
            || !class_addMethod(buttonClass, SelMenuAction, impMenuAction, "v@:@"))
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

        // 4. 装子类到按钮实例（此后左右键事件进托管 IMP）
        _ = object_setClass(_button, buttonClass);

        // 5. 菜单（右键）：与 Avalonia 路径同构的成本行 + 分隔 + 功能项；autoenables 关闭——
        //    target 不在 responder 链上，默认自动启用会误灰，可用性由 setEnabled 显式控制
        _target = MsgSend(MsgSend(buttonClass, SelAlloc), SelInit);
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

    private static void CacheSelectors()
    {
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
        SelMouseUp = Sel("mouseUp:");
        SelRightMouseDown = Sel("rightMouseDown:");
        SelMenuAction = Sel("tokenmetryMenuItemAction:");
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
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // ── IMP（仅主线程进入；异常必须在托管侧吃干净，跨 ObjC 边界抛出即崩进程）──

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ButtonMouseUpImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            // super mouseUp：恢复系统高亮状态（target/action 未设，super 不会重复发 action）
            var super = new ObjCSuper { Receiver = self, SuperClass = class_getSuperclass(object_getClass(self)) };
            MsgSendSuper(ref super, SelMouseUp);
            _leftClick?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 托盘左键处理失败：{ex.Message}");
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

    /// <summary>图标按钮在 Avalonia 屏幕坐标系中的矩形（Cocoa 点 ≈ Avalonia DIP，macOS Scaling=1）。
    /// 供弹窗精确锚定（图标正下方、右缘对齐）。</summary>
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

        var buttonFrame = MsgSendRect(window, SelFrame);
        frame = ConvertCocoaFrameToAvaloniaCoords(
            buttonFrame.X, buttonFrame.Y, buttonFrame.Width, buttonFrame.Height, GetPrimaryScreenCocoaMaxY());
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
