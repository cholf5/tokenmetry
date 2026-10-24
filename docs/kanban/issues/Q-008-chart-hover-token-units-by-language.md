---
id: Q-008
title: 图表悬停浮窗 token 单位随 UI 语言——中文 万/亿（ScottPlot 时代 ASCII 约束残留）
type: bug
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

TrendChart 悬停浮窗的 token 数在中文 UI 下仍显示英文短单位（如 `3.25M`），改为随 UI 语言的中文万进制（`325万`）——对齐全应用既有惯例（KPI/列表/环心/环形图/热力图 tooltip 均走 `FormatCompactTokens`），此处是漏网之鱼。

## Background

- 用户反馈（2026-10-24 会话）："Charts 在 Hover 时显示的小浮窗，里面显示的 token 单位，在中文状态下仍然以英文的单位显示……这在本项目里是惯例，这里是漏网之鱼。"
- 取证（git log）：`FormatCompactTooltipValue`（恒英文 K/M/B/T/Q）的 ASCII 约束来自 ScottPlot 自绘 tooltip 时代（d268368，"ScottPlot 内渲染无 CJK 字形"）；`cd1e2bd` 已把浮窗换成 Avalonia 浮层（XAML `HoverTip` Border + `chart-hovertip` 类），TextBlock 渲染 CJK 无障碍，但消费点与注释没跟着更新——约束失效后成了纯残留。
- 影响点两处：`DailyTooltipText`（in/out/tot 三行）与小时形态 label；环形图/热力图 tooltip 早已走 `FormatCompactTokens`，无需改。

## Acceptance Criteria

- [x] 日形态悬停浮窗：中文 UI 下 in/out/tot 显示 万/亿（402,000 → `40.2万`）；英文 UI 保持 K/M
- [x] 小时形态悬停浮窗同口径
- [x] 语言切换实时换单位——TrendChart 补 `Translations.CurrentLanguage` 订阅重译（B-007 同 DonutChart/Heatmap 纪律），不换数据即随切换重建
- [x] `FormatCompactTooltipValue` 零消费方后删除；过时注释（"ScottPlot 内渲染/恒 ASCII"）修正；`FormatCompactAxisValue` 轴刻度恒 ASCII 不变（ScottPlot 自绘、无 CJK 字形）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿（1447 Passed / 0 Failed / 22 Skipped）

## Subtasks

- [x] `TrendChart.axaml.cs`：两处消费点改 `FormatCompactTokens`；删除 `FormatCompactTooltipValue`；补 `OnAttachedToVisualTree/OnDetachedFromVisualTree` 对称的重译订阅（取代构造器 `AttachedToVisualTree` 事件挂法，重建走同一 `RebuildPlot` 管线）；doc 注释修正（含误挂的 `FormatCostUsd` doc 复位）
- [x] `ChartTooltips.cs` 类 doc：交叉引用更新（TrendChart 浮窗同为 Avalonia 浮层；只剩轴刻度不走本类）
- [x] `TrendChartTests`：既有 token 单位断言显式锁英文口径（`WithLanguage`）；新增中文万口径 + 语言切换实时性用例（悬停 → 切语言 → 再悬停，单位即换）；删除废弃 theory
- [x] 浮窗内 "in/out/tot/tok/sessions" 等记号保持 ASCII（与环形图/热力图 tooltip 的 " tok" 后缀同惯例）——文案本地化属另一范畴，本卡不做

## Dependencies

- d268368（B-006 紧凑单位引入，ASCII 约束的来源）、cd1e2bd（浮窗迁 Avalonia 浮层，约束失效）、4d61f93（`FormatCompactTokens` 万/亿口径）
