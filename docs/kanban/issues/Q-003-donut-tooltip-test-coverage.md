---
id: Q-003
title: 图表 tooltip 测试对称补齐（DonutChart chart-tip 类与紧凑文案零覆盖）
type: test
priority: P3
size: S
status: done
created: 2026-10-16
updated: 2026-10-17
blocked: none
---

## Goal

让 B-006 的 tooltip 回归锁覆盖全部三个图表消费方：给 `DonutChartTests` 补 tooltip 断言（chart-tip 类挂载 + 紧凑单位文案），使 `ChartTooltips.Set` 或 `SharedStyles` 的 `ToolTip.chart-tip` 样式规则回归时测试会红（现状不红）。

## Background

- 源自 B-006（d268368）收口后的 code review（2026-10-16）。
- 现状覆盖对比：`HeatmapTests` 有三类断言（GetTip 为显式 ToolTip 且挂 chart-tip 类 / `SetIsOpen` 后 Background、Foreground 与 App 令牌画刷 `Assert.Same` / Content 含紧凑单位与"无数据"）；`TrendChartTests` 有属性级（明暗两变体）+ 像素级（气泡底色出现/消失）+ 文案紧凑断言；**`DonutChartTests` 无任何 ToolTip 相关断言**（rg 证实），而 DonutChart.axaml.cs ~L246 经 `ChartTooltips.Set(path, ...)` 挂 tooltip 的路径零覆盖。
- 风险同源但覆盖不对称：Donut 是共享 helper（`ChartTooltips.Set`）与共享样式（`ToolTip.chart-tip`）之外的第三消费点——helper 被"优化"改坏、类名改名、另一半 SharedStyles 规则误删时，热力图/趋势图会红而环形图静默绿。
- 断言语言口径沿用仓内先例：测试文化固定 zh-Hans（TestCulture），HeatmapTests 已是语言相关断言（"10万 tok"），Donut 断言同等可接受。

## Acceptance Criteria

- [x] `DonutChartTests` 至少一条用例断言：分段 Path 的 GetTip 为 ToolTip 实例、Classes 含 "chart-tip"，Content 含紧凑 token 值 + "tok" 后缀（zh-Hans 固定文化）
- [x] 反向验证一次：临时破坏 chart-tip 类（去掉 `Classes.Add` 或改类名）→ TC 应红 → 恢复（结果记进卡，证明锁有效）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] 复用 DonutChartTests 现有分段数据 fixture，补 tooltip 断言（并入现有分段渲染用例或单独新增一条均可）
- [x] 反向验证（临时破坏 → 红 → 恢复）
- [x] 全量测试

## Dependencies

- none

## Test Cases

### TC-001: Donut 段 tooltip 挂 chart-tip 类且文案紧凑

Steps:
1. 分段数据 fixture 渲染；
2. 取分段 Path（同 CellBorders 的取法）→ `ToolTip.GetTip(path)` → 断言 ToolTip 实例 + Classes 含 "chart-tip"；
3. 断言 Content：`{段名}: {紧凑值} tok`（zh-Hans；与 HeatmapTests 同口径）。

Expected: 三段断言全过（对比 HeatmapTests 断言形态）。

### TC-002: 反向验证锁有效

Steps:
1. 临时去掉 `ChartTooltips.Set` 内的 `Classes.Add(...)`（或改名类）→ 单跑 TC-001；
2. 恢复。

Expected: 第一步红、恢复后绿；结果记录进卡（B-006 TC-002 同诚实口径）。

### TC-003: 全量回归

Steps: `dotnet test Tokenmetry.sln`。

Expected: 全绿。

## Development Log

### 2026-10-16

- 建卡（B-006 收口 code review 发现：DonutChartTests 零 tooltip 断言，共享 helper/样式回归时环形图侧静默绿）。

### 2026-10-16（实施，Q-003）

- 补锁：`DonutChartTests` 新增 1 条用例 `Segment_paths_get_chart_tip_tooltips_with_compact_tokens`（单独成条，
  复用既有 `FiveSegments` 分段 fixture，渲染后取 ArcCanvas 的 5 个分段 Path）：首段（Claude Code 3,600,000）
  断言 `ToolTip.GetTip(path)` 为显式 ToolTip 实例且 Content 含 `Claude Code: 360万 tok`（zh-Hans 固定文化，
  万制紧凑 + "tok" 后缀；并 `DoesNotContain "3,600,000"` 锁住"非千分位全量"口径），全 5 段断言
  `Classes` 含 "chart-tip"——断言形态与 HeatmapTests 同源（GetTip/类挂载/文案三类）。
- 反向验证（TC-002，B-006 TC-002 同诚实口径）：临时注释掉 `ChartTooltips.Set` 内
  `tooltip.Classes.Add(ChartTipClass)`（src/Tokenmetry.App/Controls/ChartTooltips.cs 唯一类挂载点）→
  单跑该用例**红**：`Failed!  - Failed: 1, Passed: 0`，`Assert.All() Failure: 5 out of 5 items did not pass`，
  逐段 `Assert.Contains() Failure: Item not found in collection`（DonutChartTests.cs:226 的 chart-tip 断言）→
  立即恢复源码 → 同用例**回绿**（`Passed!  - Failed: 0, Passed: 1`）；`git status` 证实
  ChartTooltips.cs 无残留改动（不在修改清单中）。锁有效。
- 全量 `dotnet test Tokenmetry.sln` 绿（Passed 1271 / Failed 0，Headless 280→284；其中 B-007 本日
  并入 DonutChartTests 的 2 条重译用例共用本次补的断言面，互为补充）。

### 2026-10-17（收口）

- 独立审查（新 subagent、零共享上下文）通过：断言形态与 HeatmapTests 先例一致、锁面精确（ToolTip
  实例 + chart-tip 类 × 5 段 + 紧凑文案非千分位）；反向验证为审查者独立复现——注释 `Classes.Add`
  → 恰好本用例红（Assert.All 5/5 段缺类）→ 恢复全绿，锁有效性成立；全量 1271 绿；未发现问题。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Tokenmetry.App.Headless.Tests/Controls/DonutChartTests.cs`（预期改动点）；参照 `HeatmapTests.cs` 断言形态；被锁实现 `src/Tokenmetry.App/Controls/ChartTooltips.cs`、`DonutChart.axaml.cs`、`src/Tokenmetry.App/Styles/SharedStyles.axaml`
- How to run/verify: 按名过滤单跑 DonutChartTests；全量 `dotnet test Tokenmetry.sln`
- Results: 完成（2026-10-16）。TC-001：`Segment_paths_get_chart_tip_tooltips_with_compact_tokens` 绿（ToolTip 实例 + chart-tip 类 × 5 段 + `Claude Code: 360万 tok` 紧凑文案，非千分位全量）。TC-002：反向验证红→恢复绿（红 = `Failed: 1`，Assert.All 5/5 段缺失 chart-tip；恢复后 `Passed: 1`；ChartTooltips.cs 无残留改动），详见 Development Log。TC-003：全量 `dotnet test Tokenmetry.sln` = Passed 1271 / Failed 0（Headless 280→284）。
