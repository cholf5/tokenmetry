---
id: I-016
title: Hermes Agent 适配器
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Hermes Agent 本地 SQLite（HERMES_HOME 或 ~/.hermes/state.db），产出 agent=Hermes 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/hermes/。SQLite 直读，复用 I-006 骨架。本机暂无数据。

## Acceptance Criteria

- [x] 数据目录发现（HERMES_HOME 多根 → ~/.hermes 回退）与 SQLite 只读接入
  - ✅ 2026-10-11：`HermesLogLocator` 逐条移植 paths.rs（HERMES_HOME 逗号分隔多根、去空白、跳过空段、
    按序去重 → 各根下 state.db；未设置时 ~/.hermes/state.db）；只读打开走共享 `SqliteReadonly`（WAL 兼容）
- [x] 按蓝本完成行过滤、token 口径映射与模型映射
  - ✅ 固定列查询与蓝本 loader.rs 同构（model 空白行 SQL 排除）；read_u64/read_f64/read_non_negative_f64
    容错逐条移植；秒/毫秒判别（>1e12 按毫秒、否则 ×1000、截断取整、须 >0）；五桶 + cost（actual 优先回退
    estimated、非负）全零行跳过；reasoning 并入 output（I-006 口径）；provider 归一化/前缀推断全分支；
    message id 取蓝本形状 "hermes:&lt;session_id&gt;"
- [x] 复用 I-006/I-009 泛化的 SQLite 扫描骨架
  - ✅ 只读打开与 schema 原语直接复用 `SqliteSource/`（I-009 泛化产物，无新增分叉）；诊断口径沿用
    JsonlScanDiagnostics 与 I-006 状态机；定位器/行映射语义差异见 Dev Log
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 21 项单测（临时 SQLite 库，fixtures 全合成）+ 合成 fixture（state.db）入仓 +
    CLI golden 录制逐位一致 + `CCUSAGE_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip）；
    `IHermesSqliteAdapter` + LocalIngestionDataSource 第八路 concat + App DI 双注册

## Subtasks

- [x] `HermesLogLocator`：configuredHome → HERMES_HOME（逗号分隔多根）→ ~/.hermes（USERPROFILE 优先，
  仓库共享 JsonlHomePath 约定）；File.Exists 过滤 + 按序去重
- [x] `HermesSessionParser`（parser.rs 移植）：SessionRow 读取形态 → 时间戳（TryTimestampFromNumber：
  非有限/非正作废、f64→i64 饱和钳位、MaxUnixMilliseconds 防御）→ stored cost（actual ?? estimated、
  非负、f64→decimal 经最短往返文本）→ 五桶+cost 全零行跳过 → reasoning 并入 output →
  provider 归一化（NormalizeProvider：小写、'-'→'_'、别名收敛；InferProviderFromModel：claude-*·claude/*→
  anthropic、gpt*·chatgpt*·o&lt;digit&gt;→openai、gemini-*·gemini/*→google、其余 hermes）→
  计价候选纯函数（BuildModelCandidates：qualified 仅在 exact-with-fallback 命中时插入、hermes 兜底不限定；
  HasExactNormalizedPrice：'.'/'@'→'-' 键归一化口径）
- [x] `HermesSqliteAdapter`（loader.rs 移植）：逐库只读打开 → 固定列查询 → 行读取（身份列不可读静默跳过
  并计数）→ 按 session id（message id 双射）跨库去重（先扫的库优先）→ 时间稳定排序 → 诊断与状态机
- [x] 定价快照扩充：新增 `xai` 段 grok-4.3（input 1.25 / output 2.5 / cache_read 0.2，取自蓝本
  models-dev-pricing.json bare "grok-4.3" 条目，tiers 分档按基础档计价与引擎口径一致）——服务蓝本
  frontier 模型计价移植测试；gpt-5.5 已在 openai 段命中
- [x] 单元测试 ×21（`HermesSqliteAdapterTests`：五桶+成本直采/回退、全零行、身份/时间无效行、SQL 层空白
  排除、秒/毫秒判别、整数 started_at 列、无 sessions 表降级、跨库 session 去重、排序、HERMES_HOME 多根、
  配置根优先、默认根回退、库缺失、frontier 模型快照计价、候选/归一化纯函数、时间边界）
- [x] 对账：合成 fixture（五桶 + REAL 秒/毫秒双刻度 + provider 推断 + actual cost 直采 + 全零行 + 空白
  model 行）入仓 + CLI golden 录制（fixtures/golden/hermes-fixture-daily-v20.0.29.json）+ fixture 对账
  测试 ×2 + `CCUSAGE_RECONCILE_REAL` 门控真实对账测试（HasHermesData 数据存在性 Skip）
- [x] 管道接线：`IHermesSqliteAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第八路 concat +
  App DI 双注册（具体类 + 接口）

## Dependencies

- I-006（SQLite 骨架）、I-009（SqliteSource 共享原语）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 loader.rs 内联测试 loads_billable_hermes_sessions_from_state_db
与 parser.rs 内联测试）

Steps:
1. 测试内临时目录合成 state.db（五桶齐全 + actual cost、stored cost 0、estimated cost 回退、全零行、
   身份/时间无效行、空白 model 行、REAL 秒含小数/REAL 毫秒/整数存储三种 started_at、无 sessions 表、
   跨库同 session id、HERMES_HOME 多根），运行 `HermesSqliteAdapter.ScanAsync` 断言记录、诊断与降级行为；
   provider 归一化/推断、候选顺序、键归一化、时间边界以纯函数断言蓝本内联测试同名场景。

Expected:
- 五桶映射（cache_write→cacheCreation、cache_read→cacheRead）；actual cost 直采（0.34）、cost 0 走引擎
  （蓝本 recorded_zero_cost_falls_back_to_pricing_calculation）、estimated 回退；reasoning 并入 output
  （300+10=310 型）；全零行计 skipped；身份/时间无效行计 invalid；REAL 秒 ×1000（1_750_000_000.25 →
  1_750_000_000_250 ms）、>1e12 按毫秒、0/负值作废；空白 model 行 SQL 排除不计诊断；无 sessions 表
  0 条 + 降级；跨库同 session id 先扫者保留；HERMES_HOME 逗号多根去重；gpt-5.5 / grok-4.3 按内置快照
  可计价；候选顺序 [provider/model, model]（exact 命中时）/ [model]（fuzzy 不命中时）；
  '.'/'@'→'-' 键归一化；provider 别名与前缀推断全分支。

Result: 通过（Infrastructure 21/21：Loads_billable_hermes_sessions_from_state_db、
Recorded_zero_cost_falls_back_to_pricing_engine、Falls_back_to_estimated_cost_and_skips_all_zero_rows、
Rejects_rows_with_invalid_identity_or_timestamp、Excludes_blank_model_rows_in_sql、
Distinguishes_second_and_millisecond_started_at_scales、Reads_integer_started_at_column、
Database_without_sessions_table_degrades_to_no_data、Deduplicates_cross_database_sessions_keeping_first_home、
Sorts_records_by_timestamp、Parses_comma_separated_hermes_home、Configured_home_takes_priority_over_environment_variable、
Falls_back_to_hermes_directory_under_home、Missing_database_returns_no_data、
Prices_hermes_frontier_models_from_builtin_snapshot、Tries_provider_qualified_model_candidate_first、
Uses_raw_model_when_provider_match_is_not_exact、Includes_both_candidates_when_both_match_exactly、
Normalizes_dots_and_at_signs_in_pricing_keys、Normalizes_and_infers_providers、
Rejects_non_finite_and_non_positive_timestamps）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/CCUsage.Reconciliation.Tests/fixtures/hermes/state.db`（合成数据，覆盖：五桶 +
   REAL 秒含小数截断（session-a/f）/REAL 毫秒（session-b）双刻度、billing_provider 缺失前缀推断
   （session-f）、actual cost 直采（session-b 0.05）、全零行（session-d）、空白 model 行（session-e））。
2. 录制 golden：`HERMES_HOME=$PWD/$FX/hermes npx --yes ccusage@20.0.29 hermes daily --json --offline
   --timezone UTC > $FX/golden/hermes-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateHermesAsync(HermesRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型清单两侧同；
  自研定价未命中清单为空；诊断 status=Ok、skipped=1（全零行）、records=3。

Result: 通过——两侧全量 **input=1,630 / output=343 / cacheCreation=20 / cacheRead=50 / total=2,043 tokens，
totalCost=$0.058325** 逐位一致；分日 2026-09-14（1,230/303/20/50/1,603，claude-sonnet-4-5 cost
0.008325 = 引擎计价 0.00819 + 前缀推断行 0.000135）与 2026-09-15（400/40/0/0/440，gpt-5.1 cost 0.05
actual 直采）逐位一致；诊断 status=Ok、skipped=1、records=3、UnpricedModels=∅。

### TC-003: 本机真实数据对账（CCUSAGE_RECONCILE_REAL=1 门控）

Steps:
1. `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Real_hermes"`。
2. 数据存在性门控：定位器（HERMES_HOME / ~/.hermes）下有 state.db 才运行（与 CLI 子进程同一套探测）；
   无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 hermes daily --json --offline --timezone UTC`（golden）
   vs `AggregateHermesAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（reasoning 显示差异 / estimated-only 成本差异 fail-loud，见偏差②⑥）。

Result: 本机（2026-10-11）**无 Hermes 数据 → 测试按门控 Skip**（与 claude/copilot/opencode/goose 门控
同态）。门控链路本身已在本机验证：以 `HERMES_HOME=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成
golden 与自研全链路对账**通过**（`Real_hermes_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上
运行该门控即得真实数字。同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/hermes/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 hermes/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 HermesLogLocator / HermesSessionParser / HermesSqliteAdapter
文件头；fixtures 全部合成，无真实数据入仓）：

- **骨架复用**：只读打开直接复用 I-009 泛化的 `SqliteSource/SqliteReadonly`（WAL 兼容），无新增分叉；
  schema 检查场景与 Goose 同类（单表固定列查询、prepare 失败 = 该库 0 条 + Partial 降级），复用同一处置
  模式不引入 ZCode 式必需列检查器；诊断沿用 JsonlScanDiagnostics + I-006 状态机。
- **Hermes 是会话级聚合源**（与 Goose 同型）：sessions 表每行 = 一个会话的累计用量（非逐请求），token 口径
  直取五桶（cache_write→cacheCreation、cache_read→cacheRead），无拆桶/回退链；reasoning 记蓝本
  extra_total_tokens——统一记录无该字段，按 I-006/I-009/I-012 口径并入 OutputTokens（蓝本计价把 reasoning
  按输出价计费，total 与 cost 一致）。
- **时间戳**（蓝本 timestamp_from_number）：started_at 为 REAL——f64 优先（非有限按缺失）、i64 兜底；
  >1e12 按毫秒、否则 ×1000；Math.Truncate 取整、须 > 0；f64→i64 溢出按 Rust as-cast 饱和钳位，越界值由
  MaxUnixMilliseconds 防御边界行级拒绝（I-006 同款）。
- **成本落点**（蓝本 calculate_hermes_cost）：stored cost = actual_cost_usd ?? estimated_cost_usd（非负），
  &gt; 0 直接采用（f64→decimal 经最短往返文本保持精确十进制）；否则 CostUsd=null 交定价引擎。**版本偏差**：
  本机实测发布版 CLI 20.0.29 只认 actual_cost_usd（fixture 探针：estimated-only 行 CLI 走计价 0.00045，
  蓝本 HEAD 会取 estimated 0.01）——自研按蓝本 HEAD 实现（actual ?? estimated），fixture 不含 estimated-only
  行保证对账逐位一致，真实数据上的 cost 差异 fail-loud（偏差⑥）。
- **provider 归一化与计价候选**：NormalizeProvider（小写、'-'→'_'、anthropic/claude、openai/openai_codex、
  google 系四别名、openrouter、xai、groq）与 InferProviderFromModel（claude-*/claude/*→anthropic、
  gpt*/chatgpt*/o&lt;digit&gt;→openai、gemini-*/gemini/*→google、其余 hermes）全分支移植；蓝本
  model_candidates（qualified 名仅在 find_exact_with_fallback 命中时插入——exact 或 '.'/'@'→'-' 键归一化，
  hermes 兜底 provider 不插限定名）移植为带谓词的纯函数。自研引擎按模型名定价（候选机制未端到端接入，
  偏差⑤）；blueprint 内联测试逐条移植（候选顺序、fuzzy 排除、点分归一化）。
- **message id**：取蓝本 message.id 形状 "hermes:&lt;session_id&gt;"，跨库去重按该键（与蓝本 session id
  全局集合双射同语义，先扫的库优先）。
- **诊断口径**（ZCodeSqlite 映射的 Hermes 版）：FilesDiscovered = 解析出的库数、FilesRead = 打开且完成查询
  的库数、LinesSkipped = 五桶与 cost 全零的会话行、MalformedLines 恒 0、InvalidRecords = 身份列不可读 +
  时间戳不可用/越界（蓝本静默跳过的行显式计数）、DuplicateRecords = 跨库 session 折叠；状态 Ok/Partial/
  NoData 同 I-006。
- **管道接线**：`IHermesSqliteAdapter` 单例 + LocalIngestionDataSource 第八路 concat；适配器无扫描状态
  （全量重读，与前三个 SQLite 适配器同理由）。
- **定价快照扩充**：新增 `xai` 段 grok-4.3（input 1.25 / output 2.5 / cache_read 0.2，取自蓝本
  models-dev-pricing.json bare "grok-4.3" 条目；tiers 分档字段按基础档计价，与引擎解析口径一致）——服务
  蓝本 `calculates_cost_for_hermes_frontier_models` 移植测试（gpt-5.5 已在 openai 段命中）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **messageCount 不落入统一记录**：UsageRecord 无请求数字段（CLI daily 的 messageCount 字段自研侧不复现，
  对账不含该字段）；蓝本 message_count 仅用于展示。
- ② **reasoning（extra_total_tokens）并入 OutputTokens**：CLI daily 的 outputTokens 不含 reasoning、
  totalTokens 含之——并入 output 使 total 与 cost 一致；含 reasoning 的真实数据 output 分量 fail-loud
  （I-009/I-012 同款偏差，fixture 行 reasoning=0 保证五分量逐位一致）。
- ③ **时间文本解析**：Hermes started_at 为数值列（秒/毫秒判别），无文本时间分支——与 Goose 的多形态解析
  不同，此处无超集问题。
- ④ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，I-006 系已登记差异同款）；
  `~` 展开/GetFullPath 为仓库定位器共有增强；蓝本 canonicalize 不做（本源候选路径固定无碰撞场景）。
- ⑤ **候选计价未端到端接入**：蓝本 calculate_hermes_cost 逐候选（qualified 限定名优先——仅 exact 命中时
  插入——再裸模型名）计价；自研引擎按裸模型名 + 自身候选机制匹配。**对账后果**：价格仅挂在 provider 限定键
  下的模型自研侧记 0（fail-loud）；fixture 模型按裸名两侧同价。候选机制已移植为纯函数并有移植测试。
- ⑥ **CLI 20.0.29 与蓝本 HEAD 的成本回退版本差**：蓝本 HEAD 的 cost_usd = actual.or(estimated)；
  实测发布版 CLI 20.0.29 只认 actual。自研按蓝本 HEAD（actual ?? estimated）——含 estimated-only 成本的
  真实数据上两侧 cost 会差（fail-loud）；golden 录制以 CLI 20.0.29 为准，fixture 规避该差异并登记。
- ⑦ **行级拒绝显式计数**（蓝本静默 continue）：身份列不可读 / 时间戳不可用或越界计 InvalidRecords，
  全零行计 LinesSkipped；空白 model 行由 SQL WHERE 排除（蓝本同式），不计诊断。
- ⑧ **u64 → long 边界**：蓝本 read_u64 的 i64 max(0) → 有限正 f64 截断链在 C# 等价移植，f64 超界钳
  long.MaxValue（saturating；不可达域防御）。

验证：`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` **393 通过 / 11 跳过**
（I-012 收口基线 370/10 + 新增 21 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；门控以 HERMES_HOME 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（四卡全部裁定可收口，无 P0/P1）。本卡无代码缺陷，仅注释级修正与登记：

- **[注释] HermesLogLocator 口径不对称说明**：`ResolveHomeCandidates("")` 继续走环境变量 / 默认根回退，
  而 `ResolveDatabases("")` 以"参数是否为 null"判定传入、空串即只用该值返回空（对齐蓝本 env::var 已设置
  即不回退）——在两个成员的文档注释中说明 ResolveDatabases 是适配器权威入口、空串语义对齐蓝本，
  ResolveHomeCandidates 仅供测试与诊断（行为无改动）。
- **[拆卡] 候选计价端到端接线 → [I-024](I-024-candidate-pricing-wiring.md)**：偏差⑤的落地路径拆小卡，
  Bugs 表登记 deferred（I-016-B1）；board.md Todo 加 I-024 行（PM 委托的唯一 board 改动）。
- 复核：`dotnet build CCUsage.sln` 0 警告 0 错误；全仓 `dotnet test` **417 通过 / 12 跳过**（本卡零代码
  改动，增量来自 I-009 审查修复的 2 项单测）；fixture 对账复验不变（golden 逐位一致）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-016-B1 | P2 | 候选计价未端到端（model_candidates exact-with-fallback / 点分归一化已移植为纯函数并测），别名/限定键模型记 0 fail-loud | deferred | 拆卡 [I-024](I-024-candidate-pricing-wiring.md)（候选计价端到端接线） |

## Verification

- Related files: `src/CCUsage.Infrastructure/HermesSqlite/`（HermesSqliteAdapter、HermesSessionParser、
  HermesLogLocator）、`src/CCUsage.Core/Abstractions/IHermesSqliteAdapter.cs`、
  `src/CCUsage.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第八路）、
  `src/CCUsage.App/DependencyInjection/AppServices.cs`（DI）、
  `src/CCUsage.Core/Pricing/model-pricing-snapshot.json`（xai 段）、
  `tests/CCUsage.Infrastructure.Tests/HermesSqlite/HermesSqliteAdapterTests.cs`、
  `tests/CCUsage.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第八适配器）、
  `tests/CCUsage.Reconciliation.Tests/`（HermesFixtureReconciliationTests、RealDataReconciliationTests、
  Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/FixturePaths、
  CCUsageCli/CcusageGoldenFiles）、`tests/CCUsage.Reconciliation.Tests/fixtures/hermes/state.db`
  （合成 fixture）、`tests/CCUsage.Reconciliation.Tests/fixtures/golden/hermes-fixture-daily-v20.0.29.json`
  （CLI 录制 golden + README 录制命令）
- How to run/verify: 全量 `dotnet build CCUsage.sln && dotnet test CCUsage.sln`；Hermes 单测
  `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HermesSqlite"`；
  fixture 对账 `dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Hermes"`；
  真实对账（有数据机器）`CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests
  --filter "FullyQualifiedName~Real_hermes"`（无数据自动 Skip）；golden 重录命令见
  `tests/CCUsage.Reconciliation.Tests/fixtures/golden/README.md`
- Results: 2026-10-11 — 构建 0 警告 0 错误；单测 21/21；全仓 393 通过 / 11 跳过；fixture 对账 tokens
  五分量 + cost 两侧逐位一致（全量 input=1,630 / output=343 / cacheCreation=20 / cacheRead=50 /
  total=2,043，cost=$0.058325，见 TC-002）；真实数据对账门控测试就绪，本机无 Hermes 数据按门控 Skip，
  门控链路经 HERMES_HOME 钉 fixture 全链路验证通过。
  2026-10-12（独立审查修复复验）— 注释级改动（定位器口径说明）+ I-024 拆卡登记；全仓 **417 通过 /
  12 跳过**；fixture 对账复验不变（golden 逐位一致）
