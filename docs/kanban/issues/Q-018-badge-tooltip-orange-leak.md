---
id: Q-018
title: 徽章 tooltip 橙字漏染修复 + chart-tip 令牌底——Semi 默认 tooltip 配色错配绕行
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

头部异常徽章（Q-017 迁入）的 hover tooltip：文字回归**正文文字色（AppTextBrush）+ chart-tip 令牌底**（与图表悬浮面同源观感），不再橙字 + Semi 默认反差底。徽章本体样式不变。

## Background

- 用户目检（2026-10-25，明暗两题截图）：徽章 hover 提示两主题均**橙字**——暗色下浅底橙字不可读，明色下深底橙字可读但与主题色不符，问「是故意的吗」。答：不是，两处叠加的意外。
- 根因 1（橙字）：ToolTip 在逻辑树上是挂载目标的子节点，徽章嵌套样式 `Border.popover-cost-warn-badge TextBlock`（后代选择器，Q-011 写下）把 tooltip 内容 TextBlock 一并染橙并压到 10px——真机上两主题橙字实锤。
- 根因 2（底色）：tooltip 底色走 Semi 12.1 默认 ToolTip 配色，B-006 已证错配且不随应用令牌（Light 浅底白字 / Dark 深底黑字），图表侧当年即绕行自建 `ToolTip.chart-tip`（chip 底 + 卡片描边 + AppTextBrush，圆角 6 / 内距 10,7 / 字号 12）。

## Acceptance Criteria

- [x] 徽章 tooltip = 显式 ToolTip 控件 + `chart-tip` 类（逃离 Semi 默认配色；与 Donut/Heatmap/ScottPlot 悬浮面同令牌）
- [x] 徽章文本改自类选择器 `popover-cost-warn-text`（嵌套后代选择器撤除，tooltip 内容不再被染橙/压字号）；徽章本体橙色 10px SemiBold 不变
- [x] 回归锁：badge 类 Border 下的无类深层 TextBlock 不被染橙（有人改回后代选择器时锁红）
- [x] headless 弹层 21/21；全量 `dotnet test Tokenmetry.sln` 1537 Passed / 0 Failed（22 跳过）

## Subtasks

- [x] RED：`Badge_tooltip_uses_chart_tip_tokens_and_badge_style_does_not_leak_into_tooltips` 三段锁（Tip 是显式 ToolTip+类 / 徽章文本自类染橙 / 深层无类不染）——首跑失败实锤 Tip 还是字符串
- [x] GREEN：`StatusPopoverView.axaml` 徽章挂 `<ToolTip.Tip><ToolTip Classes="chart-tip" Content="{Binding CostWarnDetail}"/></ToolTip.Tip>`；文本样式 `Border...badge TextBlock` → `TextBlock.popover-cost-warn-text`；SharedStyles chart-tip 注释扩记第二消费方
- [x] 全量 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- 调查弯路留痕（诚实记录）：中间一度误判「`>` 直接子级选择器在 UserControl.Styles 不命中」——实际上 UnsetValue 来自**单参 `FindResource` 不解析 ThemeDictionaries**（橙令牌随明暗翻转、在主题字典里），样式本身（含 `>` 变体）一直正常。正确取法是三参 `TryFindResource(key, themeVariant, out …)`；这是 Q-014「同名画刷经样式 DynamicResource 与 FindResource 两路径非同实例」教训的同族坑，本卡以 DIAG 探针实测后留痕。最终文本样式选自类选择器（比 `>` 更直白、零组合子语义负担），与误诊结论无关。
- 顺手收益：tooltip 文字从漏染的 10px 橙恢复为 chart-tip 规格 12px 正文色；显式 ToolTip 的 `Content` 走编译绑定，语言切换重译限制与 Q-011 时代相同（raise 时固化串）。
- 明确不做：其余视图十余处字符串 tooltip 仍走 Semi 默认底（无嵌套样式染色组合，暂无不可读实报）——app 级 tooltip 底色统一（全部挂 chart-tip 或全局 ToolTip ControlTheme 覆写）另立卡决策；仓库既有 `Border.icon-chip > TextBlock` 等先例不在本卡审查范围。
