---
id: D-013
title: 成本分析页（计费口径透明）
type: feature
priority: P2
size: S
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

成本口径透明页：KPI（估算总成本 / 输入成本占比 / 输出成本占比 / 待确认价格模型数）+ 口径说明表（实际计费、估算成本、未知成本三种状态如何展示）。

## Background

- 设计基准：原型 `#page-cost`。
- 「待确认价格模型数」须与定价引擎（I-003/I-024）的 UnpricedModels 实时一致；输入/输出占比不含缓存折扣差异（缓存输入单独计价，口径文案说明）。
- 口径说明表是产品立场的用户可见面：实际计费优先、估算显示价格版本与计算时间、未知不折零。

## Acceptance Criteria

- [ ] KPI 接 I-028/I-029，待确认价格数与定价引擎一致
- [ ] 输入/输出成本占比计算正确（缓存单列说明）
- [ ] 口径表三行内容与产品立场一致（可对账、缺价不折零、估算注明版本）
- [ ] 明暗两套目检通过

## Subtasks

- [ ] CostViewModel + KPI
- [ ] 口径说明表内容定稿（与 docs/reconciliation 口径对齐）

## Dependencies

- D-003, I-028, I-029

## Test Cases

### TC-001: 待确认价格数一致

Steps:
1. fixture 含 3 个缺价模型；对比页面 KPI 与定价引擎返回。

Expected:
- 均为 3；估算总成本不含这三者成本。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/CostAnalysisView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
