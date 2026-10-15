---
id: I-032
title: 趋势图视觉升级（单调平滑曲线 + Web 风 chrome）+ 只读交互（禁用缩放/平移）
type: feature
priority: P2
size: S
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

用户反馈 TrendChart 太丑、想要 Web 图表那样的平滑曲线，且不想要滚轮缩放（参照 ZCode Web 端图表的静态观感）。三形态（日趋势 / 成本 / 小时）折线升级为单调平滑曲线 + Web 风 chrome（水平网格、无边框、无刻度须、面积填充），交互改为只读（禁用缩放/平移），悬停不变。

## Background

- 原实现折线为直线段连接（ScottPlot Scatter 默认 Straight），观感生硬；AvaPlot 默认输入响应带滚轮缩放 / 拖拽平移 / 双击复位，对固定信息架构的仪表盘而言是噪音交互（用户明确要求去掉）。
- 平滑算法选型：ScottPlot 内置 `Scatter.Smooth` 是 Catmull-Rom，在 usage 序列的常态形状（长期零值 + 单日尖峰）下会甩到零轴以下（伪负 token）并在峰顶过冲——离屏渲染实验证实。改为自实现 Fritsch–Carlson 限幅单调三次 Hermite（d3 curveMonotoneX 同源），数学上保证不越过数据包络。
- ScottPlot 不吃 Avalonia DynamicResource，chrome 色沿用 RebuildPlot 时按变体现取令牌的既有管线，无新增令牌。
- 面积填充用 Scatter 内建 FillY（零基线、线色 20% 不透明），仅小时形态开启；柱形态会叠脏柱色，成本形态稀疏已知日下填充块读作"怪柱子"且暗示日间连续性（与断线诚实语义冲突），均不开。

## Acceptance Criteria

- [x] 三形态折线单调平滑：`MonotoneDenseCurve` 纯函数（Fritsch–Carlson 限幅，16 段/跨度密集化），尖峰数据零过冲、零段无伪负值；退化输入（点数不足 / x 非严格递增）原样返回退化为直线，绝不抛异常
- [x] Web 风 chrome：纵向网格关（只剩水平主网格）、四边无边框、无刻度须；水平网格色走 AppChartGridLineBrush、刻度文字 AppMutedTextBrush 10px；边距 left/right 2% / bottom 3%（零线抬离下缘，避免 y 轴「0」与首个日期标签重叠）/ top 10%
- [x] 面积填充按形态：仅小时形态（零基线、线色 20% 不透明）；柱形态与成本形态不加
- [x] 只读交互：`UserInputProcessor.Disable()`——滚轮缩放 / 拖拽平移 / 双击复位全部关闭；悬停（crosshair + tooltip，自有 Avalonia 事件管线）不受影响；InteractionsEnabled 属性固化只读语义
- [x] 全量测试通过（5 工程），TrendChartTests 明暗渲染 / hover / 诚实空态 / 成本断线等既有用例零回归

## Subtasks

- [x] TrendChart：MonotoneDenseCurve + AddSmoothLine helper，三形态 Build* 换线
- [x] ApplyChartChrome：纵向网格关、无边框、无刻度须、边距
- [x] 构造器 Disable 交互 + InteractionsEnabled 属性
- [x] TrendChartTests 新增 8 用例；StylePreviewView 交互提示文案同步

## Dependencies

- D-004（ScottPlot 趋势图正式集成）、I-027（令牌体系）

## Test Cases

### TC-001: 单调平滑纯函数

Steps: 尖峰序列（长期零 + 单日 4.4e8 + 回落）、两点、单点/空、x 非严格递增。

Expected: 密集化 (n-1)*16+1 点、x 严格递增、端点保持、denseYs.Max()=峰值且 Min()=0（不过冲不出负）；两点成 S 曲线无过冲；退化输入原样返回。

Automated: TrendChartTests.MonotoneDenseCurve_*（4 用例）。

### TC-002: 只读交互 + Web chrome

Steps: DailyTokens 渲染后断言控件状态。

Expected: InteractionsEnabled=false；Grid.XAxisStyle.IsVisible=false、YAxisStyle 可见；四轴 FrameLineStyle.Width=0；刻度须 Length=0。

Automated: TrendChartTests.Input_interactions_are_disabled_and_chrome_is_web_style。

### TC-003: 平滑与面积填充按形态生效

Steps: DailyTokens / HourlyTokens 渲染，取 LineWidth>0 的 Scatter 断言。

Expected: 两形态折线均为密集采样（点数 > 原始点数）；DailyTokens FillY=false；HourlyTokens FillY=true 且填充色 Alpha<255。既有成本形态断线用例（run 数量守恒）回归通过。

Automated: TrendChartTests.Line_series_are_dense_smooth_and_area_fill_depends_on_mode。

## Dev Log

- 2026-10-15：Catmull-Rom（Scatter.Smooth）离屏渲染对比实验：尖峰形状甩到零轴以下（伪负 token）且峰顶过冲，弃用；单调插值贴包络，视觉即 d3 curveMonotoneX。
- 2026-10-15：成本形态面积填充首版开启后目检发现两天 run 被填充成"怪柱子"，且与断线（日间成本未知）语义冲突，撤掉只保留小时形态。
- 2026-10-15：底部边距 0 会让 y 轴「0」刻度与首个日期标签在左下角重叠，调为 3%；headless 明暗两套截图（TOKENMETRY_CHART_PREVIEW_DIR 留档）目检通过。
- 2026-10-15：AppChartAxisLineBrush 不再被 TrendChart 用作边框色（令牌保留，其余消费方不动）。
