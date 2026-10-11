---
id: I-008
title: GitHub Copilot CLI 适配器
type: feature
priority: P2
size: L
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 GitHub Copilot CLI 本地数据（~/.copilot/session-state/*/events.jsonl 与 otel/*.jsonl），产出 agent=Copilot CLI 的统一 usage 记录。

## Background

本机有真实数据（~/.copilot，约 68KB）。蓝本：../ccusage/rust/adapters/copilot/（该源是 18 源中语义最复杂的：session-state 的 shutdown 记录是按 (session, model) 累计快照，resume 会重复出账，需做相邻快照差分；cache 读写含在 inputTokens 内需拆分；reasoning 已含在 output 内；-1m 等内部后缀需剥离后再定价；OpenTelemetry 行与 shutdown 区间有取优合并逻辑）。建议分两步实现：先 session-state JSONL（覆盖主要数据），otel 合并作为进阶验收。

## Acceptance Criteria

- [ ] session-state shutdown 累计快照 → 区间差分（首条保留、后条减前条），resume 场景总量守恒
- [ ] token 拆分：uncached input = inputTokens − cache 读写；reasoning 不单列
- [ ] 内部模型后缀（-1m / -1m-internal 等）剥离后再匹配定价
- [ ] （进阶）otel 行与 shutdown 区间取优合并；进程无 shutdown 结束的调用不计（对齐蓝本口径）
- [ ] fixtures + 单元测试（含 resume 差分回归）+ 真实数据对账

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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/copilot/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
