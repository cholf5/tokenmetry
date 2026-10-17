---
id: D-016
title: 状态栏快捷信息窗 · 壳层（macOS 顶部锚定 / Windows 托盘 flyout，内容复用 D-015）
type: feature
priority: P2
size: S
status: done
created: 2026-10-17
updated: 2026-10-17
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

- [x] macOS：托盘左键弹 popover（圆角 + 投影），再点托盘 / 失焦 / Esc 收起；右键菜单行为不变
- [x] Windows：同交互；锚点 = 光标上方且不越出屏幕工作区
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

## Verification

- Related files: `src/Tokenmetry.App/Tray/StatusPopoverWindow.axaml(.cs)`、`src/Tokenmetry.App/Tray/StatusPopoverController.cs`、`src/Tokenmetry.App/Tray/PopoverPositioner.cs`、`src/Tokenmetry.App/Tray/TrayService.cs`、`src/Tokenmetry.App/ViewModels/MainWindowViewModel.cs`、`src/Tokenmetry.App/App.axaml.cs`
- How to run/verify: headless = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter "StatusPopoverShell|PopoverPositioner"`（TC-001 托盘分派 Toggle/复用窗实例/Esc 收起/可见期查询 + 锚定钳制纯函数）；TC-002 全量 `dotnet test Tokenmetry.sln`（TrayServiceTests/CloseToTrayBehaviorTests/AppShellTests 回归全绿）
- Results: **全量 1310 Passed / 0 Failed**（新增 9 例壳层/定位 + 9 例内容层）；真机目检（macOS/Windows 点托盘，明暗 × 1x/2x 截图）移交用户——macOS 本机直接运行即可验证锚点近似是否可接受。
