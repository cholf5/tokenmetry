---
id: I-017
title: Kilo 适配器
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Kilo 本地 SQLite（KILO_DATA_DIR 或 ~/.local/share/kilo/kilo.db），产出 agent=Kilo 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/kilo/。SQLite 直读，复用 I-006 骨架。本机暂无数据。

## Acceptance Criteria

- [x] 目录发现与 schema 检查；按蓝本完成 token 映射
  - ✅ 2026-10-11：`KiloDataLocator` 逐条移植 paths.rs（KILO_DATA_DIR 逗号分隔多目录、**设置即只用配置
    目录**、无效/为空不回退；默认 ~/.local/share/kilo 且目录必须存在）；每目录固定取 &lt;目录&gt;/kilo.db，
    schema 检查 = 查询 prepare 失败（表/列缺失）→ 该库 0 条 + Partial 降级（蓝本同语义）；
    token 映射：五桶 lenient 读取（cache.write/read → cacheCreation/cacheRead）、reasoning 与 total 余量
    按 apply_total_token_fallback 处理后并入 output（I-006 系口径）、仅 assistant 行、载荷 cost 直采
    （蓝本 Auto 口径：Some(0.0) 记 0 不再计价——与 OpenCode 的 &gt;0 过滤不同）
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 20 项单测（临时 SQLite 库，fixtures 全合成）+ 合成 fixture（kilo.db）入仓 +
    CLI golden 录制逐位一致 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip）；
    `IKiloSqliteAdapter` + LocalIngestionDataSource 第九路 concat + App DI 双注册

## Subtasks

- [x] `KiloDataLocator`：configuredDataDir → KILO_DATA_DIR（逗号分隔、去空白、仅存目录、按序去重、
  设置即不回退默认）→ ~/.local/share/kilo（目录不存在即空）；库固定 &lt;目录&gt;/kilo.db
- [x] `KiloMessageParser`（parser.rs 移植）：lenient JSON 读取复用共享 `LenientJson`（I-009 首建、本卡
  从 OpenCodeSqlite 上移到 SqliteSource 共享）；role 原样比较 "assistant"（不 trim，蓝本 Option&lt;String&gt;）；
  apply_total_token_fallback（reasoning 记入 extra、total 余量补入，output 为 0 时优先补 output）；
  normalize_timestamp（≤0/缺失作废、&lt;1e12 按秒 ×1000 checked、防御越界）；session id / message id
  回退链（载荷 → 列值 → "&lt;db&gt;:&lt;row&gt;"）；stored cost Auto 语义（存在即采用含 0）；
  provider 归一化与计价候选纯函数（qualified 仅 find_exact 命中时插入、unknown/kilo 兜底不限定）
- [x] `KiloSqliteAdapter`（loader.rs 移植）：逐库只读打开（共享 SqliteReadonly）→ 固定列查询 →
  行读取（身份列不可读静默跳过并计数）→ message id 去重（映射成功之后，跨目录共用一个集合）→
  时间稳定排序 → 诊断与状态机
- [x] 单元测试 ×20（`KiloSqliteAdapterTests`：五桶+成本直采、无时间戳行、非 assistant 行（含空格角色）、
  cache 非对象、total 兜底、时间刻度与 ≤0 拒绝、id 回退链、诊断计数+坏库、跨目录去重、映射后去重、排序、
  WAL、定位器五场景、候选纯函数、Auto 成本语义）
- [x] 对账：合成 fixture（assistant 过滤 + 五桶 + stored cost 0/0.005 双路径 + user 行 + 坏 JSON）入仓 +
  CLI golden 录制（fixtures/golden/kilo-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasKiloData 数据存在性 Skip）
- [x] 管道接线：`IKiloSqliteAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第九路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无新增段**——fixture 的 claude-sonnet-4-5 / gpt-5.1 stored cost 非正即短路或缺失行
  无覆盖诉求（fixture 无计价路径行），现有 anthropic/openai 段足够

## Dependencies

- I-006（SQLite 骨架）、I-009（SqliteSource 共享原语与 LenientJson）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 loader.rs / parser.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 kilo.db（五桶齐全 + reasoning、stored cost 0/0.005/缺失、非 assistant 行（含
   空格角色）、无时间戳行、created ≤ 0、秒级时间戳、cache 非对象、total 兜底、载荷 id/sessionID 缺失、
   坏 JSON、跨目录同 id、WAL），运行 `KiloSqliteAdapter.ScanAsync` 断言记录、诊断与降级行为；
   候选顺序与 Auto 成本语义以纯函数 + PricingEngine 断言蓝本内联测试同名场景。

Expected:
- assistant 原样比较（空格角色行跳过）；cache 非对象按缺失；total 234 → output 234；reasoning 并入
  output；created ≤ 0 静默跳过（此处计 invalid）、秒级 ×1000；载荷 id 缺失回退 "&lt;db&gt;:&lt;row&gt;"、
  sessionID 缺失回退列值；stored cost 0.005 直采、0.0 记 0 不计价（Auto）、缺失走引擎（gpt-5 命中快照）；
  坏 JSON 计 malformed、有载荷缺 modelID 计 invalid；跨目录同 id 保先配置目录、无载荷首现行不占去重名额；
  候选 [provider/model, model]（exact 命中）/ [model]（fuzzy 不命中 / unknown / kilo）；provider '-'→'_'。

Result: 通过（Infrastructure 20/20：Loads_kilo_messages_from_sqlite、Ignores_messages_without_timestamps、
Skips_non_assistant_rows、Keeps_record_when_cache_field_is_not_an_object、
Falls_back_to_total_tokens_when_parts_are_missing、Normalizes_timestamp_scales_and_rejects_non_positive、
Falls_back_to_row_ids_when_payload_ids_missing、Counts_row_level_rejections_and_degrades_on_bad_database、
Deduplicates_messages_across_data_dirs_keeping_first、
Dedup_happens_after_mapping_so_valid_row_survives_payload_less_first_occurrence、Sorts_records_by_timestamp、
Reads_committed_rows_from_wal_database_while_writer_holds_transaction、
Kilo_data_dir_environment_variable_is_respected、Keeps_configured_data_dirs_and_deduplicates、
Does_not_fall_back_when_configured_data_dir_is_empty_or_missing、Falls_back_to_local_share_kilo_under_home、
Missing_default_directory_yields_no_data、Includes_provider_qualified_candidate_when_exact、
Uses_raw_model_when_provider_match_is_not_exact、Stored_zero_cost_records_zero_and_missing_cost_goes_to_engine）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/kilo/kilo.db`（合成数据，覆盖：assistant
   过滤、五桶映射、stored cost 0（msg-a）与 0.0（msg-c）按 Auto 记 0 不计价、stored 0.005（msg-b）
   LogCost、user 行、坏 JSON 行）。
2. 录制 golden：`KILO_DATA_DIR=$PWD/$FX/kilo npx --yes ccusage@20.0.29 kilo daily --json --offline
   --timezone UTC > $FX/golden/kilo-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateKiloAsync(KiloRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型清单两侧同；
  自研定价未命中清单为空；诊断 status=Partial（fixture 故意含 1 行坏 JSON）、skipped=1（user 行）、
  records=3。

Result: 通过——两侧全量 **input=1,600 / output=260 / cacheCreation=100 / cacheRead=300 / total=2,260
tokens，totalCost=$0.005** 逐位一致（claude-sonnet-4-5 1,100/210/100/300、cost 0——两行 stored cost
为 0/0.0 按蓝本 Auto 记 0；gpt-5.1 500/50/0/0、cost 0.005 LogCost）；诊断 status=Partial、malformed=1、
skipped=1、records=3、UnpricedModels=∅、0 成本记录 2（与 Auto 口径一一对应）。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_kilo"`。
2. 数据存在性门控：定位器（KILO_DATA_DIR / 默认根）下至少一个目录有 kilo.db 才运行（与 CLI 子进程同一套
   探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 kilo daily --json --offline --timezone UTC`（golden）
   vs `AggregateKiloAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（reasoning/total 余量并入 output 的显示差异 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Kilo 数据 → 测试按门控 Skip**（与 claude/copilot/opencode/goose/hermes
门控同态）。门控链路本身已在本机验证：以 `KILO_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI
生成 golden 与自研全链路对账**通过**（`Real_kilo_data_reconciles_with_cli` Passed，1/1）。有真实数据的
机器上运行该门控即得真实数字。同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/kilo/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 kilo/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 KiloDataLocator / KiloMessageParser / KiloSqliteAdapter 文件头；
fixtures 全部合成，无真实数据入仓）：

- **骨架复用与 LenientJson 上移**：只读打开复用 `SqliteSource/SqliteReadonly`；本卡把 I-009 的
  OpenCodeJson lenient 助手从 OpenCodeSqlite 上移为共享 `SqliteSource/LenientJson`（蓝本 jsonl.rs 助手
  等价移植），OpenCode 侧同步改引用（行为不变，I-009 全部单测零改动通过），Kilo 直接复用——Kilo 与
  OpenCode 同为 "message 表 + JSON 载荷" 形态，读取语义共享、映射语义各自移植。schema 检查 = 查询
  prepare 失败（表/列缺失）→ 该库 0 条 + Partial 降级（蓝本 debug_log + 空列表同语义）。
- **行过滤**（蓝本顺序）：role 原样比较 "assistant"（蓝本 Option&lt;String&gt; 不 trim——空格角色行跳过）
  → tokens 缺失跳过 → 四桶+extra 全零跳过 → modelID 缺失拒绝 → 时间戳 normalize 失败跳过 →
  越界防御拒绝。user 消息等非 usage 行计 LinesSkipped；有载荷但缺 modelID / 时间不可用计 InvalidRecords
  （蓝本静默跳过的行显式计数）；坏 JSON 计 MalformedLines。
- **时间戳**（蓝本 normalize_timestamp）：created ≤ 0 / 缺失 → 跳过（None 分支）；&lt;1e12 按秒 ×1000
  （checked，蓝本 checked_mul 同构）；≥1e12 按毫秒原样；越界（&gt; MaxUnixMilliseconds）行级拒绝
  （I-006 防御边界）。注意与 Goose 的判别方向不同：Kilo 是 &lt;1e12 → ×1000（秒级优先），Goose 是
  &gt;1e12 → 毫秒——两源蓝本各自如此，逐条保真。
- **id 回退链**：session id = 载荷 sessionID ?? 列值；message id = 载荷 id ?? "&lt;db 路径&gt;:&lt;row id&gt;"
  （蓝本同式——无内嵌 id 的行仍可跨目录去重，且键含库路径不会误并）。
- **成本落点**（蓝本 CostMode::Auto）：载荷 cost 存在即采用（**含 0**——Some(0.0) 记 0 不再计价，与
  OpenCode 的 &gt;0 过滤不同；fixture 对账验证 claude-sonnet-4-5 行两侧同为 0）；缺失才 CostUsd=null 交
  定价引擎（未命中 → 聚合记 0 + UnpricedModels）。非有限 cost 按缺失处理（防御）。
- **诊断口径**（ZCodeSqlite 映射的 Kilo 版）：FilesDiscovered = 选中的库数、FilesRead = 打开且查询完成的
  库数、LinesSkipped = 非 assistant / 无 tokens / 全零行、MalformedLines = 坏 JSON 载荷行、InvalidRecords =
  身份列不可读 + 缺 modelID + 时间不可用/越界、DuplicateRecords = message id 折叠；状态 Ok/Partial/NoData
  同 I-006。
- **管道接线**：`IKiloSqliteAdapter` 单例 + LocalIngestionDataSource 第九路 concat；适配器无扫描状态
  （全量重读，与前四个 SQLite 适配器同理由）。
- **定价快照**：无新增段——fixture 行的 stored cost 均非正（Auto 短路）或直采，无计价路径；真实数据中
  cost 缺失的行按裸模型名走引擎（anthropic/openai 段已覆盖主流模型）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入**：蓝本 calculate_kilo_cost_from_tokens 逐候选（qualified 仅 find_exact 命中
  时插入——无点分归一化，比 Hermes 的 exact-with-fallback 更严；unknown/kilo 兜底 provider 不插限定名）
  计价。自研 record.Model = 裸模型名，引擎按模型名 + 自身候选机制匹配。**对账后果**：价格仅挂在
  provider 限定键下的模型自研侧记 0（fail-loud）；候选机制已移植为纯函数并有移植测试（蓝本
  uses_exact_provider_qualified_pricing / uses_raw_model_for_timestamped_pricing 两场景逐条对齐）。
- ② **reasoning（extra_total_tokens）并入 OutputTokens**：CLI daily 的 outputTokens 不含 reasoning、
  totalTokens 含之——并入 output 使 total 与 cost 一致（蓝本 cost_usage 同式）；含 reasoning 的真实数据
  output 分量 fail-loud（I-009/I-012/I-016 同款偏差，fixture 不含 reasoning 行保证五分量逐位一致）。
- ③ **Display 模式不复现**：蓝本 Display 直接用 stored cost（缺 0），Auto 才走计价；自研单一口径 =
  Auto 语义（桌面应用无 display-only 模式），对 stored cost 存在的行行为一致。
- ④ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，I-006 系已登记差异同款）；
  `~` 展开/GetFullPath 为仓库定位器共有增强；蓝本 paths 不做 canonicalize（无此差异）。
- ⑤ **行级拒绝显式计数**（蓝本静默 continue）：见上诊断口径段；诊断不改变记录内容。
- ⑥ **u64 → long 边界**：蓝本 token 计数为 u64，读取层把 u64 溢出钳到 long.MaxValue（saturating；
  不可达域防御）；秒 ×1000 的 checked 溢出按蓝本 checked_mul 作废。
- ⑦ **无 since/until 窗口**：桌面九路适配器统一全量出账、日期过滤/分桶由下游聚合承担（I-008 偏差⑦
  同款架构；Kilo 蓝本无窗口相关特殊逻辑，无损失）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **415 通过 / 12 跳过**
（I-016 收口基线 393/11 + 新增 20 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；门控以 KILO_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（四卡全部裁定可收口，无 P0/P1）。本卡无代码缺陷，仅登记：

- **[拆卡] 候选计价端到端接线 → [I-024](I-024-candidate-pricing-wiring.md)**：偏差①（qualified find_exact
  候选计价）的落地路径拆小卡，Bugs 表登记 deferred（I-017-B1）；board.md Todo 加 I-024 行（PM 委托的
  唯一 board 改动）。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；全仓 `dotnet test` **417 通过 / 12 跳过**（本卡零代码
  改动，增量来自 I-009 审查修复的 2 项单测）；fixture 对账复验不变（golden 逐位一致）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-017-B1 | P2 | 候选计价未端到端（qualified find_exact 候选已移植为纯函数并测），别名/限定键模型记 0 fail-loud | deferred | 拆卡 [I-024](I-024-candidate-pricing-wiring.md)（候选计价端到端接线） |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/KiloSqlite/`（KiloSqliteAdapter、KiloMessageParser、
  KiloDataLocator）、`src/Tokenmetry.Infrastructure/SqliteSource/LenientJson.cs`（自 OpenCodeSqlite 上移共享）、
  `src/Tokenmetry.Core/Abstractions/IKiloSqliteAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第九路）、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`（DI）、
  `tests/Tokenmetry.Infrastructure.Tests/KiloSqlite/KiloSqliteAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第九适配器）、
  `tests/Tokenmetry.Reconciliation.Tests/`（KiloFixtureReconciliationTests、RealDataReconciliationTests、
  Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/FixturePaths、
  CCUsageCli/CcusageGoldenFiles）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/kilo/kilo.db`
  （合成 fixture）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/kilo-fixture-daily-v20.0.29.json`
  （CLI 录制 golden + README 录制命令）
- How to run/verify: 全量 `dotnet build Tokenmetry.sln && dotnet test Tokenmetry.sln`；Kilo 单测
  `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj --filter "FullyQualifiedName~KiloSqlite"`；
  fixture 对账 `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Kilo"`；
  真实对账（有数据机器）`TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests
  --filter "FullyQualifiedName~Real_kilo"`（无数据自动 Skip）；golden 重录命令见
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`
- Results: 2026-10-11 — 构建 0 警告 0 错误；单测 20/20；全仓 415 通过 / 12 跳过；fixture 对账 tokens
  五分量 + cost 两侧逐位一致（全量 input=1,600 / output=260 / cacheCreation=100 / cacheRead=300 /
  total=2,260，cost=$0.005，见 TC-002）；真实数据对账门控测试就绪，本机无 Kilo 数据按门控 Skip，门控
  链路经 KILO_DATA_DIR 钉 fixture 全链路验证通过。
  2026-10-12（独立审查修复复验）— 本卡零代码改动（I-024 拆卡登记）；全仓 **417 通过 / 12 跳过**；
  fixture 对账复验不变（golden 逐位一致）
