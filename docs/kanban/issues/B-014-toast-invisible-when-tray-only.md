---
id: B-014
title: 托盘常驻态（主窗隐藏）异常通知 toast 不可见——adorner 挂在隐藏主窗上
type: bug
priority: P3
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
blocked: none
---

## Goal

开启"异常通知"的用户在主窗隐藏（托盘常驻）期间也能收到采集异常反馈，或明确接受并在设置项注明。

## Background

- 2026-10-23 扩大扫描（平台守卫维度）发现：`ToastService.cs:56-70` 把 toast 挂在 `TopLevel = MainWindow` 的 adorner 层——主窗隐藏（关窗即托盘是 S-005 默认开）时 toast 无处渲染，用户永远看不到。
- 同类问题先例：首关提示（S-005）为此专门改成一次性模态窗（MainWindow.axaml.cs:125-127 注释："in-app toast 在主窗隐藏后无处渲染"）；异常通知未同等处理，是 S-005 时代同类 wall 的漏网面。

## Acceptance Criteria

- [x] 方案在卡内定案后实施（候选：托盘态经托盘菜单行/弹层角标承载；或主窗隐藏时降级为下次唤醒补提示；或接受现状 + 设置项注明"主窗可见时才显示"）
- [x] 主窗可见场景行为不变（既有 toast 测试不回归）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；UX 归宿需用户拍板，先记录实证）。

### 2026-10-25 关卡（问题前提随 Q-016 消失）

- 定案：**随 Q-016 失效，无需实施**。Q-016（2026-10-25 深夜三，8252c2a）按用户拍板把主窗异常 toast 通道整体删除——`ISystemNotification`/`ToastSystemNotification`、DI 注册、`DashboardViewModel` 通知依赖与「启用异常通知」设置项全部退场；异常唯一出口 = 托盘弹层今日花费 KPI 徽标（Q-011，后随 Q-017 迁头部徽章），**与主窗可见性无关**，托盘常驻期照常工作——本卡描述的"隐藏期间收不到"从根上不存在了。
- 本卡原候选方案（托盘菜单行/弹层角标承载）与 Q-011/Q-017 已落地的徽标出口同构，无增量价值。
- 残留面核查：`IToastService`/`ToastService` 仍在（Q-016 有意保留），消费方为设置页重扫反馈等主窗可见时点的交互反馈，不在本卡"异常通知隐藏期不可见"的问题域内，不动。
- AC 第 2 条随之自然满足：无通道可回归。

