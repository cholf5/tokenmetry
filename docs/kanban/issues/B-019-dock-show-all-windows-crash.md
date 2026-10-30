---
id: B-019
title: macOS Dock 右键「显示所有窗口」崩溃——AppKit 窗口概览层 × 全隐藏窗口，待 .ips 崩溃日志定位
type: bug
priority: P2
size: M
status: done
created: 2026-10-26
updated: 2026-10-30
blocked: none（2026-10-30 关闭，见 Development Log）
---

## Goal

macOS 上 Dock 右键「显示所有窗口」（Show All Windows）不再崩溃。

## Background

- 用户报告：主窗关闭（隐藏到托盘）状态下，Dock 右键 →「显示所有窗口」导致**进程崩溃**。
- 环境：macOS 26 Tahoe（用户确认）。上游最接近的已知家族：Avalonia #19028（macOS 26 频繁崩溃）/ #19788（RunLoop 原生段错误）——均非 Show All Windows 精确匹配，无现成结论。
- 假设（待验证）：Dock「显示所有窗口」触发 AppKit 窗口概览层枚举 `NSApp.windows`——此刻主窗与弹层窗（StatusPopoverWindow）都是 Hide（orderOut）状态，概览层对隐藏窗口的 frame/标题快照路径在 Avalonia.Native 或 AppKit 侧崩溃。
- **证据缺口**：无崩溃堆栈无法定论。需要用户提供 `~/Library/Logs/DiagnosticReports/Tokenmetry-*.ips`（或 Console.app → 崩溃报告）的异常类型 + 崩溃线程堆栈。

## Acceptance Criteria

- [ ] 拿到 .ips 崩溃日志，定位崩溃帧（Avalonia.Native / AppKit / 我方 interop）——**未执行**（用户未取回日志，症状已消失）
- [ ] 按根因定修复策略（我方规避 / 升级 Avalonia / 上游报 issue + workaround）——**未执行**（同上）
- [x] 真机复现 → 修复 → 复验通过——**复验通过由用户完成**（2026-10-30），坏路径不再复现

## Dependencies

- blocked: 用户从 Mac 取回崩溃日志（.ips）

## Development Log

### 2026-10-26

- 建卡。调查会话已确认根因候选与上游问题家族（详见看板 2026-10-26 注记），等证据开工作业。

### 2026-10-30 关闭（用户复验通过）

- 用户在同一天的会话中确认：该崩溃已不复现（macOS 26 真机，主窗隐藏到托盘后 Dock 右键「显示所有窗口」）。按用户指示收口本卡。
- **如实登记证据缺口**：仓库内没有针对本崩溃的独立修复提交，`.ips` 始终未取回，根因**未取证**。同期唯一相关变更是 B-018 的 Dock 唤回通道改造（`783c8f5`：自装 `NSAppleEventManager` 'rapp' handler、补正确拼写的 `applicationShouldHandleReopen:hasAnyVisibleWindows:`、AEM 通道里补 `unhide:`）——它替换的正是 Dock 激活/reopen 这条路径，与崩溃触发场景路径相邻，但**没有任何实测把两者关联起来**，故此处只记「不再复现」这个事实，不写根因结论。
- 重开条件：若同一路径再次崩溃，先取 `~/Library/Logs/DiagnosticReports/Tokenmetry-*.ips`，按原 AC 三步走；上游 Avalonia #19028 / #19788 家族仍待跟踪。
