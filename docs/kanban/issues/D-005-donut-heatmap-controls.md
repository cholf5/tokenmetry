---
id: D-005
title: 自绘环形图与热力图控件
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

实现两个可复用自绘控件：环形图（多段弧 + 中心总量 + 图例列表，对应原型 donut）与活跃日历热力图（日强度着色格子 + 悬停提示），供总览页与时间分析页使用。

## Background

- 原型 donut 用 SVG stroke-dasharray 圆弧实现（约 6 行核心逻辑），热力图是 35 个着色格子；逻辑简单，自绘可控性最高，不值得为它们上图表库。
- Avalonia 无内置 Arc shape，用 PathGeometry 圆弧段或 StreamGeometry；环形中心文字用 Grid 叠层。
- 颜色映射 I-027 的系列色资源；明暗两套（中心文字、格子边框、空数据色）。

## Acceptance Criteria

- [ ] DonutChart 控件：数据源驱动多段弧、中心标题/副标题、图例列表（名称 + 百分比），段序映射系列色
- [ ] Heatmap 控件：按日强度着色、hover 显示当日数值、空数据样式、列数/行数可配
- [ ] 明暗两套目检通过
- [ ] 数据量边界（>2 段、单段 100%、全空）渲染正常

## Subtasks

- [ ] DonutChart 控件（弧段几何 + 图例）
- [ ] Heatmap 控件（格子 + 悬停）
- [ ] 边界情况与明暗目检

## Dependencies

- I-027（系列色与配色）

## Test Cases

### TC-001: 环形图边界

Steps:
1. 预览页分别传入 1 段 / 2 段 / 7 段 / 空数据。

Expected:
- 单段满圆、多段比例正确、空数据不崩且显示占位。

### TC-002: 热力图悬停

Steps:
1. 悬停不同强度的格子。

Expected:
- 提示显示当日数值，颜色深浅与强度单调一致。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Controls/`（DonutChart / Heatmap）
- How to run/verify: TC-001 / TC-002
- Results: （待执行）
