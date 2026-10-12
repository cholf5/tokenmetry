---
id: D-004
title: ScottPlot 趋势图正式集成（堆叠柱 + 折线 / hover helper）
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

基于 D-002 spike 把趋势图做成正式可复用控件：日趋势（输入/输出堆叠柱 + 总量折线）、成本折线、按小时折线三种形态；落地 I-028 定案的日历窗口语义；封装 hover helper（crosshair + tooltip + 右缘/上缘裁剪翻转）。

## Background

- D-002 结论：单图核心 ~130 行，tooltip helper 100–150 行，成本可控；ScottPlot 与 Avalonia 同名类型（Pixel/PixelSize/Color）需全限定，绘图代码集中在单一 code-behind/partial。
- 原型趋势图为「输入/输出堆叠柱 + 总量折线」叠加；成本图为纯折线；时间分析页有 24 小时折线。
- 交互增强（网页原型没有的）：缩放/平移 AvaPlot 默认开启，保留；tooltip 数值格式与对账口径一致。

## Acceptance Criteria

- [ ] 日趋势控件：堆叠柱 + 总量折线，网格/轴/文字样式贴合 I-027 配色
- [ ] 成本折线与小时折线复用同一 helper（形态参数化）
- [ ] hover：crosshair + tooltip，右缘/上缘裁剪翻转处理（D-002 遗留）
- [ ] 日历窗口语义按 I-028 定案落地，图上可辨识（空日补零等）
- [ ] 明暗主题下轴/文字/系列色跟随；缩放平移无回归

## Subtasks

- [ ] TrendChart 控件封装（三种形态）
- [ ] hover helper（拾取 + 对齐翻转 + 格式化）
- [ ] 主题色接入与明暗切换
- [ ] 用 I-028 真实日序列联调

## Dependencies

- I-028（软依赖：可先以 fixture 数据开发，联调时接真实查询）
- I-027（配色）

## Test Cases

### TC-001: 三形态渲染

Steps:
1. 打开含 TrendChart 的预览页，切换三种形态与明暗主题。

Expected:
- 三种形态渲染正确、样式贴合、明暗切换即时生效、无崩溃。

### TC-002: hover 边界

Steps:
1. 悬停最右/最上数据点。

Expected:
- tooltip 不被裁剪，数值与数据源一致。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Controls/`（新建 TrendChart）
- How to run/verify: TC-001 / TC-002
- Results: （待执行）
