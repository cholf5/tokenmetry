---
id: D-016
title: 状态栏快捷信息窗 · 壳层（macOS 顶部锚定 / Windows 托盘 flyout，内容复用 D-015）
type: feature
priority: P2
size: S
status: done
created: 2026-10-17
updated: 2026-10-18
blocked: none
---

## Goal

把 D-015 的内容层挂到两平台托盘点击：点击托盘图标弹无边框 popover 窗，失焦/Esc/再点收起。壳层零业务逻辑，内容 100% 复用共享 UserControl。

## Background

- `TrayIcon.Clicked` 两平台都不带坐标（2026-10-17 会话核实），锚点方案分平台：
  - **Windows**：点击瞬间光标就在图标上——`GetCursorPos` 一行 P/Invoke 拿屏幕坐标，flyout 窗定位其上方（右缘贴屏幕边收口）；`Deactivated → Hide` 实现失焦收起。
  - **macOS**：先行纯 Avalonia——无边框窗定位主屏顶部靠右（零 interop，锚点近似；menu bar 工具早期常用做法）；若真机锚点偏差不可接受，升级为只取 `NSStatusItem.button.frame` 的最小 interop（一个 CGRect，卡内记录证据后决策）。
- 交互分工不变：托盘**左键**弹 popover，**右键**保留现有 NativeMenu（打开主窗 / 立即刷新 / 设置 / 退出）；popover 内「打开完整统计」走主窗唤回（`ShowWindow` 既有路径）。
- 窗形态：`Transparent` + `CornerRadius` 无边框窗（同原型 438px 宽、圆角、投影层级）；不进任务栏（`ShowInTaskbar=false`）；不算"主窗"——主窗生命周期、关窗分派（S-005）不受影响。
- 复用边界：内容层 100% 来自 D-015；本卡新增代码只有定位/收起/主题跟随，两平台各一个薄壳类。

## Acceptance Criteria

- [x] macOS：**点托盘图标直接 Toggle 弹窗**（CodexBar 同款，B2 升级项实施——自管 NSStatusItem interop，见 Dev Log），右键弹原生菜单；失焦 / Esc / 再点收起
- [x] Windows：托盘**左键**直接 Toggle 弹窗（Win32 WM_LBUTTONUP → Clicked 真实触发，源码核实）；右键菜单不变；菜单项入口冗余共享
- [x] 壳层零业务逻辑、零数据查询（只装配 D-015 UserControl）；主题随应用明暗设置
- [x] 主窗唤回 / 托盘菜单 / S-005 关窗分派行为零变化（回归锁）
- [ ] 真机目检：明暗 × 1x/2x 截图留档（headless 只验装配与收起逻辑）——移交用户

## Subtasks

- [x] PopoverWindow 壳（无边框 / 透明 / 失焦收起 / Esc）
- [x] macOS 锚点（近似定位先行，必要时 CGRect interop）+ Windows `GetCursorPos` 锚点
- [x] TrayIcon.Clicked 左键分派改造（弹 popover 取代直接 ShowWindow；NativeMenu 不动）
- [x] headless 回归（托盘菜单 / 主窗唤回 / S-005 关窗三态不受影响）+ 真机截图（截图待用户）

## Dependencies

- D-015（内容层）；D-014 先行更佳但非硬依赖

## Test Cases

### TC-001: 托盘左键分派（headless）

Steps: 构造 TrayService + popover 壳 → 触发 Clicked → 断言 popover 窗显示且主窗未被动；再触发收起。

### TC-002: 既有行为回归

Steps: 全量 `dotnet test Tokenmetry.sln`（重点 TrayServiceTests / CloseToTrayBehaviorTests / AppShellTests）。Expected: 全绿。

### TC-003: 真机目检

Steps: macOS / Windows 真机点击托盘，明暗两态 × 1x/2x 截图。Expected: 锚点合理、圆角投影正常、收起及时。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Development Log

### 2026-10-17

- 建卡（状态栏路线定稿）：壳层与内容层（D-015）分卡；两平台壳都按「零 interop 先行，锚点不准再上最小 interop」的台阶设计，升级需卡内留证。

### 2026-10-17（实施收口，与 D-015 同日同批落地）

- **落地文件**：`Tray/StatusPopoverWindow.axaml(.cs)`（无边框透明壳：SystemDecorations=None + TransparencyLevelHint=Transparent，宽 498 = 内容卡 438 + 投影边距，投影色经代码 Behind 按 ActualThemeVariant 取令牌 `AppPopoverShadowColor`——BoxShadow 颜色部分无法走 DynamicResource，颜色本体仍在令牌层）、`Tray/StatusPopoverController.cs`（懒建唯一窗 + Toggle + 双平台锚定 + 弹层内点击 → 唤回主窗）、`Tray/PopoverPositioner.cs`（锚定纯函数，headless 直测）、`Tray/TrayService.cs`（左键分派 `OnTrayClicked`：有控制器 → Toggle，未装配维持 A-0 直开主窗；右键 NativeMenu 不动）、`ViewModels/MainWindowViewModel.cs`（`OpenSessionsForAgent` 转 public——D-009 既有路由的公开，非新增）、`App.axaml.cs`（启动接线）。
- **坐标单位取证（实现前核实 Avalonia 12.1.4 源码）**：`Screen.Bounds/WorkingArea` 与 `Window.Position` 平台内同空间——Windows 全链路物理像素（`GetWindowRect/SetWindowPos` 原样），macOS 全链路 Cocoa 点（NSScreen.frame，Scaling=1）。锚定统一公式：窗口 DIP 尺寸 × `screen.Scaling` 换入屏幕坐标系；`GetCursorPos` 物理像素在 Windows 直接可用。
- **交互时序**：Show → 定位 → Activate（首帧后定位，SizeToContent=Height 需布局完成才知窗高）；收起三通道 = 再点托盘（Toggle）/ Deactivated → Hide / Esc → Hide；Hide 不 Close——OnLastWindowClose 不会把弹层收起误判为退出（S-005 语义不受影响）。
- **有意的近似**：macOS 锚点为主屏工作区（menu bar 之下）右上 + 8/6px，不追真实图标位置（零 interop 台阶第一级）；真机偏差不可接受时按卡升级 `NSStatusItem.button.frame` 最小 interop（需留证）。

### 2026-10-17（真机反馈 B2：「状态栏跟以前一样」→ macOS 点击事件取证与入口修正）

- **用户真机反馈看不到弹层**。取证（Avalonia 12.1.4 源码）：
  1. `src/Avalonia.Controls/TrayIcon.cs` 对 `Clicked` 事件文档原文——"Note, this is only supported on Win32 and some Linux DEs, **on OSX this event is not raised**"；
  2. `native/Avalonia.Native/src/OSX/trayicon.mm`（121 行全文）没有任何点击回调——`SetMenu` 直接 `[_native setMenu:]`，NSStatusItem 一旦挂菜单，**任何**鼠标按下都由系统打开菜单，按钮的 action/target 被接管；
  3. 反证 Windows：`src/Windows/Avalonia.Win32/TrayIconImpl.cs` `WM_LBUTTONUP → OnClicked`、`WM_RBUTTONUP → 菜单`，与本卡交互设计完全一致。
- **结论**：本卡 Background 里「托盘左键弹 popover / 右键保留 NativeMenu」的两键分工在 **Windows 成立、macOS 不可能**（零 interop 下无左键事件可接）。原 A-0 时代的 macOS 主窗唤回实际一直走菜单「打开主窗」项，Clicked→ShowWindow 路径在 macOS 从未生效过（headless 验证的是接线逻辑，验不出平台差异——教训记录）。
- **落地修正（零 interop 台阶内）**：托盘菜单在成本行下方新增「快捷信息窗」项（`TrayQuickInfo`，仅装配弹层控制器时出现）→ 命令 Toggle 弹层。macOS：点图标开菜单（与以前一致）→ 菜单项进弹窗；Windows：左键直弹 + 菜单项冗余共享。语言切换就地重译（不换菜单实例，沿用 A-0 纪律）。
- **升级项（待用户决策，不开即不做）**：macOS 实现"点图标直接弹层"需**放弃 Avalonia TrayIcon 在 macOS 的菜单挂载**，经 .NET Objective-C interop 自建 NSStatusItem（自管 mouseDown/mouseUp + 右键弹自有菜单）——远超本卡预授权的"一个 CGRect"锚定 interop，涉及真菜单替换与点击语义自管，风险与工作量都是独立卡级别。当前菜单项入口已可用，升级需求由真机使用体验决定。

### 2026-10-17（B2 升级项实施：用户拍板"点图标直接弹层，CodexBar 同款" → NSStatusItem interop 落地）

- **授权与范围变化**：用户真机用后明确要 CodexBar 交互（点图标出面板，拒绝菜单项中转）——上一条"待用户决策"的升级项就此授权，超出原 CGRect 预授权范围，按卡留证实施。
- **机制**（`Tray/MacTrayInterop.cs`）：自建 `NSStatusItem`（NSSquareStatusItemLength）→ 取其 button 的**实际类** → `objc_allocateClassPair` 派生无 ivar 子类 + `object_setClass` 装到实例（KVO 同款手法）→ 重写 `mouseUp:`（左键 Toggle 弹层；先调 super 保系统高亮语义）与 `rightMouseDown:`（弹自有 NSMenu）。菜单项 target-action 全指向动态类实例、按 tag 回派托管命令（与 Avalonia 菜单同构：成本行 + 快捷信息窗 + 打开主窗/立即刷新/设置/退出）。仅用 `[UnmanagedCallersOnly]` 静态 IMP——不涉 ObjectiveCMarshal，无 GC 生命周期耦合。`setAutoenablesItems:NO` + 显式 `setEnabled`（target 不在 responder 链上，默认自动启用会误灰）。
- **图标**：复用 `tray-icon-template.png` 模板资产（NSData→NSImage→setSize（菜单字号×4/3，同 Avalonia SetIcon 公式）→setTemplate:YES，随明暗反色）；tooltip 经 `setToolTip:` 真实可渲染。
- **精确锚定（顺带解决近似问题）**：`[[button window] frame]` → Cocoa→Avalonia 换算（对齐 Avalonia.Native `ConvertPointY`：`avaloniaY = 主屏 cocoa maxY − cocoaY`，main.mm:560 已核实）→ `PopoverPositioner.AnchorBelow`（图标正下方 4px、右缘过图标 8px，工作区钳制）。macOS 不再"工作区右上近似"。
- **双轨与回落**：`TrayService` 按 `OperatingSystem.IsMacOS() && !TOKENMETRY_HEADLESS` 分派——mac 生产走 interop（安装异常自动回落 Avalonia 托盘 + stderr 诊断，托盘常驻语义不打折）；Windows 与 headless 测试宿主走 Avalonia 托盘（测试基建在 SemiTestAppBuilder 打 `TOKENMETRY_HEADLESS=1` 标记）。
- **验证**：headless 全量 1314 Passed（+2：AnchorBelow 钳制 / Cocoa→Avalonia 换算纯函数对账）；真机冒烟——进程存活、stderr 无回落消息，System Events（辅助功能授权）查到进程 menu bar 2 **有且仅有一个 "status menu" 状态项**（interops 安装成功的客观证据；若回落会同时出现 Avalonia 兜底项与诊断消息）。左键/右键/锚点的最终目检移交用户。
- **约束记录**：仅 Apple Silicon（arm64）验证过 ABI（NSRect/NSPoint 经寄存器传返；Intel 的 objc_msgSend_stret 路径未实现——如需 Intel 支持再立卡）；Cocoa 调用全在主线程，DashboardViewModel 后台回写经 `RunOnUIThread` marshal；NSMenuItem 手工 retain/release 配对（不依赖外层 autorelease pool）。

### 2026-10-18（B3：真机反馈"左键没反应"两连修 + 锚定落位核实）

- **B3-1 左键无反应**：右键菜单正常（interop 链路通），左键死——取证 AppKit 行为：NSButton（NSStatusBarButton 的基类）在 `mouseDown:` 内自进跟踪循环并把 mouse-up 消费掉，**子类重写 `mouseUp:` 根本不会被派发**（首版实现踩坑）；点击的正规出口是 **target/action**。修正：删 mouseUp 重写，`[button setTarget:setAction:]` 指向动态类实例的 `tokenmetryTrayButtonAction:` IMP → 左键 Toggle。
- **B3-2 空 selector 段错误**：修正过程中 `SelSetAction` 声明了漏赋值，`objc_msgSend` 收到 NULL selector → SIGSEGV 三连崩（.ips 留档）。教训：selector 缓存集中一处、按名字逐一赋值，无编译期防护。
- **B3-3 锚定落位（探针实测闭环）**：System Events AXPress 模拟点击 + AX 读窗位，三轮探针拿到真值——状态窗口 frame (2118,1410,38,30)（cocoa）= 顶贴菜单栏顶（top-down y=0、高 30=栏高），AX 可视图标 (2125,3,24,24)；锚定取窗口 frame 顶对齐钳到 24 可视单元（HGT 标准单元）。**位置不再变化的原因 = AppKit constrainFrameRect**：普通窗口被强制压到菜单栏之下（设 y=14 被改写为 30=栏底），不受 Avalonia/应用控制。最终形态：弹窗顶=栏底（30），可见卡片顶=栏底+6px（ShadowMargins.Top=6，兼作投影晕染），右缘对齐图标——CodexBar 观感。AXPress 三连击验证 开→收→开 + 位置落位。
- **测试**：headless 全量 309 绿 + 全仓 1314 绿；修复 `Reload_populates_kpi_segments_and_top_agents` 的日期敏感缺陷（fixture "最近活动"固定 10-17 20:00，跨零点成"昨日"→产品逻辑如实留空→断言失败；改相对当前时刻）。

### 2026-10-18（B4：弹窗水平位置改右弹优先——用户反馈"太靠左"）

- **反馈与方向确认**：旧 `AnchorBelow` 右缘对齐图标右缘（+8 overlap）使卡片整体向图标**左侧**延伸，观感"太靠左"；Mac 惯例（NSPopover 系）是卡片左缘贴图标左缘、向右下展开。语义改为右弹优先：`cardX = anchor.X`，右缘越出工作区才整体左移贴工作区右缘、超宽钳左缘 ≥ 工作区左——后两步由既有 `ClampToArea` 承担，纯函数体只改 x 基准；`rightOverlap` 参数删除（右弹语义下无意义）。Windows 的 `AnchorAboveCursor` 分支不动（托盘贴屏幕底部，光标上方向左展开是平台惯例）。
- **锚定基准二选一（目检基准）**：采用 interop 返回的状态窗口 frame 左缘（B3 实测比 AX 可视图标左缘偏左 7px）——弹窗覆盖整个状态项命中区；若目检想贴可视图标，改 `TryGetStatusItemFrame` 即可（注释已留）。
- **取证基建**：`TrayButtonActionImp` 失败日志从 Message 升级为 `类型: Message + StackTrace`——B5 的生产独有 NRE 只有 Message 无法定位的教训反哺。
- **真机核账**：AXPress 开→收→开三连通过；图标可视单元 (2092,3,24,24) → 弹窗 (2055,30,498,477)：卡片左缘 = 窗左+边距 30 = 2085 = 状态窗口左缘（右弹成立）、右缘 2523 < 2560 未触钳制、Y=30=栏底（constrainFrameRect 既定）。

### 2026-10-18（B5：毛玻璃——两平台一套 AcrylicBlur + 协商失败回落不透明）

- **平台结论（先评估后动手）**：Avalonia 12.1.4 的 `TransparencyLevelHint=AcrylicBlur` 是跨平台一等能力（macOS Avalonia.Native vibrancy / Windows WinRT backdrop + AccentPolicy 兼容路径，Win10 1803+/Win11 均覆盖，本地包 DLL 符号取证），零 interop——"只有 Mac 能做"不成立。平台统一性：两平台对瞬态浮层各有原生材质（macOS vibrancy / Fluent acrylic——微软规范 transient surface 用 acrylic、长驻窗口用 Mica），让各平台渲染各自材质即设计语言层的统一；跨平台不变锚是内容层（信息架构/令牌/交互）。
- **实现**：`StatusPopoverWindow` hint 改 `AcrylicBlur`（`SystemDecorations=None` + 透明底不变，回落时窗体仍透明承载投影）；内容卡新增 `popover-card.acrylic` 变体样式（`AppPopoverCardBackgroundBrush` 半透明面，ThemeDictionaries 明暗各一份 `#E6FFFFFF`/`#E6111C2E`，与 `AppPanelBrush` 同基调——回落不透明时观感连续，DesignTokenTests 期望表同步）；壳层 code-behind 按 `ActualTransparencyLevel` 切类：AcrylicBlur 生效 → 加 acrylic 类，协商失败（系统透明效果关闭 / RDP / 旧 Win10 / 平台不支持 → 回落 Transparent/None）不切类保持不透明，文字对比度不破。macOS 辅助功能"减少透明度"与 Windows"透明效果"开关是对称回落情形，回落分支两平台共同需要。
- **踩坑（生产独有，headless 全绿不可见）**：Avalonia.Native 对 hint **即设即报**——XAML inflate 期（构造函数内、`InitializeComponent` 尚未给 `ShadowHost` 字段赋值）`ActualTransparencyLevel` 就经 `OnPropertyChanged` 同步到达（堆栈：set_TransparencyLevelHint → TopLevelImpl.SetTransparencyLevelHint → set_TransparencyLevel → HandleTransparencyLevelChanged → OnPropertyChanged 全同步），首版 `UpdateAcrylicCard` 直接 `ShadowHost.GetVisualDescendants()` NRE，被托盘左键 catch 吃掉后回落开主窗。修复：`ShadowHost?.` 空守卫跳过，OnOpened 与其后真实变更用当前值重评（初始协商不丢）。定位靠新加的堆栈日志（B4 条目）。
- **有意不做**：Win11 官方瞬态 backdrop（`DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_TRANSIENTWINDOW`，单调用 P/Invoke）v1 不做——Avalonia 原生路径视觉足够，不开 interop 战线；毛玻璃铺满窗口矩形（含 6/30/34px 投影边距带）的 fringe 观感待目检，不佳再收窄边距（留目检决策，不预设）；卡片刷透明度 90% 为目检起点。
- **验证**：headless 全量 1314 绿；真机 AXPress 三连 + 落位核账全过、stderr 干净；acrylic 渲染观感（明暗 × 1x/2x）headless 不可验证，移交用户目检。

### 2026-10-18（B6：毛玻璃观感返工——"整面板即原生毛玻璃"（CodexBar 同款），消除两层皮 fringe）

- **用户目检否决 B5 观感**：B5 的窗口级毛玻璃铺满整个窗口矩形（含 30/6/34px 投影边距带）——半透明卡片浮在一块更大的方形毛玻璃板上（用户原话："在现有信息窗背后加一些额外的毛玻璃"），而期望是 CodexBar 式"整个背影都是系统毛玻璃"。授权口径：能做就做，做不成回退 B5 之前观感。
- **根因（12.1.4 源码取证）**：Avalonia.Native 的 AcrylicBlur = `AutoFitContentView._blurBehind`（NSVisualEffectView，BehindWindow + wantsLayer，`SetTransparencyMode(Blur)` 只是解除 hidden）铺满窗口矩形，且窗口垫 windowBackgroundColor；Win32 侧 accent/backdrop 同为窗口矩形。**毛玻璃天然是"窗口级"，不随卡片圆角成形**——唯一解是让窗口贴合卡片、成形交给平台原生。
- **方案（B6）**：acrylic 生效时窗口几何收缩到卡片（Width 498→438、ShadowMargins→0、去 BoxShadow）——窗口即卡片，fringe 几何消除：
  - **macOS**：新 interop `MacPopoverBlurInterop.TryShapeBlurBehind`——contentView 子视图里找非隐藏 NSVisualEffectView（AutoFitContentView 另有常驻隐藏的标题栏材质视图，按 isHidden 区分），`layer setCornerRadius:15`（与卡片 CornerRadius 同值）+ `setMasksToBounds`；窗口垫色置 `clearColor`（否则圆角四角露出 Avalonia 垫的 windowBackgroundColor 实色）+ `setHasShadow:YES`（无边框窗默认无影；系统投影随内容 alpha 走圆角形状，替代被去掉的 BoxShadow）。全指针/double 传参——Intel 同样兼容（无 NSRect stret 问题）；headless 句柄非 NSView，env 标记 + 描述符双守卫。
  - **Windows**：`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND)` 原生圆角（Win11 系统半径 8px）；Win10 无该属性 → 方形 = Win10 原生 flyout 观感。卡片在 Windows acrylic 态切 `acrylic-square` 类（方形、去描边——描边画在系统裁剪之外会角部断线）；macOS 态保留 15px 圆角 + 描边（与 blur 遮罩同值贴合）。
- **回落语义不变**：协商失败 → 投影形态（B5 之前观感：498 窗 + 边距 + BoxShadow + 不透明 15px 卡）；控制器锚定边距改读实例 `EffectiveShadowMargins`（acrylic 态零边距），静态 ShadowMargins 常量仍为回落态单一来源。
- **真机核账**：AXPress 三连过；弹窗 (2051,30,438,437)——宽=卡宽（原 498）、X=状态窗口左缘（图标可视左 2058−7）、Y=栏底；stderr 干净（interop 无异常）。blur 成形/明暗观感 headless 不可验证，移交用户目检（本轮重点：是否已是"整面板一块毛玻璃"、圆角贴合、原生投影可见）。

### 2026-10-18（B6 返工：四角灰底——interop 根本没跑，描述符误判）

- **用户目检**：CodexBar 式全毛玻璃不再追求（试多轮 ROI 判断，用户定调放弃），保留现面板；但四角圆角外有灰色背影，去掉即收工。
- **根因（一次性 stderr 诊断留证）**：`TryGetPlatformHandle()` 实测 descriptor=**NSWindow**（句柄即 NSWindow 指针），B6 首版按"必为 NSView"校验直接放弃——**interop 从未执行**：blur 视图未裁圆角、窗口垫色未置透明（Avalonia 对 Blur 模式垫的 windowBackgroundColor 从四角圆角外露出 = 灰底）、投影未开。
- **修正**：描述符双形态接法（NSWindow 直用 / NSView 走 `[view window]`）；内容容器层（AutoFitContentView）同步裁圆角——内容与 blur 全部收敛进圆角，窗口 alpha 轮廓走圆角，系统投影形状正确；壳窗 Activated 幂等再成形一次（防平台在 Show/激活序列重申透明模式重新垫色）；interop 增加每进程一次性 stderr 诊断（描述符/视图命中/各步骤留证）。
- **真机核账**：诊断日志 "blur 视图 #2 已成形（圆角 15、垫色置透明、开投影）"；弹窗 (2051,30,438,437) 不变，AXPress 三连过；全量 1314 绿。四角是否已净、投影观感移交用户目检（若仍有残余，下一杠杆是 setHasShadow:NO）。

## Verification

- Related files: `src/Tokenmetry.App/Tray/MacTrayInterop.cs`（macOS interop 状态项）、`src/Tokenmetry.App/Tray/StatusPopoverWindow.axaml(.cs)`、`src/Tokenmetry.App/Tray/StatusPopoverController.cs`、`src/Tokenmetry.App/Tray/PopoverPositioner.cs`、`src/Tokenmetry.App/Tray/TrayService.cs`（双轨分派）、`src/Tokenmetry.App/ViewModels/MainWindowViewModel.cs`、`src/Tokenmetry.App/App.axaml.cs`
- How to run/verify: headless = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter "StatusPopoverShell|PopoverPositioner"`（托盘分派 / Toggle / Esc / 菜单项 / 锚定与坐标换算纯函数）；TC-002 全量 `dotnet test Tokenmetry.sln`；真机 = macOS 点图标直弹（B2 交互）/ 右键菜单 / Windows 左键直弹
- Results: **全量 1314 Passed / 0 Failed**；真机冒烟 interop 安装成功（System Events 见唯一 "status menu" 状态项，无回落诊断）；明暗 × 1x/2x 截图与点击手感目检移交用户。
