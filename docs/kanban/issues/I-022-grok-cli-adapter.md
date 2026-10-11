---
id: I-022
title: Grok Build CLI 适配器
type: feature
priority: P3
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Grok Build CLI 会话数据（GROK_HOME 或 ~/.grok/sessions/<url-encoded-cwd>/<uuid>/updates.jsonl），产出 agent=Grok 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/grok/。仅计 turn_completed 完成行；OpenAI 风格 inputTokens 含 cache 需扣除；reasoningTokens 已含在 output 内（丢弃防重复）；costUsdTicks 为发票成本（1 tick = 1e-10 USD），display 口径直接采用、calculate 回退 token×价——本项目统一由 PricingEngine 计价，CostUsd 采用日志 ticks（LogCost 短路），与 CLI display/auto 口径一致。logs/unified.jsonl 明确不用（无逐请求模型 id）。本机暂无数据。

## Acceptance Criteria

- [ ] 目录发现（url-encoded cwd 层级）与 turn_completed 过滤
- [ ] token 拆分（cache 扣除、reasoning 丢弃）与 costUsdTicks → CostUsd 换算
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

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/grok/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
