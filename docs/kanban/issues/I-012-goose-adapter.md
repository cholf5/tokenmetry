---
id: I-012
title: Goose 适配器
type: feature
priority: P2
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Goose 会话 SQLite（GOOSE_PATH_ROOT 或 ~/.local/share/goose、~/.local/share/Block/goose 下的 sessions.db），产出 agent=Goose 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/goose/。SQLite 直读，双默认根 + 环境变量覆盖；复用 I-006 SQLite 骨架。本机暂无数据。

## Acceptance Criteria

- [x] 多根发现与环境变量覆盖
  - ✅ 2026-10-11：`GooseLogLocator` 逐条移植 paths.rs——GOOSE_PATH_ROOT（单根，去首尾空白，**空串视为
    未设置**）→ &lt;root&gt;/data/sessions/sessions.db；默认三候选按序全探测（~/.local/share/goose、
    ~/Library/Application Support/goose（macOS 根）、~/.local/share/Block/goose——卡片所称"双默认根"实为
    蓝本 default_goose_db_candidates 的三个候选，全部保留存在的文件）；候选按序去重、只保留真实库文件
- [x] 按蓝本完成 schema 检查、行过滤与 token 映射
  - ✅ 固定列查询与蓝本 GOOSE_SESSION_QUERY 同构（model_config_json 为空白在 SQL 层排除）；token 取值
    read_token_value 口径（仅正整数生效、accumulated_* 优先、total 回退 input+output）；时间戳四形态
    （整数秒/毫秒判别、时间文本、19 字符空格分隔归一、纯日期）；model_config_json.model_name；
    provider 归一化前缀推断全分支；reasoning（total − 分量和）并入 output（I-006 口径）；全零行跳过
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 19 项单测（临时 SQLite 库，fixtures 全合成）+ 合成 fixture（sessions.db）入仓 +
    CLI golden 录制逐位一致 + `CCUSAGE_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip）；
    `IGooseSqliteAdapter` + LocalIngestionDataSource 第七路 concat + App DI 双注册

## Subtasks

- [x] `GooseLogLocator`：configuredRoot → GOOSE_PATH_ROOT（单根、trim、空回退默认）→ 三默认候选
  （.local/share/goose、Library/Application Support/goose、.local/share/Block/goose，均 + sessions/sessions.db）；
  File.Exists 过滤 + 按序去重（蓝本 canonicalize + seen；C# GetFullPath 不解析 symlink，登记差异）
- [x] `GooseSessionMapper`（parser.rs row_to_entry 移植）：时间戳解析（TryParseTimestamp：整数 >1e12 按毫秒
  /否则 ×1000（须 >0）→ JsonlTimestamp 文本解析 → 19 字符 "YYYY-MM-DD HH:MM:SS[/T]" 归一 UTC →
  10 字符纯日期 T00:00:00Z）；model_config JSON（model_name non_empty_string）；provider 归一化
  （显式 '-'→'_'；缺省 claude-→anthropic / gpt-·chatgpt-·o*→openai / gemini-→google / qwen*→openrouter /
  其余 goose）；映射产出（MessageId = session id、CostUsd 恒 null、cache 两桶恒 0、reasoning 并入 output）
- [x] `GooseSqliteAdapter`（loader.rs 移植）：逐库只读打开（共享 SqliteReadonly）→ 固定列查询 →
  行读取（身份/时间列不可读静默跳过并计数；token 列 i64>0 过滤 + accumulated 回退链）→
  按 (库路径, session id) 去重（蓝本键形状）→ 时间稳定排序 → 诊断与状态机
- [x] 单元测试 ×19（`GooseSqliteAdapterTests`：accumulated 优先与回退链、全零行、坏 config/坏时间戳、
  时间四形态+整数列、SQL 层空白排除、无 sessions 表降级、双库同 session id 保留、配置根优先、排序、WAL、
  定位器四场景、provider 归一化九分支、越界时间防御）
- [x] 对账：合成 fixture（accumulated 优先 + 干扰 plain 列 + 秒级时间戳 + 全零/坏 config/坏时间戳行）入仓 +
  CLI golden 录制（fixtures/golden/goose-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `CCUSAGE_RECONCILE_REAL` 门控真实对账测试（HasGooseData 数据存在性 Skip）
- [x] 管道接线：`IGooseSqliteAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第七路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无新增段**——fixture 覆盖的 claude-sonnet-4-5（3/15/0.3/3.75）与 gpt-5.1
  （1.25/10/0.125/cache_write 缺省 ×1.25=1.5625）均已在 anthropic/openai 段命中且与 CLI 内嵌价一致
  （fixture 对账 cost 逐位同），无 I-006 式补段需求

## Dependencies

- I-006（SQLite 骨架）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 loader.rs 内联测试 loads_accumulated_tokens_from_goose_sqlite
与 parser.rs 实现语义）

Steps:
1. 测试内临时目录合成 sessions.db（accumulated 与 plain 列并存、全零行、坏 JSON config、缺 model_name、
   不可解析时间戳、整数秒/毫秒/RFC3339/纯日期/空格分隔时间形态、整数存储的时间列、无 sessions 表、
   双库同 session id、WAL、越界毫秒），运行 `GooseSqliteAdapter.ScanAsync` 断言记录、诊断与降级行为；
   provider 归一化与时间解析以纯函数断言蓝本分支。

Expected:
- accumulated_* 列优先（plain 列为干扰值）；仅正整数生效（负值/零回退）；total 回退 input+output；
  reasoning = total − 分量和并入 output（180 → output 50+30=80 型）；全零行计 skipped；坏 config / 坏
  时间戳计 invalid；整数秒 ×1000、>1e12 按毫秒、0/负值作废；19 字符空格分隔归一 UTC、纯日期补
  T00:00:00Z；越界毫秒行级拒绝不击穿扫描；model_config_json 为空白在 SQL 层排除不计诊断；无 sessions 表
  0 条 + 降级；双库同 session id 都保留（去重键含库路径）；WAL 只读已提交行；provider 前缀推断
  claude-→anthropic / gpt-·chatgpt-·o*→openai / gemini-→google / qwen*→openrouter / 其余 goose。

Result: 通过（Infrastructure 19/19：Loads_accumulated_tokens_from_goose_sqlite、
Falls_back_to_plain_token_columns_when_accumulated_are_not_positive、Skips_all_zero_sessions_as_skipped_rows、
Rejects_rows_with_unparsable_config_or_timestamp、Parses_all_timestamp_shapes、Reads_integer_created_at_column、
Excludes_blank_model_config_rows_in_sql、Database_without_sessions_table_degrades_to_no_data、
Reads_multiple_databases_keeping_same_session_id_across_paths、Configured_root_takes_priority_over_environment_variable、
Sorts_records_by_timestamp、Reads_committed_rows_from_wal_database_while_writer_holds_transaction、
Discovers_goose_path_root_database、Empty_goose_path_root_falls_back_to_default_candidates、
Probes_all_three_default_candidates_in_order、Missing_home_and_databases_yield_no_data、
Normalizes_provider_by_name_then_model_prefix、Rejects_invalid_epoch_values、
Rejects_timestamp_beyond_datetimeoffset_range）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/CCUsage.Reconciliation.Tests/fixtures/goose/data/sessions/sessions.db`（合成数据，
   覆盖：accumulated 列优先 + plain 干扰值、整数秒时间戳（session-b）、19 字符空格分隔时间（session-a）、
   provider 缺失前缀归一（gpt-5.1 → openai）、tokens 全零行、坏 JSON config 行、不可解析时间戳行）。
2. 录制 golden：`GOOSE_PATH_ROOT=$PWD/$FX/goose npx --yes ccusage@20.0.29 goose daily --json --offline
   --timezone UTC > $FX/golden/goose-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateGooseAsync(GooseRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型清单两侧同；
  自研定价未命中清单为空；诊断 status=Partial（fixture 故意含坏 config / 坏时间戳各 1 行计 invalid）、
  skipped=1（全零行）、records=2。

Result: 通过——两侧全量 **input=1,300 / output=230 / cacheCreation=0 / cacheRead=0 / total=1,530 tokens，
totalCost=$0.006675** 逐位一致；分日 2026-09-14（1,000/200/0/0/1,200，claude-sonnet-4-5 cost 0.006 =
1000×$3/M + 200×$15/M）与 2026-09-15（300/30/0/0/330，gpt-5.1 cost 0.000675 = 300×$1.25/M + 30×$10/M）
逐位一致；诊断 status=Partial、invalid=2、skipped=1、records=2、UnpricedModels=∅。

### TC-003: 本机真实数据对账（CCUSAGE_RECONCILE_REAL=1 门控）

Steps:
1. `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Real_goose"`。
2. 数据存在性门控：定位器（GOOSE_PATH_ROOT → 三默认候选）下至少一个库文件才运行（与 CLI 子进程同一套
   探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 goose daily --json --offline --timezone UTC`（golden）
   vs `AggregateGooseAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（含 reasoning 的真实数据在 output 分量上会 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Goose 数据 → 测试按门控 Skip**（与 claude/copilot/opencode 门控同态）。
门控链路本身已在本机验证：以 `GOOSE_PATH_ROOT=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden
与自研全链路对账**通过**（`Real_goose_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行该
门控即得真实数字。同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/goose/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 goose/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 GooseLogLocator / GooseSessionMapper / GooseSqliteAdapter 文件头；
fixtures 全部合成，无真实数据入仓）：

- **骨架复用**：只读打开走 I-009 泛化的共享原语 `SqliteSource/SqliteReadonly`（WAL 兼容），schema 检查
  场景较简单（单表固定列查询，蓝本靠 prepare 失败隐式降级），不引入 ZCode 式必需列检查器；Goose 的
  "schema 检查" = 查询 prepare 失败 / sessions 表缺失 → 该库 0 条 + Partial 降级（蓝本 unwrap_or_else
  同语义），行过滤在 SQL WHERE（model_config_json 非空白）完成。
- **多根发现**（卡片 AC-1 勘误）：蓝本 GOOSE_PATH_ROOT 为**单根**（非逗号分隔多根）——设置即只扫
  `<root>/data/sessions/sessions.db`；默认候选为**三个**（Linux XDG 根、macOS Application Support 根、
  Block 根），存在即全扫（多库场景）。卡片 Background 的"双默认根"按蓝本实现细化为三候选。
- **token 口径**（蓝本 read_token_value + 回退链）：仅正整数生效（NULL/零/负值/类型异常 → 回退下一列）；
  accumulated_input/output/total 优先，回退 input/output/total，total 最终回退 input+output；全零行
  （三分量皆 0）不是 usage 行，跳过。reasoning = total − (input + output)（saturating），蓝本记入
  extra_total_tokens 并按输出价计费——统一记录无该字段，按 I-006/I-009 口径并入 OutputTokens（total 与
  cost 两侧一致，见偏差②）。
- **时间戳**（蓝本 parse_goose_timestamp 四分支逐条对齐）：整数 >1e12 直接按毫秒、否则 ×1000（蓝本
  checked_mul，数值域内不可达溢出，C# checked 保留同构防御）、须 >0；文本走 JsonlTimestamp（RFC3339
  超集）；19 字符 "YYYY-MM-DD HH:MM:SS/T" 归一 "…T…Z"；10 字符纯日期补 "T00:00:00Z"。越界毫秒
  （> MaxUnixMilliseconds）按行级无效拒绝（I-006 防御边界，蓝本 jiff 域更宽）。
- **模型与 provider**：model_name 取 model_config_json（non_empty_string，解析失败整行作废）；provider
  归一化（蓝本 normalize_provider 全分支）在自研侧仅作为已移植纯函数保留（自研引擎按模型名定价，provider
  候选机制见偏差①）。
- **成本落点**：Goose 无逐请求成本（蓝本 cost_usd 恒 None，逐行 calculate_goose_cost 计价），自研
  CostUsd 恒 null 交定价引擎（I-008 口径）；未命中 → 聚合记 0 + UnpricedModels（CLI missingPricing 同态）。
- **诊断口径**（ZCodeSqlite 映射的 Goose 版）：FilesDiscovered = 解析出的库数、FilesRead = 打开且完成查询
  的库数、LinesSkipped = tokens 全零的会话行、MalformedLines 恒 0（无 JSONL 行解析概念）、InvalidRecords =
  身份/时间列不可读 + 时间戳不可解析 + model_config 不可解析 + 时间戳越界（蓝本静默跳过的行显式计数）、
  DuplicateRecords = (库路径, session id) 折叠（蓝本键形状；同库 session id 为 PRIMARY KEY，实际折叠只
  发生在路径归一碰撞场景）；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IGooseSqliteAdapter` 单例 + LocalIngestionDataSource 第七路 concat；适配器无扫描状态
  （全量重读，与 ZCode/OpenCode SQLite 适配器同理由）。
- **定价快照**：无新增段——claude-sonnet-4-5 与 gpt-5.1 均命中现有 anthropic/openai 段且与 CLI 内嵌价
  逐位一致（gpt-5.1 的 cache_write 双方都缺省 ×1.25 推导）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入**：蓝本 calculate_goose_cost 先按裸模型名计价、未命中且 provider ≠ goose 时
  再试 `{provider_id}/{model}` 候选（missing_goose_pricing 同候选清单）。自研 record.Model = 裸模型名
  （对齐 CLI 显示/分组），定价引擎按模型名 + 自身候选机制匹配。**对账后果**：价格仅挂在 provider 前缀键
  下的模型自研侧记 0（fail-loud）；fixture 模型按裸名两侧同价，不受影响。蓝本对 provider == "goose"
  （前缀推断兜底）的行不做候选尝试、直接以裸名计价——与自研行为自然一致。
- ② **reasoning（extra_total_tokens）并入 OutputTokens**：CLI daily 的 outputTokens 不含 reasoning
  （extra 单列、skip 序列化），totalTokens 含之。统一记录四桶无法表达第五桶——并入 output 使 total 与
  cost 两侧一致。**对账后果**：真实数据 accumulated_total > input+output（模型有推理输出）时 output
  分量会以差异形式响亮失败（fail-loud）；fixture 行 accumulated_total = 分量和保证五分量逐位一致。
- ③ **canonicalize 差异**：蓝本对每条候选路径 canonicalize（符号链接归一）后去重，C# GetFullPath 不解析
  symlink——经链接指向同一物理库的候选不合并（与 I-006 定位器已登记差异同类）；(库路径, session id)
  去重键兜底，仅重复读 + DuplicateRecords 计数口径受影响。
- ④ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 home.rs HOME 优先——常规环境两者
  一致，I-006/I-008 已登记差异同款）；`~` 展开/GetFullPath 为仓库定位器共有增强。
- ⑤ **时间文本解析为蓝本严格口径的超集**：C# JsonlTimestamp（DateTimeOffset.TryParse AssumeUniversal）
  会接受蓝本 parse_ts_timestamp 静默丢弃的行（空格分隔变体、微秒/纳秒精度、缺时区等）并出账；真实
  Goose 数据为 RFC3339 / 空格分隔秒级，仅极端输入下产生两侧差异，以对账差异形式 fail-loud。
- ⑥ **行级拒绝显式计数**（蓝本静默 continue）：映射到 LinesSkipped（全零）/ InvalidRecords（身份、时间、
  config 不可用），诊断不改变记录内容。
- ⑦ **u64 → long 边界**：蓝本 token 计数为 u64，统一记录为 long——蓝本 read_token_value 仅正 i64 生效，
  读取层结构性保证非负且 ≤ i64 域，无钳位分支（与 I-009 的 lenient_u64 场景不同）。

验证：`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` **370 通过 / 10 跳过**
（I-009 收口基线 349/9 + 新增 19 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；门控以 GOOSE_PATH_ROOT 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（四卡全部裁定可收口，无 P0/P1）。本卡无代码缺陷，登记与勘误：

- **[勘误] AC-3 单测数字**：AC-3 的"Infrastructure 20 项单测"与实际 19 项矛盾，改为 19（Dev Log 与
  Verification 的数字本就一致，仅 AC 文本勘误）。
- **[拆卡] 候选计价端到端接线 → [I-024](I-024-candidate-pricing-wiring.md)**：偏差①（provider 限定候选
  计价）的落地路径拆小卡，Bugs 表登记 deferred（I-012-B1）；board.md Todo 加 I-024 行（PM 委托的唯一
  board 改动）。
- 复核：`dotnet build CCUsage.sln` 0 警告 0 错误；全仓 `dotnet test` **417 通过 / 12 跳过**（本卡零代码
  改动，增量来自 I-009 审查修复的 2 项单测）；fixture 对账复验不变（golden 逐位一致）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-012-B1 | P2 | 候选计价未端到端（provider 前缀推断与 `{provider}/{model}` 候选已移植为纯函数并测），别名/限定键模型记 0 fail-loud | deferred | 拆卡 [I-024](I-024-candidate-pricing-wiring.md)（候选计价端到端接线） |

## Verification

- Related files: `src/CCUsage.Infrastructure/GooseSqlite/`（GooseSqliteAdapter、GooseSessionMapper、
  GooseLogLocator）、`src/CCUsage.Core/Abstractions/IGooseSqliteAdapter.cs`、
  `src/CCUsage.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第七路）、
  `src/CCUsage.App/DependencyInjection/AppServices.cs`（DI）、
  `tests/CCUsage.Infrastructure.Tests/GooseSqlite/GooseSqliteAdapterTests.cs`、
  `tests/CCUsage.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第七适配器）、
  `tests/CCUsage.Reconciliation.Tests/`（GooseFixtureReconciliationTests、RealDataReconciliationTests、
  Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/FixturePaths、
  CCUsageCli/CcusageGoldenFiles）、`tests/CCUsage.Reconciliation.Tests/fixtures/goose/data/sessions/sessions.db`
  （合成 fixture）、`tests/CCUsage.Reconciliation.Tests/fixtures/golden/goose-fixture-daily-v20.0.29.json`
  （CLI 录制 golden + README 录制命令）
- How to run/verify: 全量 `dotnet build CCUsage.sln && dotnet test CCUsage.sln`；Goose 单测
  `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GooseSqlite"`；
  fixture 对账 `dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Goose"`；
  真实对账（有数据机器）`CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests
  --filter "FullyQualifiedName~Real_goose"`（无数据自动 Skip）；golden 重录命令见
  `tests/CCUsage.Reconciliation.Tests/fixtures/golden/README.md`
- Results: 2026-10-11 — 构建 0 警告 0 错误；单测 19/19；全仓 370 通过 / 10 跳过；fixture 对账 tokens
  五分量 + cost 两侧逐位一致（全量 input=1,300 / output=230 / cacheCreation=0 / cacheRead=0 / total=1,530，
  cost=$0.006675，见 TC-002）；真实数据对账门控测试就绪，本机无 Goose 数据按门控 Skip，门控链路经
  GOOSE_PATH_ROOT 钉 fixture 全链路验证通过。
  2026-10-12（独立审查修复复验）— 本卡零代码改动（AC-3 数字勘误 + I-024 拆卡登记）；全仓 **417 通过 /
  12 跳过**；fixture 对账复验不变（golden 逐位一致）
