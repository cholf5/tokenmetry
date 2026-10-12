---
id: I-028
title: UI 聚合查询服务 A：日趋势 + KPI + 区间对比
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

在 facts 存储之上提供面向 UI 的聚合查询服务：按日 token（输入/输出/缓存）与成本序列、KPI 汇总（总 token / 估算成本 / 会话数 / 缓存读取占比）及环比区间对比，支撑总览页与成本分析页。查询参数统一含时间范围与 Agent 过滤。

## Background

- 原型里每个数字都对应一个聚合口径；UI 层与解析层之间需要显式查询服务，ViewModel 不直接写 SQL（30k 行解析层已收口，本卡是 UI 与既有数据的接缝）。
- D-002 遗留决策点在本卡定案：日历窗口语义（空日补零 / 断线 / 仅画实际日）——本机 facts 实际 9 个数据日横跨 8 个月，稀疏日期的长直线有误导性。倾向：范围内空日补零并让图层可辨识；最终以实现结论为准，记录于 Development Log。
- 产品口径：未知成本（缺价模型）返回 null 且显式区分，绝不折算为 0 混入合计（对齐 I-024/I-025 收口时确立的 UnpricedModels 纪律）。

## Acceptance Criteria

- [x] 日粒度序列查询：任意日期范围返回 in/out/cacheRead/cacheWrite/total/cost 按日聚合；日历窗口语义实现并记录结论（`GetDailySeriesAsync`；补零 + `DailyUsagePoint.HasData` 标记，定案见 Development Log 2026-10-13）
- [x] KPI 汇总查询：区间汇总 + 上一周期对比（环比方向与百分比）（`GetKpiAsync` + `KpiPeriodComparison`，等长回看窗口）
- [x] 查询参数支持 Agent 过滤（为全局筛选联动准备）（`UsageQueryFilter.Agent` 贯通全部 7 个查询方法）
- [x] 未知成本口径：接口层显式区分 null 与 0，文档化返回契约（`AggregatedCost`：KnownCostUsd + HasUnknownCost，四种组合语义 XML doc 化）
- [x] 单元测试覆盖聚合正确性（fixture 数据与手写 SQL 对照）（`UsageQueryServiceTests` 22 例，日序列/KPI 与手写 SQL 逐项对照）

## Subtasks

- [x] 查询服务接口定义（Core/Infrastructure 归属斟酌）（定案：接口 `Core/Abstractions`、实现 `Core/Services`，理由见 Development Log）
- [x] 日序列实现 + 日历窗口语义定案（补零 + HasData 图层标记）
- [x] KPI 汇总 + 环比区间对比（等长回看窗口）
- [x] Agent 过滤参数贯通
- [x] 单元测试（22 例，含 TC-001 SQL 对照）

## Dependencies

- none（复用 I-002 存储、I-003 定价引擎）

## Test Cases

### TC-001: 聚合正确性

Steps:
1. 构造 fixture facts（已知逐日逐模型数据 + 一个缺价模型）。
2. 调用日序列与 KPI 查询，与手写 SQL 结果对照。

Expected:
- 逐日逐项一致；缺价模型成本为 null，KPI 中计入"未知"而非 0。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13（实现完成）

**接口归属决策（卡内决策点定案）**：接口 `IUsageQueryService` 放 `CCUsage.Core/Abstractions`（与 `IUsageStore` 同位），实现 `UsageQueryService` 放 `CCUsage.Core/Services`（与 `CacheService` 同位），**不是** Infrastructure。理由：查询服务只消费 `IUsageStore`——facts 存储已是唯一 SQL 数据面（I-002），复用其读取 / cost_usd 文本解析 / 迁移口径，不引入第二套 SQL 语义；桌面本地库全量读取后内存聚合成本可忽略（`CacheService.GetDailyRowsAsync` 同模式）；纯 Core 落位让契约可被 App 与测试在无 Infrastructure 依赖下绑定。契约模型（`AggregatedCost` / `DailyUsagePoint` / `UsageKpiModels` / `UsageQueryFilter`）放 `Core/Models`。DI 在 `AppServices` 注册单例，时区 = 本机（与采集侧 `UsageFactsMapper` 分桶默认一致）。

**日历窗口语义定案（D-002 遗留决策点）**：三选一取**范围内空日补零 + `HasData` 标记**。理由：①"仅画实际日"会把 9 个数据日横跨 8 个月的稀疏序列画成连续花费（D-002 审查记录在案的误导）；②"断线"需要图层数据结构表达序列中断，单 scatter/line 图实现成本高，且热力图 / tooltip 消费方无论如何都需要完整日历轴；③补零让 x 轴回归真实日历、与 SQL 聚合对账最直接，配合 `DailyUsagePoint.HasData=false`，UI 图层可把补零点画成淡化 / 空心（"补零且图层可辨识"），未来若要改断线只需在图层处理 `HasData`，查询契约不变。双端无界（"全部时间"）或单端有界时无从补零，只返回实际有数据的日期。

**未知成本契约（纪律红线）**：新增 `AggregatedCost`（`KnownCostUsd: decimal?` + `HasUnknownCost: bool`）贯穿全部查询（日点 / KPI / 维度 / 会话 / 小时桶）。四种组合语义在 XML doc 契约化：`(null,false)`=无成本数据（补零空日）；`(null,true)`=全部未知；`(0,false)`=已知为零（如 ZCode 自定义 provider 行的显式 0）；`(x,true)`=部分未知，x 是已知下限。未知成本绝不折 0 混入合计，对齐 I-024/I-025 的 UnpricedModels 纪律（Missing 定价 CostUsd 为 null 不是 0）。边界说明：I-002 行语义下 cost_usd 仅在该行全未知时为 null，混合行内隐藏的未知残留 facts 层不可辨——`HasUnknownCost` 是可证据化的未知下限，权威逐记录未知清单仍是 `PricingEngine.GetUnpricedModels()`。

**环比窗口定案**：上一周期 = 等长回看窗口 `[FromDay − N, FromDay − 1]`，N = 当前窗口自然日天数（含端点）；Agent 过滤沿用当前窗口。仅双端有界时计算，否则 `PreviousPeriod = null`。百分比口径 `(current − previous) / |previous| × 100`（2 位小数）：previous 无数据 → Neutral/null；previous = 0 且 current ≠ 0 → 方向保留、百分比 null（除零不可定义，不伪造）；current = previous → Neutral/0。

**混源守则**：同日混源以 full 粒度行为准（CLI 分解行不重复计数），无 full 行的分解日退回 day 粒度行——逐日解析与 `UsageFactsMapper.ToDailySummaries` 口径一致；分解日 + Agent 过滤时 day 粒度行（agent 为空）被过滤，日点保持 HasData=true 但合计为 0（不折算 agent 粒度行，避免与维度聚合重复计数）。

**顺带（同组提交）**：`BackgroundRefreshService` 增加 `TriggerRefreshAsync()`（与定时器共用互斥与静默异常语义，不改定时器计划），App 启动路径 fire-and-forget 触发一次首刷——首启顶栏不再停留"尚未刷新"（定时器首跑要等完整间隔，默认 15 分钟）。

**测试**：`tests/CCUsage.Infrastructure.Tests/Query/UsageQueryServiceTests.cs` 22 例：fixture 3 Agent / 4 模型 / 6 项目 + 缺价模型 + CLI 分解日 + 纪元与日锚定时间戳会话，时区固定 UTC；TC-001 日序列与 KPI 与手写 SQL（full 优先 NOT EXISTS 子查询）逐项对照；补零、Agent 过滤、环比方向 / 百分比 / 除零、null ≠ 0 ≠ Empty 三态、空库不伪造占比、非法范围抛错均有用例。

### 2026-10-13（独立审查修复轮）

独立审查确认聚合正确性与未知成本契约逐位成立、SQL 对照为真对照；以下为修复项（P1 及 I-029 侧各项见 I-029 卡同日"审查修复轮"）：

- **P2-3 修复——日序列补零范围上限**：双端有界的日序列此前无物化上限（极端 filter 可物化数百万点）。新增 `UsageQueryFilter.MaxDailySeriesDays = 3653`（10 年含闰日余量，含端点），超出抛 `ArgumentOutOfRangeException`；接口与常量 XML doc 契约化；补"恰好上限可用 / 超一天抛错"边界测试。KPI / 维度 / 会话 / 小时分布为聚合口径，不物化逐日点，不受限。
- **P2-4 修复——隐式时区假设文档化**：`UsageQueryService` 类 XML doc 增补"时区前提"段——day 文本由采集侧写入（本机时区分桶默认），本服务默认同口径解释；存储 `FormatTimestamp` 保留原始 offset，测试中 substr(first_seen_at,1,10) 的 UTC 日口径仅在 offset 恒为 +00:00 时与本地日等价，该等价性由适配器链解析层 AdjustToUniversal 保证。
- **P2-6 修复——BackgroundRefreshService 并发占位原子化**：`_isRunning` 的 check-then-set 改为 `Interlocked.CompareExchange` / `Exchange`（bool → int），注释说明定时器回调与启动首刷（TriggerRefreshAsync）两个并发入口在非原子 check-and-set 下可双跑的既有缺陷。
- **P3-7**：纪元判定提为具名常量 `MillisecondsPerDay`（在 I-029 侧的小时分布判定处，含写入方锚定注释）；`MetricChange` XML doc 补"current 为 null（当前成本全未知）→ Neutral（不臆造方向）"组合。
- 顺带修正首轮测试的两个断言口径（审查手算核对后固化）：会话成本排序中 s7（纪元会话）成本 0.10 为**已知**成本；项目维度 TokenShare 分母是项目维度行合计（不含 agent/model 分解日的贡献）。
- 验证：`dotnet build CCUsage.sln` 0 警告；`dotnet test CCUsage.sln` 全量 860 通过 / 22 跳过 / 0 失败（首轮 857 + 审查轮新增 3 例，零回归）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Core/Abstractions/IUsageQueryService.cs`、`src/CCUsage.Core/Services/UsageQueryService.cs`、`src/CCUsage.Core/Models/AggregatedCost.cs`、`src/CCUsage.Core/Models/DailyUsagePoint.cs`、`src/CCUsage.Core/Models/UsageKpiModels.cs`、`src/CCUsage.Core/Models/UsageQueryFilter.cs`、`src/CCUsage.App/DependencyInjection/AppServices.cs`（DI）、`src/CCUsage.App/Services/BackgroundRefreshService.cs` + `src/CCUsage.App/App.axaml.cs`（启动首刷）、`tests/CCUsage.Infrastructure.Tests/Query/UsageQueryServiceTests.cs`
- How to run/verify: `dotnet build CCUsage.sln`（0 警告）+ `dotnet test CCUsage.sln`；TC-001 可独立复现 = 测试 `Daily_series_matches_handwritten_sql_with_full_grain_precedence` 与 `Kpi_matches_handwritten_sql_and_exposes_unknown_cost`（真实 SQLite fixture + 手写 SQL 同库对照）
- Results: 全量 860 通过 / 22 跳过 / 0 失败（基线 835 通过 / 22 跳过；首轮 857 + 审查轮新增 3 例，零回归）；构建 0 警告 0 错误

**PM 验收留痕（2026-10-13）**：修复轮后 PM 实跑 build 0 警告 + 全量测试复核。期间两次全量运行出现 1 例 Infrastructure.Tests 间歇失败（单跑该项目与随后 13 次连跑均无法复现，失败时恰逢与构建/测试并行的高负载窗口，疑似 CCUsageCliRunner 进程类用例在 CPU 争抢下超时——该超时用例先于本卡存在）。按观察项记录、不阻塞本卡；后续复现时以 console logger 抓用例名再定位。
