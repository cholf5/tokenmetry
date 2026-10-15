---
id: D-004
title: ScottPlot 趋势图正式集成（堆叠柱 + 折线 / hover helper）
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

基于 D-002 spike 把趋势图做成正式可复用控件：日趋势（输入/输出堆叠柱 + 总量折线）、成本折线、按小时折线三种形态；落地 I-028 定案的日历窗口语义；封装 hover helper（crosshair + tooltip + 右缘/上缘裁剪翻转）。

## Background

- D-002 结论：单图核心 ~130 行，tooltip helper 100–150 行，成本可控；ScottPlot 与 Avalonia 同名类型（Pixel/PixelSize/Color）需全限定，绘图代码集中在单一 code-behind/partial。
- 原型趋势图为「输入/输出堆叠柱 + 总量折线」叠加；成本图为纯折线；时间分析页有 24 小时折线。
- 交互增强（网页原型没有的）：缩放/平移 AvaPlot 默认开启，保留；tooltip 数值格式与对账口径一致。

## Acceptance Criteria

- [x] 日趋势控件：堆叠柱 + 总量折线，网格/轴/文字样式贴合 I-027 配色
- [x] 成本折线与小时折线复用同一 helper（形态参数化）
- [x] hover：crosshair + tooltip，右缘/上缘裁剪翻转处理（D-002 遗留）
- [x] 日历窗口语义按 I-028 定案落地，图上可辨识（空日补零等）
- [x] 明暗主题下轴/文字/系列色跟随；缩放平移无回归

## Subtasks

- [x] TrendChart 控件封装（三种形态）
- [x] hover helper（拾取 + 对齐翻转 + 格式化）
- [x] 主题色接入与明暗切换
- [x] 用 I-028 真实日序列联调

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

### 2026-10-13

- **TrendChart 控件落地**（`src/Tokenmetry.App/Controls/TrendChart.axaml(.cs)`，绘图代码单一 code-behind，沿用 D-002 结论；spike 页保留不动）。
- **API 形态**：无 DataContext 依赖，数据全部走样式属性——`Mode`（`TrendChartMode.DailyTokens | CostUsd | HourlyTokens`）、`DailyPoints`（I-028 `IReadOnlyList<DailyUsagePoint>`）、`HourlyDistribution`（I-029 结果）；属性变更即整图重建。另暴露 `Plot`（宿主页缩放复位用）、`HoverChanged` 事件（IsActive/Index/LabelText，页面状态行可订阅）、`ShowHoverAt(Coordinates/Point)`（测试与宿主复用的显式悬停入口）。三形态复用同一 AvaPlot + 同一 hover helper。
- **堆叠柱实现**：输入柱 `ValueBase=0`、输出柱 `ValueBase=输入`（真堆叠；原型 SVG 的半透明覆盖是实现技巧，此处取语义等价的正确堆叠），柱宽 0.72 天；总量折线叠于其上。
- **补零日辨识手法**（I-028 `HasData=false`）：折线保持连续（日历窗口语义：补零让 x 轴回归真实日历，不做断线美化），有数据日画数据色实心大点（cyan，size 5），补零日画网格色淡化小点（size 3）——"亮大点=真实日、淡小点=补零日"，柱体在补零日高度为 0 自然不可见；tooltip 明示 `no data (zero-filled)`（与真实 0 用量日区分）。成本形态同理：已知成本日实心点、未知/空日淡点。
- **主题重绘机制**：ScottPlot 不吃 Avalonia DynamicResource——控件订阅自身 `ActualThemeVariantChanged`（EffectiveVariant 从 App/Window 继承，覆盖 light/dark/system 三种 ThemeService 路径），变更时 `RebuildPlot()` 整图重建，chrome 色（网格/轴/刻度文字/crosshair）在重建时按当前变体从令牌现取。缩放/平移保留 AvaPlot 默认交互（未改交互代码）。
- **新增令牌**：`AppChartGridLineBrush`（Dark=#23344b 原型 .gridline 原值 / Light=#d9e1ec 补值）、`AppChartAxisLineBrush`（Dark=#35465e 原型 .axis 原值 / Light=#b9c5d7 补值），进 ThemeDictionaries；刻度文字复用 `AppMutedTextBrush`、图例三色复用 `AppAccentBlue/Purple/CyanBrush`（= 原型图例 var(--blue/--purple/--cyan)）。DesignTokenTests 期望值表已同步，Semi 撞名检查随既有用例覆盖。
- **hover helper**：拾取沿用 spike 路径（`GetNearestX` + 15px 半径）；裁剪翻转收口为纯函数 `ComputeTooltipAlignment(点像素, 数据区)`——右半 → 气泡左伸（`*Right`）、上半 → 气泡下伸（`Upper*`），四象限单测覆盖。实现期发现并修复时序坑：`DateTimeTicksBottom()` 会替换 bottom 轴实例，chrome 必须在数据布完后统一应用。
- **格式化口径**：token `N0` 千分位、成本 `F2` USD；`FormatCostUsd` 对齐未知成本契约——`(null,true)` 不出现成本行/显式 `cost unknown`，`(x,true)` 显示 `$x.xx+`（已知成本下限）；轴刻度用紧凑格式（1.2M/850K，ASCII）。ScottPlot 内渲染文本保持 ASCII（避免 CJK 字形缺失），中文说明走 `HoverChanged` 事件 / 页面文案。
- **预览接线**：`ChartPreviewViewModel`（fixture ↔ 真实 `IUsageQueryService` 开关 + 7/30/90 范围）挂 StylePreviewView 图表演示区（最小侵入，无新页面、无导航入口）；真机 hover 目检入口 `TOKENMETRY_PREVIEW_WINDOW=1 dotnet run --project src/Tokenmetry.App`。
- **测试**：新增 headless 16 例（`tests/Tokenmetry.App.Headless.Tests/Controls/`）：三形态明暗渲染（系列色像素命中）、hover 翻转纯函数四象限、悬停事件与文案口径（千分位 / `F2` / `+` 后缀）、补零日文案、空态诚实展示、主题变体重绘（轴文字色随令牌变化）、I-028 真实日序列联调（30 天窗口 + facts 空态断言）、token/成本格式化。全量 897 通过 / 22 跳过 / 0 失败 / 0 警告（基线 860 + 37）。
- **遗留风险**：①真机 hover 手感（tooltip 气泡跟随延迟、拖拽平移手感）未在桌面会话目检——本机自动化环境无法拉起 AvaloniaNative 渲染定时器，需真机按上述入口验证；②ScottPlot tooltip 为库内渲染，文字样式（圆角/描边）未对齐原型 tooltip 样式，页面卡若要求逐像素还原需换自绘 overlay；③小时形态的 unknown 会话占比由页面层展示（控件不画），D-012 时间分析页接线时注意。

### 2026-10-13（独立审查修复轮）

- **[P1] tooltip 未知成本契约塌缩修复**：`DailyTooltipText`/`CostTooltipText`（改为 public static 纯函数）按
  `AggregatedCost` 四态逐态区分——日形态 `(null,true)` 追加 "cost unknown" 行、`(null,false)` 无成本行不变；
  成本形态 `(null,false)` → "no cost data"、`(null,true)` → "cost unknown"，不再共用一句。headless 逐态断言
  四态 × 两形态（`Daily_tooltip_distinguishes_all_four_cost_states` / `Cost_tooltip_distinguishes_all_four_cost_states`）。
- **[P1] 空态语义定案 + 测试环境无关化**：控件新增 `IsEffectivelyEmpty`——双端有界窗口内所有点 `HasData=false`
  （I-028 全补零窗口）等同无数据，与空集一样显示"该时间范围内没有用量记录"空态，不再画伪零线（补零语义不冲突：
  补零是"有数据窗口内的空日"，全空窗口是"没有数据"）。原 `Real_query_..._matches_facts` 依赖本机 facts 非空（本机有数据
  掩盖了空态断言必败），替换为：stub `IUsageQueryService` 注入全补零/部分数据两条路径（经 `ChartPreviewViewModel`
  真实查询模式）+ 真实服务降级为冒烟（渲染不崩 + 空态可见性 == 全补零谓词，不依赖本机数据有无）。
- **[P2] 成本形态视觉诚实性**：新增纯函数 `BuildLineRuns`（连续已知成本且 ≥2 天才成线），折线只连已知成本日，
  `(null,*)` 日不再画到零轴（淡点保留作悬停锚点）；悬停拾取从 `GetNearestX`（依赖单个 scatter）重构为像素 X 空间
  最近点匹配（`PickNearestIndex`，半径/平手语义与原实现一致）——断线后 null 成本日仍可悬停，且 hover 管线与
  plottable 解耦。测试：`BuildLineRuns` 纯函数 + plottable 连线段数断言 + 补零日悬停 "no cost data"。
- **[P2] 配套**：DonutChart 百分比去掉 `Math.Max(1,total)` 下限（单段 total<1 显示 100% 而非 50%，测试锁定）；
  Donut/Heatmap 的 INCC 订阅改为 Attach/Detach 对称重挂（detach→reattach→集合变更仍生效，两控件各补测试）；
  ChartPreviewViewModel fixture 环心总量与 Agent 段同源（段值=日序列合计×原型占比，余量入末段）+ LoadAsync
  序号防护（旧完成不覆盖新选择）；App 预览窗随主窗关闭（主窗可最小化到托盘，防预览窗残留）。
- **记录不修**（遗留风险追加）：四象限 y 恰中线（`ComputeTooltipAlignment` 边界取等方向）未单测；tooltip 气泡
  像素级裁剪断言未做（仅象限翻转语义）；ScottPlot 网格竖线（原型只画横线）、柱圆角、系列色 1-2 色阶偏差存在。
- **遗留风险**（前序条目 ①②③ 维持，另见上条"记录不修"）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Controls/TrendChart.axaml`、`src/Tokenmetry.App/Controls/TrendChart.axaml.cs`、
  `src/Tokenmetry.App/ViewModels/ChartPreviewViewModel.cs`、`src/Tokenmetry.App/Views/StylePreviewView.axaml(.cs)`（演示区）、
  `src/Tokenmetry.App/Styles/Tokens.axaml`（+2 图表 chrome 令牌）、`src/Tokenmetry.App/App.axaml.cs`（TOKENMETRY_PREVIEW_WINDOW 钩子）、
  `tests/Tokenmetry.App.Headless.Tests/Controls/TrendChartTests.cs`、`tests/Tokenmetry.App.Headless.Tests/Controls/ChartTestHelpers.cs`、
  `tests/Tokenmetry.App.Headless.Tests/Styles/DesignTokenTests.cs`（令牌期望值同步）
- How to run/verify:
  - TC-001（自动化）：`dotnet test tests/Tokenmetry.App.Headless.Tests --filter FullyQualifiedName~TrendChartTests`
    ——三形态 × 明暗渲染（系列色像素命中 + 截图留档）、主题变体重绘（轴文字色 #8293ad↔#66788f）、空态、格式化口径。
  - TC-002（自动化部分）：`ComputeTooltipAlignment` 四象限翻转单测 + `ShowHoverAt` 悬停管线（真实/补零/未知成本日的
    tooltip 文案断言）。真机 hover 手感（气泡跟随、拖拽/缩放）未自动化：`TOKENMETRY_PREVIEW_WINDOW=1 dotnet run
    --project src/Tokenmetry.App` 打开预览窗，在"图表控件"区悬停最右/最上数据点目检（本机自动化环境无法启动
    AvaloniaNative 渲染定时器，留待真机）。
  - 真实数据联调冒烟（环境无关）：`Real_query_service_smoke_renders_consistent_with_facts_shape` 走真实
    `IUsageQueryService` 30 天窗口，只断言渲染不崩 + 点数=30 + 空态可见性与 facts 实际形状一致（不依赖本机
    facts 有无数据）；空态/补零两语义路径由 stub `IUsageQueryService`（`Query_service_all_padded_series_flows_into_honest_empty_state`
    / `Query_service_partial_series_draws_padded_chart_without_empty_state`）环境无关覆盖。
  - 截图再生成命令（P2-3）：`TOKENMETRY_CHART_PREVIEW_DIR=docs/design TOKENMETRY_STYLE_PREVIEW_DIR=docs/design
    dotnet test tests/Tokenmetry.App.Headless.Tests`（环境变量未设置时仅断言可渲染，不写文件）。
- Results: 全仓 `dotnet test Tokenmetry.sln` → **908 通过 / 22 跳过 / 0 失败，0 警告**（基线 860 + 48 新增，无回归；
  审查修复轮净增 11 例：四态 tooltip、空态语义、断线、reattach 等）；截图留档
  `docs/design/trendchart-{daily,cost,hourly}-{light,dark}.png`（6 张，成本形态为断线版）与
  `docs/design/style-preview-{light,dark}.png`（含图表演示区的整页）。
