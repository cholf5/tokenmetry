---
id: I-020
title: OpenClaw 适配器
type: feature
priority: P3
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 OpenClaw 数据（OPENCLAW_DIR 或 ~/.openclaw 及 ~/.clawdbot、~/.moltbot、~/.moldbot 的 JSONL；2026.9.x 起的 agents/*/agent/openclaw-agent.sqlite），产出 agent=OpenClaw 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/openclaw/。双格式：JSONL 直读（共享引擎）+ SQLite transcript_events（复用 I-006 骨架）；同数据的 SQLite 行优先于迁移的 JSONL 副本（去重）。本机暂无数据。

## Acceptance Criteria

- [ ] 多 home 发现与双格式解析；SQLite 行与 JSONL 去重优先级对齐蓝本
- [ ] token 映射对齐蓝本；fixtures + 单元测试

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-001（共享引擎）、I-006（SQLite 骨架）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/openclaw/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
