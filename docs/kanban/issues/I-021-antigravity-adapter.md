---
id: I-021
title: Antigravity 适配器
type: feature
priority: P3
size: L
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Antigravity 会话 SQLite（~/.gemini/antigravity*/conversations/ 与 ~/.config/antigravity/conversations/ 下的 .db），产出 agent=Antigravity 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/antigravity/。多默认根、ANTIGRAVITY_DATA_DIR 多根支持；依赖 gen_metadata 表，steps schema 与 trajectory_metadata_blob（protobuf blob）提供时间戳回退——开工前先做 spike 验证 blob 解析的最小实现（或按蓝本裁剪不必要字段），否则拆卡。本机暂无数据。

## Acceptance Criteria

- [ ] 多根发现与 schema 门槛（gen_metadata 必需）
- [ ] usage/时间戳提取（含 protobuf blob 回退方案验证）
- [ ] 解析失败显式上报而非静默空报（对齐蓝本）
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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/antigravity/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
