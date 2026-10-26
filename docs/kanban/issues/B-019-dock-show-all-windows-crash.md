---
id: B-019
title: macOS Dock 右键「显示所有窗口」崩溃——AppKit 窗口概览层 × 全隐藏窗口，待 .ips 崩溃日志定位
type: bug
priority: P2
size: M
status: todo
created: 2026-10-26
updated: 2026-10-26
blocked: 用户提供崩溃日志（~/Library/Logs/DiagnosticReports/Tokenmetry-*.ips）
---

## Goal

macOS 上 Dock 右键「显示所有窗口」（Show All Windows）不再崩溃。

## Background

- 用户报告：主窗关闭（隐藏到托盘）状态下，Dock 右键 →「显示所有窗口」导致**进程崩溃**。
- 环境：macOS 26 Tahoe（用户确认）。上游最接近的已知家族：Avalonia #19028（macOS 26 频繁崩溃）/ #19788（RunLoop 原生段错误）——均非 Show All Windows 精确匹配，无现成结论。
- 假设（待验证）：Dock「显示所有窗口」触发 AppKit 窗口概览层枚举 `NSApp.windows`——此刻主窗与弹层窗（StatusPopoverWindow）都是 Hide（orderOut）状态，概览层对隐藏窗口的 frame/标题快照路径在 Avalonia.Native 或 AppKit 侧崩溃。
- **证据缺口**：无崩溃堆栈无法定论。需要用户提供 `~/Library/Logs/DiagnosticReports/Tokenmetry-*.ips`（或 Console.app → 崩溃报告）的异常类型 + 崩溃线程堆栈。

## Acceptance Criteria

- [ ] 拿到 .ips 崩溃日志，定位崩溃帧（Avalonia.Native / AppKit / 我方 interop）
- [ ] 按根因定修复策略（我方规避 / 升级 Avalonia / 上游报 issue + workaround）
- [ ] 真机复现 → 修复 → 复验通过

## Dependencies

- blocked: 用户从 Mac 取回崩溃日志（.ips）

## Development Log

### 2026-10-26

- 建卡。调查会话已确认根因候选与上游问题家族（详见看板 2026-10-26 注记），等证据开工作业。
