---
id: B-014
title: 托盘常驻态（主窗隐藏）异常通知 toast 不可见——adorner 挂在隐藏主窗上
type: bug
priority: P3
size: S
status: todo
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

开启"异常通知"的用户在主窗隐藏（托盘常驻）期间也能收到采集异常反馈，或明确接受并在设置项注明。

## Background

- 2026-10-23 扩大扫描（平台守卫维度）发现：`ToastService.cs:56-70` 把 toast 挂在 `TopLevel = MainWindow` 的 adorner 层——主窗隐藏（关窗即托盘是 S-005 默认开）时 toast 无处渲染，用户永远看不到。
- 同类问题先例：首关提示（S-005）为此专门改成一次性模态窗（MainWindow.axaml.cs:125-127 注释："in-app toast 在主窗隐藏后无处渲染"）；异常通知未同等处理，是 S-005 时代同类 wall 的漏网面。

## Acceptance Criteria

- [ ] 方案在卡内定案后实施（候选：托盘态经托盘菜单行/弹层角标承载；或主窗隐藏时降级为下次唤醒补提示；或接受现状 + 设置项注明"主窗可见时才显示"）
- [ ] 主窗可见场景行为不变（既有 toast 测试不回归）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；UX 归宿需用户拍板，先记录实证）。
