---
id: D-006
title: 总览页（上半）：KPI 行 + 使用趋势 + Agent 占比环形
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

重建 Dashboard 为原型总览页的上半区：4 张 KPI 卡（总 Token / 估算成本 / 会话数 / 缓存读取占比，含环比箭头）、Token 使用趋势（D-004）、Agent 使用占比环形（D-005），全部接真实查询服务与全局筛选。

## Background

- 设计基准：原型 `#page-overview` 的 `.kpis` 与 `.grid2` 第一行。
- 这是 v0.4「Dashboard 重构为固定视图」的落地卡（取代旧 DashboardView 的 Fluent 版本）。
- 环比箭头数据来自 I-028 的上一周期对比；未知成本显示「未知」而非 $0。

## Acceptance Criteria

- [ ] KPI 卡接 I-028：数值、环比方向（↑绿/↓红）、副文案齐全
- [ ] 趋势图随全局时间范围 / Agent 筛选刷新
- [ ] Agent 环形随筛选刷新，占比数值与查询结果一致
- [ ] 未知成本 KPI 显示「未知」不折零
- [ ] 明暗两套目检通过

## Subtasks

- [ ] OverviewViewModel（接全局筛选状态）
- [ ] KPI 卡布局 ×4 + 绑定
- [ ] 趋势图与环形图接入
- [ ] 真实数据联调与目检

## Dependencies

- D-003, I-028, D-004, D-005

## Test Cases

### TC-001: KPI 数值对账

Steps:
1. 选定时间范围，记录 KPI 四值。
2. 与 `sqlite3` 直查同口径对照（或复用对账测试口径）。

Expected:
- tokens 一致；成本在容差内；缺价模型期间成本显示「未知」。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/OverviewView.axaml`（替换 DashboardView）、`ViewModels/`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
