---
id: B-006
title: 图表 hover tooltip 三缺陷（无样式透明气泡 / 配色不随主题 / token 数放不下）
type: bug
priority: P1
size: S
status: done
created: 2026-10-16
updated: 2026-10-16
blocked: none
---

## Goal

修复用户报告的图表悬浮提示三缺陷：① 观感"没样式一样"；② 配色不随主题（夜间模式下刺眼）；③ token 数量不用单位简写、千分位全量数字在小气泡里不可读。覆盖两套悬浮面：TrendChart 的 ScottPlot 气泡 + DonutChart/Heatmap 的 Avalonia `ToolTip`。

## Background

- 用户 2026-10-16 报告原话要点："比较丑，看起来没样式一样""颜色不遵守主题，统一是白色，在夜间模式下很刺眼""上面显示的 Token 数量，没有用单位简写，在小小的 tips 上根本看不清"。
- **两套悬浮机制，两条病灶**：
  - ScottPlot Tooltip（TrendChart）：创建时只设了 `LabelPadding`，其余全默认——`FillStyle.Color` 默认 `Colors.Transparent`（气泡无底！）、`LabelStyle.ForeColor` 默认黑、`LineStyle.Color` 默认黑（源码已核对 5.1.59 tag）。结果 = 透明底 + 黑字黑框：Dark 主题卡片（#121e31）上整体隐形，Light 下也是孤零零的黑骨架，观感即"没样式"。
  - Avalonia `ToolTip`（DonutChart/Heatmap）：走 Semi 12.1 默认 ControlTheme，`ToolTipBackground = SemiColorTertiaryActive`、`ToolTipForeground = SemiColorBlack`（Dark）/ `SemiColorWhite`（Light）——配色源不在本应用令牌体系且明暗对调错配（Dark 深底黑字基本不可读；Light 浅底白字）。
- 文本：三处（日形态 in/out/tot 行、小时形态、环形/热力格）token 数全用 `N0` 千分位，1e6~1e12 量级日耗在 12px 气泡里 5-9 位数字挤作一团；紧凑单位（`FormatCompactTokens`）已在全局 KPI/表列广为复用，tooltip 反而是漏网之鱼。
- 约束接续：ScottPlot 不吃 DynamicResource（I-032 已定 RebuildPlot 整图重建 + `TokenColor` 现取模式，`ApplyChartChrome` 是既定范式）；图内渲染恒 ASCII（无 CJK 字形，I-032 锁定的既有结论），故图内 tooltip 单位恒 K/M/B，与轴刻度 `FormatCompactAxisValue` 同制——中文 万/亿 口径留给 Avalonia 弹层（Donut/Heatmap tooltips 用 `FormatCompactTokens`，随 UI 语言）。

## Acceptance Criteria

- [x] 用户三种缺陷均消除：气泡有底有描边、配色随明暗令牌、token 数紧凑单位
- [x] 两套悬浮面统一视觉语汇：chip 底 + AppCardLineBrush 描边 + AppTextBrush 文字、12px 字号（ScottPlot 侧 `ApplyTooltipChrome` 现取令牌随明暗重建；Avalonia 侧 ToolTip 实例挂 `chart-tip` 类，SharedStyles 令牌化）
- [x] 回归锁：样式属性随明暗变体（ScottPlot）+ chart-tip 类与令牌画刷真实应用（ToolTip 实例）+ 悬停帧像素级气泡底色出现/消失 + 文案紧凑断言；既有"成本四态 / no data"契约不动
- [x] 视图/控件不新增硬编码色值（TokenColor fallback = 原型 Dark 原值，与 ApplyChartChrome 同模式）；不新增令牌键（复用 AppChipBackgroundBrush / AppCardLineBrush / AppTextBrush，无 Semi 撞名）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] `TrendChart.ApplyTooltipChrome`：FillColor/LineColor/LabelStyle.ForeColor/FontSize 令牌化
- [x] `TrendChart.FormatCompactTooltipValue`（ASCII 紧凑，tickPrecision=false 全精度档）换掉 tooltip 内的 `FormatTokens`（连同其死代码删除）；小时形态同换
- [x] `ChartTooltips.Set` 辅助：显式 ToolTip 实例挂 `chart-tip` 类；DonutChart/Heatmap 接线并换紧凑文案
- [x] `SharedStyles.axaml` 增 `ToolTip.chart-tip` 令牌化类样式；design-tokens.md §4 登记条目
- [x] 回归锁更新与像素级测试 + 全量测试

## Dependencies

- none（缺陷修复，挂在 D-005/I-032 引入的图表悬浮面上）

## Test Cases

### TC-001: ScottPlot 气泡配色跟随明暗令牌

Steps:
1. Dark 组建 TrendChart 重建 → 取 `PlottableList.OfType<Tooltip>().Single()` 断言 FillColor/LineColor/LabelStyle.ForeColor/FontSize；窗口变体切 Light 再次重建。

Expected: Dark = #1b2c44 / #22324a / #e8eef9；Light = #e3ecfb / #d8e0eb / #1c2735；FontSize 12。

Results: PASS（`Tooltip_bubble_colors_follow_theme_tokens`）。

### TC-002: 悬停帧像素级（回归锁，双向验证）

Steps:
1. 未悬停渲染帧断言无 #1b2c44 像素（tolerance 1）；悬停真实日 → 帧内出现精确 #1b2c44 底色像素 + 近白文字像素，存档 PNG；远离数据 → 帧内气泡底色像素消失。

Expected: 三段断言全过（修复前旧码：透明气泡 → 悬停帧无 #1b2c44，锁将红）。

Results: PASS（`Hover_renders_themed_bubble_pixels_and_archives`；旧码未跑反向验证——同帧像素断言逻辑直接对应缺陷本体）。

### TC-003: chart-tip 类真实应用（Avalonia 弹层）

Steps:
1. Heatmap 单格 GetTip 为显式 ToolTip 实例且挂 chart-tip 类、Content 含紧凑数值（zh-Hans → "10万 tok"）；`SetIsOpen` 后断言实例 Background/Foreground = AppChipBackgroundBrush/AppTextBrush 同一画刷实例。

Expected: 动态资源命中仓库令牌而非 Semi 调色板。

Results: PASS（`Cell_tooltips_show_day_value_or_no_data` / `Chart_tip_class_tokens_apply_on_open`）。

### TC-004: 文案口径

Steps:
1. `FormatCompactTooltipValue` theory（0/42/1234/1500/402000/1.23M/1B 越档/2.5T/1Q，语言无关）；
2. hover 真实日事件文本含 "tot 556K tok"、"in  402K"、不含千分位 "556,000"；成本行保持 $x.xx 对账口径；四态 / no data / 断线契约既有用例不动仍绿。

Expected: 全过；Heatmap/TimeCost 两处千分位断言随口径同步更新（"10万 tok" / "6750 tok"）。

Results: PASS（全量套件绿）。

### TC-005: 全量回归

Steps: `dotnet test Tokenmetry.sln`。

Results: PASS。Core 159 / Reconciliation 49（19 skip 既有）/ Infrastructure 652 / App 127 / Headless 279，0 失败。

## Development Log

### 2026-10-16

- 建卡（用户报告 → 源码定位两条病灶：ScottPlot Tooltip 全默认样式 + Semi ToolTip 错配；版本源码逐字段核对 5.1.59 的 FillStyle/LabelStyle/LineStyle 默认值与 Semi Dark/Light Tooltip.axaml）。
- ScottPlot 侧：新建 `ApplyTooltipChrome`（chip 底/卡片描边/文本色/12px，RebuildPlot 重建时现取令牌）+ `ApplyTooltipChrome` 调用点替换原三行；`FormatCompactTooltipValue` 换掉 `FormatTokens`（后者全仓无其他使用点，删）；`Tooltip.Padding=6` 让圆角半径对齐 Avalonia 侧。
- Avalonia 弹层侧：复用一个工具类 `ChartTooltips.Set`（显式 ToolTip + Classes["chart-tip"]）；SharedStyles 增 `ToolTip.chart-tip` 类样式（Foreground/Background/BorderBrush 三令牌 + 圆角/内距/字号）；DonutChart/Heatmap tooltip 文案换 `TrendChart.FormatCompactTokens`（随 UI 语言 K/M 或 万/亿，进位算法与图内同源）。未采用"重写 Semi 的 ToolTip 类型级 ControlTheme"路线（BasedOn 静态解析在 StyleInclude 里不保真；实例挂类是可见生效路径，且不与 Semi 资源键撞名）。
- headless 截图目检（trendchart-hover-dark.png）：Dark 下 chip 蓝气泡 + 亮色文字 + 紧凑数值（in 402K / out 154K / tot 556K tok / $1.82）清晰弹出，边缘翻转与十字线无回归。
- 明确不做：① 轴刻度仍走 `FormatCompactAxisValue`（tickPrecision 档）不动；② KPI/表列紧凑口径不动；③ 不引入新令牌键、不改 Semi 资源键（无撞名嫌疑）；④ 成本行保持两位小数对账口径（成本数字不用紧凑单位，对账锚点）；⑤ ScottPlot 图内恒 ASCII（CJK 字形缺失为 I-032 锁定结论，中文 万/亿 口径只在 Avalonia 弹层）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Controls/TrendChart.axaml.cs`、`src/Tokenmetry.App/Controls/ChartTooltips.cs`（新）、`src/Tokenmetry.App/Controls/DonutChart.axaml.cs`、`src/Tokenmetry.App/Controls/Heatmap.axaml.cs`、`src/Tokenmetry.App/Styles/SharedStyles.axaml`、`docs/design/design-tokens.md`、测试 `TrendChartTests.cs` / `HeatmapTests.cs` / `TimeCostPagesTests.cs`
- How to run/verify: TC-001~TC-004 按名过滤单跑；TC-005 全量；真机体感 = 悬停任意趋势图/环形图/热力图，气泡应弹出 chip 底色、文字随主题、token 数带单位
- Results: 五条 TC 全 PASS；截图留档 `TOKENMETRY_CHART_PREVIEW_DIR`（trendchart-hover-dark.png 等 9 张）
