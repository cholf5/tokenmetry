---
id: B-011
title: Dsh/Copilot 目录递归不跳 reparse point——junction 环崩进程、junction 复制致 DSH 双计
type: bug
priority: P2
size: S
status: todo
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

`DshLogLocator` 与 `CopilotLogLocator` 的目录递归比照共享收集器跳过 ReparsePoint 条目，消除 Windows junction 成环时的进程崩溃与不成环时的会话双计。

## Background

- 2026-10-23 扩大扫描（Windows 冒烟后续）发现，两处证据：
  - `src/Tokenmetry.Infrastructure/DshSession/DshLogLocator.cs:88-103` — `Directory.GetFileSystemEntries` + `Directory.Exists(entry)` 递归，无 ReparsePoint 检查；
  - `src/Tokenmetry.Infrastructure/CopilotSession/CopilotLogLocator.cs:123-148` — `Directory.EnumerateFileSystemEntries` 递归，同样无检查。
- 对照组：共享引擎 `JsonlFileCollector.cs:35-39` / `SourceFileCollector.cs:60-64` 都显式跳过 `FileAttributes.ReparsePoint`（注释"与蓝本一致：符号链接不跟随"）——这两处是移植偏差。
- 机理：Windows junction 常见且可成环：环 → 无限递归 → StackOverflowException（不可捕获，进程死）。不成环时 junction 指向另一会话树 → 同一物理文件以两个路径被 `artifactByPath`（`DshSessionAdapter.cs:44`，Ordinal key）当两份文件记账；DSH 记录无消息级去重（"重复保证由文件级记账承担"，`DshSessionAdapter.cs:15-16`）→ usage facts 双计。Copilot otel 递归同理。

## Acceptance Criteria

- [ ] 两处递归入口跳过 ReparsePoint 条目（对齐 JsonlFileCollector 口径与注释）
- [ ] 回归锁：临时目录内建目录 junction（Windows）/ symlink（Unix）用例——不跟随、不重复收集、成环不死循环
- [ ] 全量测试绿；Windows 实机复核（junction 语义 Unix 上只是 symlink，Windows 才有环状 junction 的真实风险）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描 F2；严重度定 P2：成环即不可捕获崩溃 + 双计污染成本数字，但需要用户目录里恰有 junction）。
