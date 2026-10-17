---
id: S-005
title: 关窗行为开关（关窗=托盘可关）+ 首次关窗托盘一次性提示
type: feature
priority: P2
size: S
status: done
created: 2026-10-17
updated: 2026-10-17
blocked: none
---

## Goal

A-0「用户关窗 = 隐藏到托盘」从**零开关、零告知**升级为：设置页可切换关窗行为；首次「关窗=托盘」时一次性告知应用去向与退出方式。

## Background

- A-0 pivot 规格（docs/plans/2026-10-11-opinionated-usage-stats-pivot.md §5）定的是托盘常驻 + 后台刷新；`MainWindow.OnClosing` 此前拦截一切用户关窗（点 X / Alt+F4 / 程序内 `Close()`）一律 `Hide()`，真退出只有托盘「退出」。
- 缺口（2026-10-17 用户会话定案）：Windows 用户预期「点 X = 关了」，无任何告知；再唤回失败时找不到出口，只能任务管理器杀进程（数据不丢但体验糟糕）。
- 提示面选型：`ToastSystemNotification` 是 in-app toast（I-027 WindowNotificationManager），**主窗隐藏后无处渲染**，注释明确「系统级通知留给 v0.5」；Avalonia 无跨平台系统通知 API——Windows balloon 需触达 Avalonia 内部 Shell_NotifyIcon 句柄（反射内部实现，跨版本脆弱），macOS UNUserNotification 需 objc interop + 用户授权流程，对一个一次性提示不成比例。选**模态提示窗**（`ShowDialog` 挂主窗）：零 interop、且在「关窗时点」而不是事后告知。
- 开关归类：S-004 拆分立场——「不影响数字的偏好」归设置页签；pivot §5 本就规划设置页承载通知/自启类偏好，关窗行为同类，不破坏 opinionated。
- 开关默认**开**（保持 A-0 现状）：后台刷新与异常检测依赖进程常驻；关闭后点关闭按钮 = 退出进程。

## Acceptance Criteria

- [x] 设置页新增「关闭窗口时最小化到系统托盘」开关（默认开），改完即落盘，下次关窗按新值生效
- [x] 开关关：用户关窗 = `desktop.Shutdown()` 生命周期退出（与托盘「退出」同通道，后台刷新定时器在 `desktop.Exit` 停止），不走 `Environment.Exit`、不依赖 OnLastWindowClose 隐式退出
- [x] 开关开且首次：关窗先弹一次性模态提示窗（文案含托盘常驻说明 / 重新打开与退出路径 / 设置入口），提示窗关闭后主窗才隐藏；状态位落盘后不再打扰
- [x] 生命周期驱动的关闭（ApplicationShutdown / OSShutdown）不弹提示、不拦截（原语义不变）；设置未注入（headless / 设计期构造）= 旧行为隐藏
- [x] i18n：新增文案全走 Translations（zh 源 + en 覆盖）；headless 断言语言无关
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] Core：`AppSettings` 加 `MinimizeToTrayOnClose`（默认 true）+ `CloseToTrayHintShown`（一次性状态位，非偏好、无 UI 出口）
- [x] Translations：`TrayHintTitle/Body/Ok` + `CheckMinimizeToTrayOnClose/Hint` 五键
- [x] SettingsViewModel + SettingsView：开关全自动保存（对齐 AnomalyNotificationsEnabled 模式）
- [x] MainWindow：`ResolveUserCloseAction` 三态决策（internal 纯函数）+ OnClosing 分派 + 代码建模态提示窗（沿用 ShowStartupError 先例）+ `ActiveHintWindow` internal 测试接缝
- [x] App.axaml.cs：主窗注入 settings + IAppSettingsStore
- [x] 测试：App.Tests 开关即改即存；Headless 决策三态 + 首关提示流 + 二次直接隐藏

## Dependencies

- none（寄生在 A-0 既有 OnClosing 拦截与 S-004 设置页结构上）

## Test Cases

### TC-001: 首关提示流（headless）

Steps: Show → `Close()` → 断言 flag 落位、store 落盘 1 次、`ActiveHintWindow` 可见、主窗保持可见 → 提示窗 `Close()` → 主窗隐藏 → 再 `Show()` 可唤回。

Expected: 提示期间主窗可见（OnLastWindowClose 不被误触）；提示窗关闭后主窗才隐藏；窗口未销毁。

### TC-002: 二次关窗直接隐藏（headless）

Steps: `CloseToTrayHintShown=true` → Show → `Close()`。

Expected: 直接隐藏，无提示窗（ActiveHintWindow 为 null），不落盘。

### TC-003: 决策纯函数三态映射（headless，非 UI）

Steps: `ResolveUserCloseAction(null / 默认 / 已提示 / 开关关)`。

Expected: 隐藏 / 首关提示 / 隐藏 / 退出。

### TC-004: 开关即改即存（App.Tests）

Steps: 改 `MinimizeToTrayOnClose` 两次，断言 settings 内存值与 store.SaveCalls。

### 真机人工核验（headless 能力外）

- Windows：点 X → 首次弹提示窗 → 确认后缩托盘、托盘可唤回、托盘菜单「退出」正常；开关关后点 X = 进程退出（含托盘图标消失）。
- macOS：同流程 + 提示窗观感（Window 默认 chrome）。

## Development Log

### 2026-10-17（建卡即实施）

- 用户会话定案「保留默认缩托盘 + 补开关与告知」后直接落地，本卡与实现同日收口。
- 关窗分派抽成 `internal static UserCloseAction ResolveUserCloseAction(AppSettings?)` 纯函数（托盘 / 首关提示 / 退出）：`desktop.Shutdown()` 会终结 headless 宿主（其 ApplicationLifetime 为 null，模式匹配不命中故真 Shutdown 从不在测试里跑），「关窗=退出」只测决策函数，真 Shutdown 路径沿 TrayServiceTests 的「真机人工核验」约定。
- 提示窗选 `ShowDialog` 模态而非系统通知（理由见 Background）；关窗被拦截后主窗仍可见，提示期间应用始终有可见窗口，默认 ShutdownMode（OnLastWindowClose）不会误触。
- **踩坑**：headless 宿主 `Application.Current.ApplicationLifetime` 为 **null**（不止"无桌面"），经 `desktop.Windows` 枚举提示窗恒空集 → 首版测试泵超时。诊断（临时落盘输出）证实 ShowDialog 本身工作正常（主窗未被 finally 隐藏 = 仍在 await）后，加 `MainWindow.ActiveHintWindow` internal 接缝定位提示窗（注释写明为何需要）。
- 并发说明：`CloseToTrayHintShown` 由 MainWindow 落盘、设置页自动保存由 VM 的 `_saveGate` 串行化——两侧写同一 AppSettings 单例且保存为临时文件 + 原子 Move，最坏 last-writer-wins 且内容一致，不加锁。
- 有意不做：OS 级 balloon/toast（需 Win32/objc interop，与一次性提示不成比例，留待真系统通知需求成卡）；关窗时二次确认对话框（开关语义已足够表达）；Mac 状态栏直显成本（单独立 **D-014**）。
- 测试结果：`dotnet build Tokenmetry.sln` 0 error；全量 `dotnet test Tokenmetry.sln` = Passed 1275 / Failed 0（Headless 284→287，App.Tests 127→128）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Core/Models/AppSettings.cs`、`src/Tokenmetry.App/Views/MainWindow.axaml.cs`、`Views/SettingsView.axaml`、`ViewModels/SettingsViewModel.cs`、`App.axaml.cs`、`Localization/Translations.cs`；测试 `tests/Tokenmetry.App.Headless.Tests/Shell/CloseToTrayBehaviorTests.cs`、`tests/Tokenmetry.App.Tests/ViewModels/SettingsViewModelTests.cs`
- How to run/verify: 真机 = 点 X → 首次弹提示、确认后缩托盘、托盘可唤回/退出；设置页关开关 → 点 X 直接退出。自动验证 = TC-001~TC-004
- Results: 完成（2026-10-17）。TC-001/002/003（CloseToTrayBehaviorTests 3 条）+ TC-004（SettingsViewModelTests 1 条）全绿；全量 1275 Passed / 0 Failed。
