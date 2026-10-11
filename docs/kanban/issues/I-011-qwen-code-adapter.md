---
id: I-011
title: Qwen Code 适配器
type: feature
priority: P2
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Qwen Code 会话数据（QWEN_DATA_DIR 或 ~/.qwen/projects/**/*.jsonl 与 chats/*.jsonl），产出 agent=Qwen Code 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/qwen/。日志布局与 Claude Code projects/ 同构，预期可大量复用 I-001 的扫描引擎与解析骨架。本机暂无数据。

## Acceptance Criteria

- [ ] 目录发现（projects/chats 双布局）与增量扫描接入
- [ ] token/模型口径映射对齐蓝本
- [ ] fixtures + 单元测试；接入本地采集管道

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-001（共享引擎）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/qwen/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
