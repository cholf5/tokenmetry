---
id: D-027
title: Overview 单日窗口趋势图切小时形态——今天/昨天不再单点死图（会话开始时间归因，口径差异可见）
type: feature
priority: P3
size: M
status: done
created: 2026-10-30
updated: 2026-10-30
blocked: none
---

## Goal

Overview 页的 Token 使用趋势与成本趋势，在查询窗口恰好单日时（内置今天/昨天档与自定义单日档）把 X 轴切到当天小时刻度（0..23 全量桶），画出日内曲线，替代当前"只有一个点"的死图形态；非单日窗口两张图行为零变化。

## Background

- 用户提案（2026-10-30）：切到"今天"或"昨天"后两张趋势图只剩一个点、画不出曲线，原因是窗口只有一天的数据；建议单日时把 X 轴换成当天的时间点、以每小时或每两小时为刻度。
- 评估结论：值得做——单点图没有信息量，今天/昨天是最高频的时间档；且基础设施大半现成：
  - `TrendChart` 已有 `HourlyTokens` 形态（`Controls/TrendChart.axaml.cs` 的 `BuildHourlyLine`：0..23 全量桶折线+圆点+悬停浮窗，时间分析页在用）。
  - Core 已有 `GetHourlyDistributionAsync(filter)`，与日序列吃同一个 `UsageQueryFilter`；`HourlyUsageBucket` 已带 `Cost`（`AggregatedCost`，`HourlyDistributionModels.cs`）——成本趋势只需给 TrendChart 新增 `HourlyCost` 形态，不需要新查询模型。
- 口径坑（本卡的核心决定）：I-029 的小时分布是**会话开始时间归因**——按会话 `first_seen_at` 的本地小时归桶，整个会话的 token/成本记到开始那个小时（`UsageQueryService.GetHourlyDistributionCoreAsync`）；粗粒度时间戳会话剔除进 unknown；会话按 first_seen_at 落窗，跨天会话整个不在单日窗口内。这与 Overview 日序列/KPI 的口径（usage_facts 按 `fact.Day` 归日）不一致：小时曲线加总 ≠ KPI 总量（昨天开始今天还在跑的会话消失、Goose 等日粒度来源缺块）。本项目对口径诚实性有既定纪律（I-028 补零语义、P2-7 未知成本不折 0、I-029 unknown 可见而非伪造），直接换轴等于悄悄换归因口径，必须把差异做成可见。
- 拍板（2026-10-30 会话）：走路线 A——复用会话开始归因口径，tooltip/页面标注讲清楚，unknown 占比对齐时间分析页的既有做法。**明确不做**路线 B（usage_facts 加小时维度：新 schema 迁移 + 采集层按小时聚合；存量行聚合时分钟级时间戳已丢、无法回填，历史只能靠源文件重采）——将来若要精确的"用量发生时间"归因，另开卡。

## Acceptance Criteria

- [x] 触发条件 = 查询窗口恰好单日（`FromDay == ToDay`）：内置今天/昨天与自定义单日档都命中；非单日窗口 Token/成本两张图行为零变化（日序列形态、DateTimeTicksBottom 原样）
- [x] Token 趋势单日 → 小时形态（0..23 全量桶，折线+圆点+悬停，复用 `BuildHourlyLine` 既有视觉）
- [x] 成本趋势单日 → 小时成本折线（TrendChart 新增 `HourlyCost` 形态；"未知成本绝不静默折算为 0"的 P2-7 纪律在小时桶延续——有用量但无价的桶断线/淡点，语义与 `BuildCostLine` 同构）
- [x] "今天"档尚未到达的小时不画成真实 0——沿用日序列补零日淡化小点的视觉语言辨识"还没发生"（昨天档 0..23 全为真实过去，不受此条影响）
- [x] 口径差异可见：小时图 tooltip 标注"按会话开始时间归集"；unknown（粗粒度时间戳/被剔会话）的占比可见性至少与时间分析页同级
- [x] 新增文案进 Translations 简体源 + 全部 10 语种同步补齐（`TranslationsTests` 键奇偶与占位符不变式固化）；测试断言语言无关
- [x] 明确不做：两日窗口的形态变化（仍走日序列 2 点，不扩）；usage_facts 小时维度改造（路线 B）；小时查询并入 `GetOverviewSnapshotAsync` 的性能优化（先独立查询，sessions 表量小）

## Dependencies

- none（复用 I-028 日序列 / I-029 小时分布既有基础设施，与在途卡无交集）

## Development Log

### 2026-10-30

- 建卡。单点死图的机理：日序列（I-028）在单日窗口只产出 1 个点，`TrendChart.BuildLineRuns` 成线需 ≥2 个连续点，日历补零也无从补起（窗口只有一天），于是 Token 趋势只剩 1 个孤立实心点、成本趋势 0 段成线。
- 评估取证留档（实现时的地图）：
  - `src/Tokenmetry.App/Controls/TrendChart.axaml.cs`：`BuildDailyLine` / `BuildCostLine`（日形态；补零日淡化点、未知成本断线）、`BuildHourlyLine`（小时形态，仅 `HourlyTokens` 模式，X 轴 integer formatter）、`BuildLineRuns`（≥2 连续点成线）。
  - `src/Tokenmetry.Core/Services/UsageQueryService.cs`：`GetHourlyDistributionCoreAsync`——会话 `first_seen_at` 归桶 + `HasMinuteLevelTimestamp` 剔除进 unknown（宁可排除不伪造）。
  - `src/Tokenmetry.Core/Models/HourlyDistributionModels.cs`：`HourlyUsageBucket` 已含 Input/Output/CacheRead/Total tokens 与 `Cost`。
  - `src/Tokenmetry.App/ViewModels/OverviewViewModel.cs`：`DailyPoints` 同源喂两张趋势图（I-028 单列成本）；单日时需并行取小时分布。
  - `src/Tokenmetry.App/ViewModels/TimeViewModel.cs`：小时分布 + unknown 占比的消费先例。

### 2026-10-30（实现收口）

**做了什么**

- `TrendChart`（`Controls/TrendChart.axaml.cs`）
  - 新增 `TrendChartMode.HourlyCost` 形态（`BuildHourlyCost`）：分段规则与 `BuildCostLine` 同构——已知成本小时与「无任何会话」的小时（成本真实 $0）成线、有用量但无价的小时断线（未知成本绝不折 0），不叠面积填充（同成本日形态的视觉诚实取向）。
  - 新增 `HourlyCutoffHour` 样式属性（`int?`）：null = 24 小时全已发生（昨天/多日窗口，行为与改动前逐位一致），值 = 最后一个已发生小时（含；-1 = 整天未到）。`BuildHourlyLine` 据此把曲线/面积填充/实心点只画到边界内，其后的小时只留淡化小点（锚零轴，复用补零日视觉语言）——**未来小时绝不被连成「降到零」的曲线**。只剩 1 个已发生小时时不成线（由标记点呈现）。
  - 纯函数 `RealHourCount(hourCount, cutoffHour)` 承担夹回（越界钟漂不画到桶外，测试固化）。
  - tooltip 文案抽成 `HourlyTokenTooltipText` / `HourlyCostTooltipText`：已发生小时带口径标注「按会话开始时间归集」，尚未到达的小时只写「尚未到达」（不冒充 0 用量）。时间分析页分享同一实现，故小时 tooltip 也多了这一行口径标注（有意为之，与页脚 meta 同义）。
- `OverviewViewModel`
  - 单日判定 `IsSingleDayFilter(filter)`（双端确定且相等）+ `IsSingleDayWindow` 可观察状态；派生 `TokenTrendMode` / `CostTrendMode`（单日 → `HourlyTokens` / `HourlyCost`，其余 → `DailyTokens` / `CostUsd`）。
  - `LoadAsync` 在单日窗口与复合快照**并行**发小时查询（不并入快照，卡内已明确不做那步性能优化），快照先落地先渲染、小时数据回填再翻形态；非单日窗口复位三个小时字段，日形态数据面零改动。
  - `HourlyCutoffHour` 由纯函数 `ResolveHourlyCutoffHour(toDay, now)` 决定（now 注入供测试钉死时钟）：今天 → 当前小时；未来单日 → -1；昨天及更早 → null。
  - unknown 占比与时间分析页**同一实现**：把 `SumKnownSessions` / `BuildUnknownNote` 从 `TimeViewModel` 抽到新的 `App/Services/HourlyAttribution.cs`，两处 VM 都调它（口径同级靠共享代码而非「看起来差不多」；`TimeViewModel` 保留同名 internal 转发，既有测试零改动）。
- `OverviewView.axaml`：两张图改为 `Mode` 绑定 VM 派生属性并接上 `HourlyDistribution` / `HourlyCutoffHour`；单日时图例从「按日汇总」换「按小时」、输入/输出图例随堆叠柱一起隐藏（不列不存在的系列）；图下加口径标注行（单日可见）与 unknown 占比行（有 unknown 才可见）。
- i18n：新增 3 键（`LegendHourly` / `ChartTipHourNotYet` / `AttributionSessionStart`），简体源 + zh-Hant/en/ja/ko/pt-BR/ru/es/de/fr 全量补齐；`TranslationsTests` 键奇偶与占位符不变式全绿。

**测试（+17 headless，断言语言无关）**

- `TrendChartTests`：`RealHourCount` 夹回 Theory（7 例）、cutoff 让曲线止于边界且未来小时只留 18 个淡点、单已发生小时不成线、`HourlyCost` 在未知成本小时断成两段、两种小时 tooltip 的归因/未知成本/尚未到达文案。
- `OverviewTests`：今天档切小时形态（含 cutoff = 当前小时、unknown 标注等于时间分析页同一函数）、切回 7 天档形态与小时字段全复位、多日窗口**一次都不查**小时分布（`HourlyCalls == 0`）、昨天档 cutoff 为 null、cutoff/单日判定纯函数边界、真实 `OverviewView` 渲染出两张小时图且口径标注可见（编译绑定回归锁，截图留档 `overview-hourly-dark.png`）。
- 全量：Core 222 / Reconciliation 49（19 跳过）/ App 325 / Infra 761（3 跳过）/ Headless 509，0 失败。

**已登记边界**

- 今天的未来小时淡点落在零轴上，与 x 轴重叠时可见度低（与补零日同一视觉语言，属刻意一致）；悬停可确认「尚未到达」。
- 小时曲线加总 ≠ 上方 KPI（会话开始归因 vs facts 按日归日；跨天会话只落在开始那天、日粒度来源缺块），页面以「按会话开始时间归集」+ unknown 占比两行标明；这是路线 A 的既定代价，路线 B（usage_facts 加小时维度）仍不在本卡内。
- 单日窗口多一次 sessions 读（与快照并行），未并入快照查询——按卡内 AC 明确不做。
