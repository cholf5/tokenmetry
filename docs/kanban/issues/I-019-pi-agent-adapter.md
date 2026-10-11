---
id: I-019
title: pi-agent 适配器
type: feature
priority: P3
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 pi-agent 会话数据（PI_AGENT_DIR 或 ~/.pi/agent/sessions，及 ccusage.json 的 pi.stores[] 附加存储），产出 agent=pi 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/pi/。注意附加 stores 的发现机制（ccusage.json 配置）。本机暂无数据。

## Acceptance Criteria

- [ ] 目录发现（含 pi.stores[] 附加根）与解析
- [ ] token 映射对齐蓝本；fixtures + 单元测试

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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/pi/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
