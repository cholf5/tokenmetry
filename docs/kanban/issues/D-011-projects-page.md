---
id: D-011
title: 项目统计页
type: feature
priority: P2
size: S
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

项目维度统计页：项目/目录、会话数、总 Token、估算成本、占比、平均每会话六列表；行点击跳会话明细页并带项目筛选。

## Background

- 设计基准：原型 `#page-projects`；「路径归一化后的项目名」来自 I-029 的项目维度聚合。
- 归一化歧义守卫对齐 I-025 思路：歧义时显示原始路径，不猜测合并。

## Acceptance Criteria

- [ ] 六列表接 I-029 项目维度，占比与均值计算正确
- [ ] 行点击跳会话页且项目筛选生效
- [ ] 归一化歧义项目显示原始路径（或可展开原始路径）
- [ ] 未知成本显示「未知」；明暗两套目检

## Subtasks

- [ ] ProjectsViewModel + 排行表
- [ ] 跳转联动与歧义路径展示

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 跳转联动

Steps:
1. 点击某项目行。

Expected:
- 会话页列表只剩该项目会话。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/ProjectsView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
