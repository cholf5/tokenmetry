---
id: I-003
title: 自有定价引擎（内置快照 + models.dev + 用户覆盖）
type: feature
priority: P1
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

实现自有定价引擎：构建期内置价格快照 + models.dev 联网刷新（TTL 24h、失败静默回退）+ 用户覆盖规则，三级回退；为 JSONL usage 记录计算成本。

## Background

- 自主采集后 cost 由我们自己计算，不再依赖 CLI。
- 定价刷新永远在后台执行，绝不阻塞 UI 或刷新主路径——ccusage 慢的根因就是把联网刷新放在关键路径（`docs/chore/why-ccusage-so-slow.md`）。
- 蓝本：`../ccusage-tauri/src-tauri/src/pricing_refresh.rs`（三级回退、TTL）与 `claude.rs` 中 cache write 1h 按 input × 2 计价的规则。

## Acceptance Criteria

- [ ] 断网环境下用内置快照完成定价，UI 与刷新流程不被网络阻塞
- [ ] models.dev 刷新后台执行，TTL 24h，失败时沿用上一次成功数据
- [ ] 用户覆盖规则（exact 模型名）生效并持久化到设置
- [ ] 价格未命中的模型有记录（供对账测试与诊断使用）

## Subtasks

- [ ] 内置价格快照嵌入与加载
- [ ] models.dev 拉取（TTL 24h、后台、失败回退）
- [ ] 用户覆盖规则（exact 匹配）与持久化
- [ ] 计价 API（含 cache write 5m / 1h 倍率规则）
- [ ] 未命中模型的记录与暴露
- [ ] 单元测试（快照定价、覆盖生效、未命中记录）

## Dependencies

- none（与 I-001 可并行）

## Test Cases

### TC-001: 离线定价

Steps:
1. 断网状态下对样例记录计价。

Expected:
- 内置快照命中并返回成本；无网络请求、无阻塞。

### TC-002: 用户覆盖优先

Steps:
1. 为某模型设置覆盖价格后重新计价。

Expected:
- 使用覆盖价格，优先级高于快照与远程数据。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Core/Services/`（定价引擎）、`src/CCUsage.Infrastructure/`（models.dev 客户端）
- How to run/verify: `dotnet test tests/CCUsage.Core.Tests/CCUsage.Core.Tests.csproj`
- Results: 待执行
