---
id: D-007
title: 总览页（下半）：模型分布 + Token 构成 + 项目排行 + 成本趋势
type: feature
priority: P2
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

总览页下半区四块：模型使用分布表（TopN + mini 进度条 + 查看详情链接）、Token 类型构成环形（输入/输出/缓存）、项目/目录排行表、成本趋势折线（USD）。

## Background

- 设计基准：原型 `#page-overview` 的两个 `.gridEqual` 下行。
- 「查看详情 ↗」跳转模型统计页 / 项目统计页；mini 进度条复用 I-027。
- 成本趋势接 I-028 日序列的 cost 列；未知成本不折零（序列中 null 的日聚合处理在 I-028 契约内）。

## Acceptance Criteria

- [ ] 模型 TopN 表：token 数、占比、mini bar 比例正确，「查看详情」跳模型页
- [ ] Token 类型构成环形三段正确（输入/输出/缓存相关）
- [ ] 项目排行表：排名、归一化项目名、token、占比正确，跳项目页
- [ ] 成本趋势折线随全局筛选刷新
- [ ] 全部随全局筛选联动，明暗两套目检

## Subtasks

- [ ] 模型分布表 + mini bar + 跳转
- [ ] 类型构成环形接入
- [ ] 项目排行表
- [ ] 成本趋势接入

## Dependencies

- D-006（同页，先落上半区）
- I-028, I-029, I-027

## Test Cases

### TC-001: 占比与跳转

Steps:
1. 目检各表占比与 mini bar 宽度比例；点击「查看详情」。

Expected:
- 占比合计合理（TopN 不足 100% 时含「其他」或按 TopN 占比展示，实现时定案记录）；跳转到达对应页。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/OverviewView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
