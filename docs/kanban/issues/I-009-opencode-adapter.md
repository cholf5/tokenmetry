---
id: I-009
title: OpenCode 适配器
type: feature
priority: P2
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 OpenCode 本地 SQLite 数据（OPENCODE_DATA_DIR 或 ~/.local/share/opencode），产出 agent=OpenCode 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/opencode/。SQLite 直读模式与 I-006 ZCode 适配器同构（只读打开、schema 检查、动态列），可复用其定位器/检查器骨架。本机暂无该源数据，fixture 依蓝本合成，对账仅能做 fixture 级（CLI 有数据时才可真实对账）。

## Acceptance Criteria

- [ ] 数据目录发现（OPENCODE_DATA_DIR → XDG 口径回退）与 SQLite 只读接入
- [ ] 按蓝本完成行过滤、token 口径映射与模型映射
- [ ] 复用/泛化 I-006 的 SQLite 扫描骨架（避免复制分叉）
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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/opencode/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
