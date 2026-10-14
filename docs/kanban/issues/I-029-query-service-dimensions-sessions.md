---
id: I-029
title: UI 聚合查询服务 B：维度聚合 + 会话明细 + 小时分布
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

提供 Agent / 模型 / 项目三个维度的聚合排行查询，会话明细列表查询（搜索、组合筛选、排序、分页），以及小时级分布查询。缺精确事件时间的来源按"未知"返回并在结果中可见，不伪造小时分布。

## Background

- 原型七个内容页里有四页（会话明细 / Agent / 模型 / 项目）+ 时间分析页全部吃这一层。
- 项目维度需要路径归一化为项目名（原型中 Project-A/AI-Project 等）；归一化歧义守卫对齐 I-025 的思路：歧义时宁可显示原始路径也不猜。
- session_facts 表结构 I-002 已就绪；会话去重口径（Agent + Session ID）与采集层既有语义保持一致。
- 小时级分布：部分源只有会话级粗粒度时间戳，对这类记录聚合到小时是伪造精度——按产品立场显式排除并标注。

## Acceptance Criteria

- [x] Agent/模型/项目维度聚合：会话数、输入/输出/总 token、成本、占比；项目名经路径归一化（`GetAgent/Model/ProjectBreakdownAsync`；键 = facts 聚合 ∪ 会话计数并集；归一化 + 歧义守卫见 Development Log）
- [x] 会话列表：标题/项目/模型/Agent 搜索 + 组合筛选 + 排序（token/成本/最近活动）+ 分页（`GetSessionsAsync`；facts 无标题字段，标题搜索落在 SessionId，契约已文档化）
- [x] 小时级分布：仅纳入有时间精度的记录；无精度来源返回 unknown 计数，调用方可展示（`GetHourlyDistributionAsync`：UnknownSessionCount / UnknownTotalTokens 可见，0..23 全量桶）
- [x] 未知成本口径与 I-028 一致（null ≠ 0）（同一套 `AggregatedCost` 类型与契约，零新口径）
- [x] 单元测试覆盖各维度与"未知"口径（三维度 SQL 对照 + 歧义守卫 + 纪元/日锚定 unknown + 缺价 null 全链路）

## Subtasks

- [x] 三维度聚合查询（token/成本来自 usage_facts、会话数来自 session_facts，按日 full 粒度优先）
- [x] 会话列表查询（搜索/筛选/排序/分页）
- [x] 小时分布查询（含时间精度判定，判定方式见 Development Log）
- [x] 单元测试（与 I-028 同套件，共 22 例）

## Dependencies

- none（复用 I-002 存储）

## Test Cases

### TC-001: 维度聚合与未知口径

Steps:
1. 构造 fixture：多 Agent/模型/项目 + 一个无分钟级时间戳的源 + 一个缺价模型。
2. 依次调用三类查询。

Expected:
- 聚合数值与手写 SQL 一致；unknown 时间源不出现在小时分布但计数可见；缺价成本为 null。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13（实现完成）

与 I-028 同一服务落地（`UsageQueryService`，接口归属决策见 I-028 Development Log）：`GetAgentBreakdownAsync` / `GetModelBreakdownAsync` / `GetProjectBreakdownAsync` / `GetSessionsAsync` / `GetHourlyDistributionAsync`。

**时间精度判定方式（先读 schema/写入路径后定案）**：读取结论——facts 不存精度元数据：`usage_facts` 只有 day 文本，`session_facts` 存完整 DateTimeOffset（first/last_seen_at），且各适配器均写逐记录时间戳，粗粒度只出现在**文档化兜底**（`StrictRfc3339Timestamp.Epoch` 纪元、文件 mtime、now()）。因此判定在查询侧按"已存时间戳的形态"做（`UsageQueryService.HasMinuteLevelTimestamp`）：① 1970-01-01T00:00:00Z（纪元兜底标记，`ToUnixTimeMilliseconds()==0`）→ 无事件时间，unknown；② 查询时区下时间OfDay恰为 00:00:00.000（日锚定值——日粒度来源与真实午夜事件不可辨）→ 按"宁排除不伪造"计 unknown；③ 其余视为携带分钟级精度入分布。**已知边界**：mtime / now() 兜底在 facts 层与真实事件时间不可辨，查询侧无法排除（登记为限制；若需权威判定须采集层写入精度元数据 + 新迁移）。真实午夜开始的会话被保守排除，但计入 `UnknownSessionCount` / `UnknownTotalTokens`，可见而非丢失。小时分布返回 0..23 全量桶（空桶补零），锚点 = first_seen_at 的本地小时。

**归一化歧义守卫实现（对齐 I-025）**：项目维度按**原始路径**分组（永不合并不同项目），展示名候选 = facts 的 `project_display_name`（存储读取序确定）优先、session 侧兜底；同一结果集内 ≥2 个不同原始路径归一化出相同展示名时，相关行全部退回显示**原始路径**——宁可显示原始也不猜（`ResolveProjectDisplayNames`，守卫作用于当次查询结果集）。Agent / 模型维度 DisplayName 为 null（Key 即展示名）。

**会话去重口径**：会话侧全部查询按 **(Agent, SessionId)** 分组计数——`session_facts` 主键是 session_id（I-002 语义，采集层 `ToSessionFacts` 按 SessionId 分组），行内 agent 单值，故 (Agent, SessionId) 分组与主键等价；未来主键若扩为二元组无需改查询。会话归日 = first_seen_at 的本地自然日（与 KPI 会话数、会话列表时间过滤同一口径）。维度行键 = usage_facts 聚合键 ∪ session_facts 计数键（会话独有键 token 为 0 成行；CLI 源 facts 独有键会话数为 0——两个方向都不静默丢键）。

**会话列表契约**：搜索词大小写不敏感包含匹配 SessionId / Agent / 项目路径 / 展示名 / 主模型——session_facts **无标题字段**（I-002），"标题"搜索落在 SessionId（会话标识），XML doc 已注明，D-008 引入标题数据源后在同参数扩展；组合筛选 = 全局（时间范围 + Agent）+ Project（原始路径精确）+ Model（dominant_model 精确）；排序 LastSeenAt（默认，最近活动）/ TotalTokens / Cost（未知成本视为最小值，降序时排在已知成本之后，不折 0 参与比较），平局键恒为 SessionId 保证分页稳定；分页 Page / PageSize（null = 不分页）+ 过滤后 TotalCount。

**未知成本口径**：与 I-028 完全同一套 `AggregatedCost` 类型与契约（null ≠ 0，未知不折 0）；会话行 `CostUsd` 为 null 即全未知（I-002 行语义），便捷标记 `SessionListRow.CostIsUnknown`。

**TC-001**：fixture 多 Agent/模型/项目 + 一个缺价模型 + 两个无分钟级时间戳的源（纪元 + 日锚定 00:00 两形态）+ 一个 CLI 分解日；三维度聚合与手写 SQL（full 优先 + 对应分解粒度 NOT EXISTS 子查询 + 会话计数 SQL）对照一致；unknown 源不出现在小时分布但计数与 token 体量可见；缺价成本 null 在日点 / KPI / 维度 / 会话 / 小时桶全链路保持。测试同 I-028 套件（`UsageQueryServiceTests`，时区固定 UTC）。

### 2026-10-13（独立审查修复轮）

- **P1 修复——小时分布"日锚定"判定的时区错配**：原规则②只查查询时区 `TimeOfDay == 0`，而日期级来源（Goose 解析 `"{date}T00:00:00Z"`）锚定的是 **UTC 午夜**——非 UTC 机器（本机 UTC+8 实测复现）上被伪造进本地 08 点桶、unknown 不可见；测试抓不到是因为 fixture 时区钉死 UTC。修复：判定改为两条与来源形态对应的规则——① `ToUnixTimeMilliseconds() % 86_400_000 == 0`（UTC 午夜锚定，**与查询时区无关**，同时覆盖纪元兜底）→ 无精度；② 查询时区下 00:00:00.000（本地日锚定）→ 无精度。代价：真实 UTC / 本地午夜事件被保守排除并计入 unknown（"宁排除不伪造"口径内），已登记进 `HourlyDistributionModels` 已知边界段。新增回归测试：Asia/Shanghai 时区下 UTC 午夜锚定会话落入 `UnknownSessionCount`、08 点桶保持空。
- **P2-1 修复——三维度 SQL 对照补齐**：首轮实现只有 Agent 维度有手写 SQL 对照（Model/Project 是硬编码期望值，卡面措辞超前于实现——按"不虚勾"纪律如实登记）。本轮抽取 `QueryDimensionExpectationAsync` 共用 helper（facts 聚合 full 优先 + 对应分解粒度 NOT EXISTS 子查询 ∪ 会话计数，键并集、排序与契约一致），三个维度测试全部改为 SQL 逐键逐列对照，首轮审查方的手算核对结论（硬编码期望值正确）已被 SQL 对照取代固化。
- **P2-2 修复——分页契约**：`PageSize <= 0` 由静默当作"不分页"改为抛 `ArgumentException`（XML doc 契约写明，null 才是不分页）；分页 skip 算术 long 化（`(long)(page-1) * pageSize`），越界页返回空页而非 int 溢出。补边界测试（0 / 负数抛错、Page=2+PageSize=int.MaxValue 空页、Page=1+int.MaxValue 全量）。
- **P2-5 修复——歧义守卫碰撞检测改 ordinal-ignore-case**：`/work/Tools` 与 `/work/tools` 在大小写不敏感的本地文件系统上是同一路径，仅 ordinal 逐位比较漏判歧义。修复后展示名碰撞按 ignore-case 检测，语义保持"碰撞即全部退回原始路径，绝不合并"。与 I-025 的范围对齐说明：I-025 模型名守卫是 ordinal（归一化本身已折叠大小写，碰撞发生在同一归一化串上），本守卫的末段不做归一化折叠，故检测面更宽。fixture 增补 /work/Tools 行 + 断言三行全退原始路径。
- **P3-7**：纪元判定提为具名常量 `MillisecondsPerDay` 并注释锚定写入方（Kimi / OpenClaw / Grok 返回 `StrictRfc3339Timestamp.Epoch`，Gemini / Amp / Droid 严格 RFC3339 失败回退，Goose 日期级 `T00:00:00Z`）；`MetricChange` XML doc 补"current 为 null（当前成本全未知）→ Neutral"组合。
- 未涉及本卡的审查项（P2-3 日序列上限、P2-4 offset 前提、P2-6 刷新原子化）记录在 I-028 审查修复轮。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.Core/Services/UsageQueryService.cs`、`src/TokenPulse.Core/Abstractions/IUsageQueryService.cs`、`src/TokenPulse.Core/Models/DimensionUsageRow.cs`、`src/TokenPulse.Core/Models/SessionQueryModels.cs`、`src/TokenPulse.Core/Models/HourlyDistributionModels.cs`、`src/TokenPulse.Core/Models/AggregatedCost.cs`、`src/TokenPulse.App/Services/BackgroundRefreshService.cs`、`tests/TokenPulse.Infrastructure.Tests/Query/UsageQueryServiceTests.cs`（DI 接线同 I-028 的 `AppServices.cs`）
- How to run/verify: `dotnet build TokenPulse.sln`（0 警告）+ `dotnet test TokenPulse.sln`；TC-001 可独立复现 = 测试 `Agent_breakdown_matches_sql_with_session_counts_and_shares` / `Model_breakdown_matches_sql_and_keeps_session_only_models` / `Project_breakdown_matches_sql_and_guard_falls_back_to_raw_path`（三维度手写 SQL 对照）/ `Hourly_distribution_excludes_imprecise_timestamps_and_counts_unknown` + `Hourly_distribution_utc_midnight_anchor_is_unknown_on_non_utc_timezone`（P1 回归，Asia/Shanghai）
- Results: 全量 860 通过 / 22 跳过 / 0 失败（基线 835 通过 / 22 跳过；首轮 857 + 审查轮新增 3 例，零回归）；构建 0 警告 0 错误
