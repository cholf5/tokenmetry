---
id: B-016
title: Windows 托盘弹窗首显锚定取陈旧 Bounds 高度——弹在屏幕中间偏右，二次起才正常
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

Windows 托盘左键首次弹出的快捷信息窗锚定在光标上方（贴托盘 flyout 位），不再落屏幕中间偏右；macOS / Linux 语义不变。

## Background

- 用户报告：Windows 上首次点托盘图标，弹窗出现在屏幕中间偏右，多点几次位置才对；macOS 无此问题。
- 机理：`StatusPopoverController.Show()` 是 `Show() → PositionWindow()`，Windows 分支锚定尺寸取 `window.Bounds.Size`（DIP × Scaling）。Avalonia 12 的窗口 HWND 以 `CW_USEDEFAULT` 创建（本机解析出 ≈720 DIP 高的默认矩形）；`SizeToContent="Height"` 的实测高度在 `Show()` 返回时已进 `ClientSize`（438×376），但 `Bounds` 高度要等首个布局 tick 才回写（此刻仍 438×720）。
- 真机留证（1920×1080 @100%，临时打点后已还原）：修复前首显 `y = cursor 1020 − 720 − 10 = 290`，窗口实际 376 高 → 落 `y 290..664`，即"屏幕中间偏右"；第二次起 Bounds 正确，位置正常。macOS 不受影响的原因：`AnchorBelow` 以图标 frame 为顶锚，窗口高度只参与越界钳制、不移动可见卡片——与"Mac 没这问题"吻合。

## Acceptance Criteria

- [x] `ScreenSize` 改用 `window.ClientSize`（`Show()` 返回时即正确；无边框窗口 frame=0，非首显场景与 Bounds 等价，三平台锚定语义不变）
- [x] 真机首显留证：`target=(1172,634)`，`GetWindowRect=(1172,634)-(1610,1008)`——底缘贴光标上方 12px（gap 10 + 布局回写 2px 差），右缘贴托盘
- [x] `PopoverPositioner` 纯函数零改动；headless 锚定/装配用例全绿

## Dependencies

- none

## Development Log

### 2026-10-25

- 建卡即收口。取证路径：Avalonia 12.1.4 源码（`Window.ShowCore` 时序：`ExecuteInitialLayoutPass` → SizeToContent 补 `ClientSize` → `SetWindowStartupLocation` → `PlatformImpl.Show`，`Bounds` 回写不在同步路径上）+ `%TEMP%` 最小复现程序（复刻壳窗口属性与 Show 时序，同样观测到首显 `Bounds=498×720` / `ClientSize=498×660` 分叉）+ 真实应用带打点实测（托盘点击经 UIA/Win32 toolbar 自动化触发）。
- 修复一行：`ScreenSize` 的 `window.Bounds.Size` → `window.ClientSize`，注释留痕根因。诊断打点已全部还原。
- 测试：本卡相关面全绿——App.Tests 265、App.Headless 386、Infrastructure 672、Reconciliation 49（19 跳过按本机 fixture）。**并发注记**：与 S-007（刷新默认 1→5，8ab392c）/ I-042 / I-043 会话同工作区并行——其间两次 App.Tests 全量单例失败（SingleInstanceGuard / JsonAppSettingsStore）经查属并行会话互踩而非 flaky（后者已被 8ab392c 随卡同步收口）；Core.Tests 现 2 失败（AppSettingsTests / DashboardSummaryTests 仍断言 RefreshMinutes=1）同属该会话默认值改动的测试同步欠账，与本卡无交集（本卡仅动 `ScreenSize` 一行），经选择性暂存分账。
- 明确不做：不改 Show/定位时序（不动"先 Show 后定位"避免默认位闪烁）；不为此引入 headless 首显 Bounds 断言（headless 平台无 CW_USEDEFAULT 语义，测不了该分叉，真机留证为准）。
