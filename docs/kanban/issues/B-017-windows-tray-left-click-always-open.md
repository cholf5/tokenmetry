---
id: B-017
title: Windows 托盘左键永远 Open 不 Switch——失焦收起先于 Clicked 到达，Toggle 看到的已是"关着"又重新弹出
type: bug
priority: P2
size: S
status: done
created: 2026-10-26
updated: 2026-10-26
blocked: none
---

## Goal

Windows 上托盘左键点击快捷信息窗恢复 Switch 语义：开着 → 收起，关着 → 弹出；macOS（interop 左键直回调）行为不变。

## Background

- 用户报告：Windows 上每次左键点托盘图标都是 Open，弹窗开着时点击也不收起；macOS 是正常的 Switch。
- 机理（时序竞争）：`StatusPopoverWindow` 在窗口构造里自接线 `Deactivated → Hide()`（失焦收起）。Win32 上点托盘图标，系统在**按下瞬间**把焦点交给任务栏——弹窗先收到 `Deactivated` 被失焦收起；`TrayIcon.Clicked` 要等 **WM_LBUTTONUP（抬起）** 才 raise，此时 `Toggle()` 看到的已是"关着"，照常走 Show 分支重新弹出。两条通道各干各的，净效果 = 左键永远只会 Open（弹窗开着时表现为收起后立即再弹的闪烁）。
- macOS 免疫：interop 路径左键由 NSStatusItem 直接回调 `Toggle`，无"先失焦后回调"的时序分叉，Switch 正常——与用户观察吻合。

## Acceptance Criteria

- [x] 失焦收起接线从窗口构造移入 `StatusPopoverController`（同时补上 VM.OnHidden——窗口自接线走的是裸 `Window.Hide()`，漏了内容层"可见期才发查询"的 OnHidden 语义，Esc / 再点收起通道均有）
- [x] `ToggleFromTrayClick`（托盘左键专用入口）前置失焦守卫：刚失焦收起（400ms 窗口期）→ 维持隐藏不重新弹出；守卫消费制（吞一次即失效），快速连点可再开
- [x] 守卫只挂托盘左键路径——托盘右键菜单「快捷信息窗」项等显式 `Toggle()` 不带守卫（打开菜单本身会抢焦点触发失焦收起，菜单项随后才到，属明确意图应照常执行）
- [x] headless 回归锁 ×2：失焦后 Toggle 维持关闭、消费后可再开；自身收起不记账（收起后立即再点可开）
- [x] macOS interop / Avalonia 回落路径语义不变（守卫在该路径下惰性：无先失焦时序则不触发）

## Dependencies

- none（B-016 同文件相邻面，已收口）

## Development Log

### 2026-10-26

- 建卡即收口。修复三处：`StatusPopoverController` 接管失焦接线（`Show()` 建窗时订阅 `Deactivated` → `OnShellWindowDeactivated`：可见态失焦才算外部抢焦点，记账 + 走控制器 Hide；自身 Hide 引发的 Deactivated 到达时窗口已不可见，不记账——否则每次 Toggle 收起都给下一次点击上锁）；`Toggle()` 拆双入口（`ToggleFromTrayClick` 带守卫 / `Toggle` 显式入口不带）；`TrayService.TogglePopoverOrShowWindow`（interop 左键回调 + Avalonia Clicked 共用）改走 `ToggleFromTrayClick`。`StatusPopoverWindow` 构造里的自接线移除。
- 守卫取值 400ms：覆盖按下到抬起的按压时长（p99 ≈ 250ms）；代价是点走焦点后 400ms 内点托盘会被吞一次，消费制下再点即开。macOS interop 路径下守卫惰性（无先失焦时序）；若未来 macOS 出现同款时序，守卫恰好给出正确的 Switch 结果。
- 测试 +2（`Toggle_within_focus_loss_guard_stays_closed_then_reopens` / `Self_initiated_hide_does_not_arm_focus_loss_guard`，经 internal 处理器直接模拟 Win32"先失焦后 Clicked"时序——headless 不产生真实 Deactivated）。全量：Core 189 / Reconciliation 49（19 跳过按本机 fixture）/ Infra 681 / Headless 396 全绿；App.Tests 265 + 1 失败——`SingleInstanceGuardTests.Second_instance_cannot_acquire_while_first_holds`，**git stash 干净工作区复现同款失败**（且单类过滤跑必过），与本卡零交集，同 2026-10-25 深夜八注记的环境性单例失败家族，留待独立排查。
- 明确不做：真机托盘连点留证移交用户（headless 无法模拟 WM_LBUTTONUP 前的焦点交接时序，守卫逻辑已由回归锁钉死）；不动 B-016 的锚定逻辑；不引入全局鼠标钩子级"点外收起"重构。
