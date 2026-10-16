---
id: B-007
title: 语言切换后图表 tooltip 文案陈旧（Heatmap/DonutChart 缺 CurrentLanguage 重译订阅）
type: bug
priority: P3
size: S
status: todo
created: 2026-10-16
updated: 2026-10-16
blocked: none
---

## Goal

切换 UI 语言后，热力图与环形图的 tooltip 文案（token 单位口径 + Translations 文案）立即随新语言重构，不再需要"等下一次数据变更重建"才换语言。

## Background

- 源自 B-006（d268368）收口后的 code review（2026-10-16）。
- B-006 把 Donut/Heatmap 的 tooltip 文案换成随 UI 语言的 `TrendChart.FormatCompactTokens`（zh 万/亿 / en K/M）后，tooltip 文案是 build-time 固化字符串：`Heatmap.BuildCellTooltip`（Heatmap.axaml.cs ~L180）与 Donut 段 tooltip 都在 `Rebuild()` 时求值。
- 同类 build-time 固化还有两处：热力图"无数据"文案（`Translations.Instance.HeatmapNoDataTooltip`，Heatmap.axaml.cs ~L183，**旧账**，B-006 之前就固化）与 Donut 空态中心副题（`CenterSubtitleText.Text = NoData`，DonutChart.axaml.cs ~L222）。
- 仓内既有重译约定（Translations.cs 头注释）：命令式渲染的订阅方以 `Translations.PropertyChanged(nameof(CurrentLanguage))` 作为"每次切换一次"信号重译；先例 `DataPricingViewModel.cs:329`、`SettingsViewModel.cs:229`、`TrayService.cs:110`。Heatmap/DonutChart 均未订阅（2026-10-16 grep 证实）。
- 复现：切语言 → 悬停热力图/环形图（期间无数据变更）→ tooltip 仍是旧语言口径（zh "10万 tok" vs en "100K tok"；"无数据" vs 英文样板），直到下一次 Cells/Segments 数据变更才重建。
- 范围外（有意不做）：TrendChart 图内 tooltip/轴刻度恒 ASCII 是 I-032 锁定决策，不需要重译；TrendChart 空态是 XAML `{Binding NoData, Source={x:Static loc:Translations.Instance}}` 动态绑定，随 PropertyChanged 自动更新。

## Acceptance Criteria

- [ ] 切换 UI 语言后（无数据变更），Heatmap tooltip（数字单位 + "无数据"）与 DonutChart tooltip（数字单位）、Donut 空态副题随新语言更新
- [ ] 走仓内重译约定：订阅 `PropertyChanged(nameof(CurrentLanguage))` → Rebuild；订阅/退订对称（防重复），Rebuild 幂等不破坏既有用例
- [ ] 回归锁：headless 双语言切换（zh→en）断言 tooltip Content 随语言变化，且不依赖 Cells/Segments 数据变更触发
- [ ] 全量 `dotnet test Tokenmetry.sln` 全绿（B-006 既有像素/属性用例不回归）

## Subtasks

- [ ] Heatmap.axaml.cs：挂 `CurrentLanguage` 订阅（挂树/脱树对称，沿用现行 `CellsProperty` 订阅模式）→ Rebuild
- [ ] DonutChart.axaml.cs：同上（含空态副题重涂）
- [ ] 复查两控件其余 build-time Translations 文案是否被同一 Rebuild 覆盖
- [ ] headless 回归锁（双语言各一圈）+ 全量测试

## Dependencies

- none（寄生在 B-006 引入的 tooltip 面）

## Test Cases

### TC-001: zh→en 后 Heatmap tooltip 随语言重译

Steps:
1. headless 建 Heatmap（测试文化固定 zh-Hans）→ 断言有数据格 tooltip 含 "10万 tok"（例值 100,000）、补零格含"无数据"；
2. `Translations.Instance.SetLanguage(English)` → `Dispatcher.UIThread.RunJobs()`（不触碰 Cells）→ 重断言同一 tooltip 的 Content。

Expected: Content 变为英文口径（K/M 单位 + 英文"无数据"样板），无需数据变更触发。

### TC-002: DonutChart 同步重译

Steps:
1. 分段数据 fixture 断言 tooltip 文案；
2. 切语言 → RunJobs → 重断言（含空态副题场景若可构造）。

Expected: 同 TC-001。

### TC-003: 全量回归

Steps: `dotnet test Tokenmetry.sln`。

Expected: 全绿。

## Development Log

### 2026-10-16

- 建卡（B-006 收口 code review 发现：Heatmap/DonutChart 无 `CurrentLanguage` 重译订阅，切语言后 Avalonia 弹层 tooltip 陈旧至下次数据重建；"无数据"固化是旧账，本卡一并纳入）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Controls/Heatmap.axaml.cs`、`src/Tokenmetry.App/Controls/DonutChart.axaml.cs`；测试 `tests/Tokenmetry.App.Headless.Tests/Controls/HeatmapTests.cs`、`DonutChartTests.cs`
- How to run/verify: 真机体感 = 切语言后悬停热力图/环形图，tooltip 文案（单位/无数据样板）应立即换语言；自动验证 = TC-001~TC-003
- Results: 待执行（建卡时未做）
