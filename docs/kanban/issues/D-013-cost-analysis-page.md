---
id: D-013
title: 成本分析页（计费口径透明）
type: feature
priority: P2
size: S
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

成本口径透明页：KPI（估算总成本 / 输入成本占比 / 输出成本占比 / 待确认价格模型数）+ 口径说明表（实际计费、估算成本、未知成本三种状态如何展示）。

## Background

- 设计基准：原型 `#page-cost`。
- 「待确认价格模型数」须与定价引擎（I-003/I-024）的 UnpricedModels 实时一致；输入/输出占比不含缓存折扣差异（缓存输入单独计价，口径文案说明）。
- 口径说明表是产品立场的用户可见面：实际计费优先、估算显示价格版本与计算时间、未知不折零。

## Acceptance Criteria

- [x] KPI 接 I-028/I-029，待确认价格数与定价引擎一致（`PricingEngine.GetUnpricedModels()`，全局口径已在页面脚注说明）
- [x] 输入/输出成本占比计算正确（缓存单列说明）——**AC 偏差如实标注**：契约（I-028/I-029 模型与 `UsageRecord.CostUsd`）只有混合成本，无按方向拆分；落地为 **Token 构成占比 + 显式标注**（「按 Token 构成展示 · 成本按方向拆价口径待扩展」），分母含缓存相关（缓存单列说明成立），非按方向计价拆分。决策过程见 Dev Log 2026-10-13。
- [x] 口径表三行内容与产品立场一致（可对账、缺价不折零、估算注明版本）——「估算注明版本」落地为口径表注明价格来源链与采集时点口径；逐笔「价格版本 + 计算时间」未在 facts 层记录，页面如实不展示该二项（差距登记 Dev Log 遗留风险）
- [x] 明暗两套目检通过（docs/design/cost-{light,dark}.png）

## Subtasks

- [x] CostViewModel + KPI
- [x] 口径说明表内容定稿（与 docs/reconciliation 口径对齐：docs/reconciliation/2026-10-11-reconciliation.md §1.2）

## Dependencies

- D-003, I-028, I-029

## Test Cases

### TC-001: 待确认价格数一致

Steps:
1. fixture 含 3 个缺价模型；对比页面 KPI 与定价引擎返回。

Expected:
- 均为 3；估算总成本不含这三者成本。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 实现完成（`CostViewModel` + `CostAnalysisView`，外壳路由 `PageId.Cost` 真实化；`MainWindowViewModel` 构造注入 `PricingEngine` 单例——与 PiAgentAdapter 同一取法，页面读引擎实时快照，未改定价引擎）。
- **输入/输出成本占比决策点（定案：方案 b）**：先读契约——`UsageKpiResult` / `DailyUsagePoint` / `DimensionUsageRow` 均只有混合 `AggregatedCost`，`UsageRecord.CostUsd` 也是单一混合值；按方向拆成本需扩展定价/查询契约按记录重算方向价（方案 a），会产出**未被与 ccusage 对账覆盖的新口径数字**并波及对账基线，重且违背"任何成本数字必须能与 ccusage 核对"立场；方案 c（Token 占比 × 混合成本估算）是"估算的估算"，否决。落地为**方案 b**：KPI 显示 ΣInput/ΣTotal、ΣOutput/ΣTotal（分母含缓存相关，与原型 input% + output% ≈ 86%、缓存占余量的构成一致），KPI 脚注**显式标注**「按 Token 构成展示 · 成本按方向拆价口径待扩展（缓存单列）」——无标签的隐式近似被排除，AC 字面偏差已在 AC 处如实登记。
- **待确认价格口径（TC-001）**：`GetUnpricedModels()` 为实例方法（同步、无参、返回 `IReadOnlyList<UnpricedModelInfo>`），测试直接构造真实 `new PricingEngine()` 并以 `PriceTokens` 累计 3 个缺价模型，页面 KPI 与引擎断言一致（含引擎实时联动：再加 1 个 → 重查后「4 个」）。**全局口径**：清单按引擎计价过程累计、不随时间范围 / Agent 筛选变化，页面脚注注明「全局口径，不随筛选变化」，headless 断言筛选切换后计数不变；估算总成本不含缺价模型成本（stub KPI 按 I-028 契约只回传已知成本，`AggregatedCost.HasUnknownCost` 标记未知）。已知语义：清单为**进程生命周期内累计**（应用重启后随计价重新累积），与"全局"口径一并可见。
- **口径说明表三行依据**：内容与 docs/reconciliation/2026-10-11-reconciliation.md §1.2 的 ccusage 20.0.29 探针实测口径对齐（非凭记忆）：① 实际计费优先 = 日志 costUSD 直接采用（引擎 LogCost 路径，对账两侧一致）；② 估算成本 = 用户覆盖 → models.dev 远程 → 内置快照三级回退 + 缓存 5m/1h 单独计价（fixture 对账逐分钱一致）；③ 未知成本不折零（I-024/I-025 纪律 + AggregatedCost 契约）。静态文案块，无假数据。
- 测试：`TimeCostPagesTests` 16 用例中 8 个覆盖本卡（含真实服务冒烟与截图两项共用）（KPI 四卡绑定与四态、占比算式与无基数 "—"、TC-001 引擎一致性与实时联动、全局口径可见性与筛选不变性、口径表内容、筛选联动、序号防护、Dispose、真实服务冒烟、明暗截图）。全量 1003 通过 / 22 跳过 / 0 失败、0 警告（基线 987 + 新增 16，无回归）。
- 遗留风险：① 逐笔「价格版本 + 计算时间」未落 facts（I-002 无该列），页面无法展示，口径表已按实际能力表述——若需落地面需新迁移加列；② 方向拆价（真 AC 字面）待契约扩展后回补，届时本页两张占比卡换算式即可（脚注文案已预留「待扩展」）；③ 本机真实数据成本为显式 $0.00（ZCode 自定义 provider 行，(0,false) 已知为零态），四态中「未知 / +下限」两态由 stub 用例覆盖。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/ViewModels/CostViewModel.cs`, `src/TokenPulse.App/Views/CostAnalysisView.axaml(.cs)`, `src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（路由 + 引擎注入）, `src/TokenPulse.App/Views/MainWindow.axaml`（DataTemplate）, `tests/TokenPulse.App.Headless.Tests/Shell/TimeCostPagesTests.cs`
- How to run/verify: `dotnet test tests/TokenPulse.App.Headless.Tests --filter FullyQualifiedName~TimeCostPagesTests`（TC-001 在 `Unpriced_model_count_matches_pricing_engine_tc001`）；明暗目检 `docs/design/cost-light.png` / `cost-dark.png`（重生成：`TOKENPULSE_TIMECOST_PREVIEW_DIR=docs/design dotnet test --filter Time_and_cost_pages_render_light_and_dark_screenshots_for_archive`）
- Results: 2026-10-13 全量 `dotnet test TokenPulse.sln` 1003 通过 / 22 跳过 / 0 失败、0 警告；TC-001 通过（fixture 3 个缺价模型 → 页面 KPI「3 个」与引擎一致，估算总成本 $2.25+ 不含三者成本）；输入/输出占比为 Token 构成口径并显式标注（AC 偏差见上）；明暗截图已留档并目检（KPI 四卡、未知说明行、口径表三行渲染正常）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，无 P0/P1。unknown 占比口径（完备不变式有测试锁定、占比算式手算一致）、热力图数据链（强度分母=窗口内有数据日最大值、补零淡格对齐 D-002 定案、无界实际日语义）、方案 b 诚实性（脚注卡内可见非藏 tooltip、分母算式核实、AC 偏差登记一致）、PricingEngine 接线（真引擎 TC-001 + 3→4 实时联动、单例同法核实、全局口径筛选不变性有测）、静态表逐句对照原型与对账文档（无凭记忆表述）、超范围零改动全部核实。P2 三条由 PM 处理：① 看板状态迁移（本次收尾操作）；② D-013 卡用例计数 9→8 更正；③ 成本页脚注补「（自启动累计）」语义（174/174 headless 复验通过）。PM 验收：全量 1003 通过 / 22 跳过 / 0 失败维持。
