---
id: B-012
title: "projects" 文件名 Ordinal 精确比较——Windows 大小写不敏感语义下 Claude 源静默空数据
type: bug
priority: P3
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
blocked: none
---

## Goal

配置根目录的 `projects` 文件名识别改 OrdinalIgnoreCase，与 Windows/macOS 文件系统的大小写不敏感语义对齐，不再因键入大小写与磁盘不符而静默丢源。

## Background

- 2026-10-23 扩大扫描（Windows 冒烟后续）发现，两处同型证据：
  - `src/Tokenmetry.Infrastructure/ClaudeCodeJsonl/ClaudeCodeLogLocator.cs:93` — `Path.GetFileName(trimmed) == "projects" && Directory.Exists(trimmed)`；
  - `src/Tokenmetry.Infrastructure/Codebuff/CodebuffLogLocator.cs:127`（`FileNameIsProjects`，Ordinal）。
- 机理：`Directory.Exists` 在 Windows/macOS 上大小写不敏感，而文件名比较是 Ordinal。用户把 `CLAUDE_CONFIG_DIR` 指到 projects 目录本身且键入大小写与磁盘不符（`...\PROJECTS`，盘上 `projects`）→ Exists 通过、文件名比较失败 → `NormalizeConfigCandidate` 返回 null → Claude 源整体静默空数据（数据页无该 agent，无任何报错）。

## Acceptance Criteria

- [x] 两处文件名比较改 `StringComparison.OrdinalIgnoreCase`（仅该比较，不动路径去重等其他 Ordinal 口径）
- [x] 回归锁：候选目录名大小写变体（测试里构造 `PROJECTS` 目录名，断言仍被识别为根）
- [x] 全量测试绿

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描 F3；定 P3：触发条件是"配置指到 projects 目录本身 + 大小写键错"，触发面窄但失败完全静默）。

### 2026-10-25 收口

- 修法：两处比较改 `OrdinalIgnoreCase` 并留注释说明"仅此比较对齐文件系统语义、其他 Ordinal 口径不动"——`ClaudeCodeLogLocator.NormalizeConfigCandidate`（`==` → string.Equals ignorecase）、`CodebuffLogLocator.FileNameIsProjects`（Ordinal → OrdinalIgnoreCase，类型注释登记对蓝本 `root.file_name() == Some("projects")` 的有意偏离）。路径去重 / 排序等其余 Ordinal 口径零改动。
- Windows 实机（本机真跑）TDD RED 双实锤：Claude 候选（盘上 projects、键入 PROJECTS）旧实现返 null；Codebuff 根（同场景）旧实现 records 空——正是"静默空数据"本身。修后 2/2 绿。Linux 文件系统大小写敏感无此场景，用例内 `!Windows && !macOS` 提前返回（不走 xUnit Skip，保持无外部依赖）。
- 明确不做：全仓其余 "projects" 比较（含路径去重 Ordinal）不跟改；不做路径规范化（Codebuff 键入路径原样返回是既有口径，测试一并锁）。
- 测试：+2（ClaudeCodeLogLocatorTests / CodebuffAdapterTests 各一），Infrastructure.Tests 672 Passed 全绿。

