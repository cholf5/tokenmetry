---
id: B-011
title: Dsh/Copilot 目录递归不跳 reparse point——junction 环崩进程、junction 复制致 DSH 双计
type: bug
priority: P2
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
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

- [x] 两处递归入口跳过 ReparsePoint 条目（对齐 JsonlFileCollector 口径与注释）
- [x] 回归锁：临时目录内建目录 junction（Windows）/ symlink（Unix）用例——不跟随、不重复收集、成环不死循环
- [x] 全量测试绿；Windows 实机复核（junction 语义 Unix 上只是 symlink，Windows 才有环状 junction 的真实风险）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描 F2；严重度定 P2：成环即不可捕获崩溃 + 双计污染成本数字，但需要用户目录里恰有 junction）。

### 2026-10-25 收口

- 修法：两处递归入口在进入条目前查 `(File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0` 即跳过——`DshLogLocator.CollectFromDirectory`（借用既有逐条目 try/catch，stat 间条目消失按跳过处理）、`CopilotLogLocator.CollectFilesWithJsonlExtensionCore`（原无逐条目 catch，补 `IOException|UnauthorizedAccessException` continue，语义与"读不了的条目跳过"对齐）。跳过不分文件/目录 reparse point，与 JsonlFileCollector"符号链接不跟随"完全同口径。根目录自身是 reparse point 时仍进入（对齐共享收集器只查条目）。
- Windows 实机复核（本机真跑）：用例经 `cmd /c mklink /J` 建真 junction——sibling junction（RED 实锤双计：同一 session.jsonl 两路径两份记录）+ sessions 根自指 junction（旧实现无限递归会击穿测试宿主而非红，只修后随同运行）+ Copilot otel 递归 sibling junction（RED 实锤重复发现）。修后 3/3 绿，环不跟随、不双计。
- 测试基建顺手加固：两个 fixture 类 Dispose 的递归删除 catch 补 `UnauthorizedAccessException`（.NET 递归删除会探入 junction 触发拒绝访问，B-011 用例引入 junction 后必现）。
- 明确不做：共享收集器（JsonlFileCollector/SourceFileCollector）本就正确零改动；DSH artifact 根目录 reparse 语义不另立口径。
- 测试：+3（DSH×2 / Copilot×1），Infrastructure.Tests 670 Passed 全绿。

