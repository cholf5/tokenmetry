---
id: B-012
title: "projects" 文件名 Ordinal 精确比较——Windows 大小写不敏感语义下 Claude 源静默空数据
type: bug
priority: P3
size: S
status: todo
created: 2026-10-23
updated: 2026-10-23
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

- [ ] 两处文件名比较改 `StringComparison.OrdinalIgnoreCase`（仅该比较，不动路径去重等其他 Ordinal 口径）
- [ ] 回归锁：候选目录名大小写变体（测试里构造 `PROJECTS` 目录名，断言仍被识别为根）
- [ ] 全量测试绿

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描 F3；定 P3：触发条件是"配置指到 projects 目录本身 + 大小写键错"，触发面窄但失败完全静默）。
