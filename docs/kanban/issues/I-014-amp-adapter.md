---
id: I-014
title: Amp 适配器
type: feature
priority: P3
size: S
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Amp 线程数据（AMP_DATA_DIR 或 ~/.local/share/amp/threads/T-*.json），产出 agent=Amp 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/amp/。单层 JSON 文件（非 JSONL），解析后接入统一记录模型。本机暂无数据。

## Acceptance Criteria

- [ ] 线程文件发现与 JSON 解析；token/模型口径映射对齐蓝本
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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/amp/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
