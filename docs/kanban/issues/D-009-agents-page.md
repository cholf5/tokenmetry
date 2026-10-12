---
id: D-009
title: Agent 统计页
type: feature
priority: P2
size: S
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

Agent 维度统计页：顶部 Agent KPI 卡（前 4 个 Agent）+ Agent 排行表（会话数 / 输入 / 输出 / 总 Token / 估算成本 / 占比），行点击跳会话明细页并带上该 Agent 筛选。

## Background

- 设计基准：原型 `#page-agents`。
- 数据全部来自 I-029 的 Agent 维度聚合；无新增口径。

## Acceptance Criteria

- [ ] KPI 卡显示前 4 个 Agent 的 token / 会话数 / 成本（未知成本显示「未知」）
- [ ] 排行表六列数据接 I-029，占比正确
- [ ] 行点击跳转会话页且 Agent 筛选生效
- [ ] 随全局筛选联动，明暗两套目检

## Subtasks

- [ ] AgentsViewModel + KPI 卡
- [ ] 排行表 + 跳转联动

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 跳转联动

Steps:
1. 点击排行表某 Agent 行。

Expected:
- 会话页打开且列表只剩该 Agent 会话。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/AgentsView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
