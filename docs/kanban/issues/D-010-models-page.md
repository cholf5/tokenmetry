---
id: D-010
title: 模型统计页（价格缺失显示未知口径样板）
type: feature
priority: P2
size: S
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

模型维度明细页：模型、关联 Agent、输入 / 输出 / 缓存读取 / 总 Token、成本；价格缺失显示「未知」而非 0，是全应用「未知成本口径」的样板页。

## Background

- 设计基准：原型 `#page-models`；页面 meta 文案「价格缺失时成本应标记为未知，而非 0」直接对应产品立场（可对账、缺价不折零，I-024 收口确立）。
- 关联 Agent 列：同一模型可能被多个 Agent 使用——显示主用 Agent 或「多 Agent」聚合展示，实现时定案记录。

## Acceptance Criteria

- [ ] 全模型列表接 I-029 模型维度聚合
- [ ] 缺价模型成本列显示「未知」且有视觉区分（chip 或弱化色），不混入合计
- [ ] 合计行只计已估价部分，或已估/未估拆分显示，口径在页面可见
- [ ] 随全局筛选联动，明暗两套目检

## Subtasks

- [ ] ModelsViewModel + 明细表
- [ ] 未知成本视觉与合计口径
- [ ] 关联 Agent 列展示定案

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 未知口径

Steps:
1. fixture 含缺价模型；查看列表与合计。

Expected:
- 缺价行显示「未知」；合计不含该行成本但含其 token。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/ModelsView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
