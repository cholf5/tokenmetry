---
id: I-018
title: Codebuff 适配器
type: feature
priority: P3
size: S
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Codebuff 会话数据（CODEBUFF_DATA_DIR 或 ~/.config/<channel>/projects/**/chat-messages.json，channel 含 manicode/-dev/-staging），产出 agent=Codebuff 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/codebuff/。多 channel 目录枚举 + 单文件 JSON。本机暂无数据。

## Acceptance Criteria

- [ ] 多 channel 目录发现与解析；token 映射对齐蓝本
- [ ] fixtures + 单元测试；接入本地采集管道

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/codebuff/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
