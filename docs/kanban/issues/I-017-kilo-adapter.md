---
id: I-017
title: Kilo 适配器
type: feature
priority: P3
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Kilo 本地 SQLite（KILO_DATA_DIR 或 ~/.local/share/kilo/kilo.db），产出 agent=Kilo 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/kilo/。SQLite 直读，复用 I-006 骨架。本机暂无数据。

## Acceptance Criteria

- [ ] 目录发现与 schema 检查；按蓝本完成 token 映射
- [ ] fixtures + 单元测试；接入本地采集管道

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-006（SQLite 骨架）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/kilo/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
