---
id: I-006
title: ZCode SQLite 适配器
type: feature
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 ZCode 桌面版本地 SQLite 账本（~/.zcode/cli/db/db.sqlite 的 model_usage ⟕ session），产出 agent=ZCode 的统一 usage 记录并接入本地采集管道。

## Background

用户主力工具之一，此前完全没有接入。蓝本：../ccusage/rust/adapters/zcode/（loader/parser/paths）。关键口径：仅取 status='completed' 行；ZCode 的 input_tokens 含 cache 读写两个桶，需扣除后拆桶（各自截断到 input）；computed_total_tokens 超出分量和的余量并入 output 计价；started_at 为 epoch 毫秒。本机实测（2026-10-11）发布版 ccusage CLI v20.0.29 对本机数据全部报 missingPricing（provider_id 为内部 UUID，不被视为 Z.ai），故对账口径 = tokens 精确一致 + 成本两侧同为缺失/0。

## Acceptance Criteria

- [x] ZCodeLogLocator：ZCODE_HOME（逗号分隔多根，规范化去重、无效根跳过）→ ~/.zcode，库路径 <root>/cli/db/db.sqlite（配置根与 ZCODE_HOME 同语义支持逗号多根）
- [x] schema 检查：必需列缺失的库跳过；可选列（cache_creation/cache_read/computed_total/provider_id）按存在性动态拼查询，computed_total 缺失回退 input+output
- [x] token 归一化与蓝本 parser.rs 逐条对齐（含截断、全零行跳过、extra total 余量并入 output）
- [x] 计价门槛对齐蓝本：Z.ai 限定 provider（或无 provider 的 glm-* 行）走定价引擎且 cache_creation 按 input 价计（并入 input）；其余 provider 行 CostUsd=0（与 CLI 的 missingPricing/0 一致）
- [x] 只读打开（WAL 兼容，ZCode 运行中可读），按 usage id 去重、按时间排序
- [x] 接入 LocalIngestionDataSource 与 DI，fixture 单元测试 + 真实数据对账（Q-001 harness 扩展 zcode 源）

## Subtasks

- [x] ZCodeLogLocator（根解析 + 库文件定位 + 多根去重）
- [x] ZCodeSchemaInspector（PRAGMA table_info + 必需/可选列检查）
- [x] ZCodeRecordMapper（row_to_entry 移植：归一化 + zai 门槛 + 余量处理）
- [x] ZCodeSqliteAdapter（只读打开、动态查询、诊断计数、id 去重、时间排序）
- [x] DI 注册 + LocalIngestionDataSource 管道接入
- [x] fixture 单元测试 ×13（临时 SQLite 库，无真实数据入仓）
- [x] Q-001 harness 扩展（AggregateZcodeAsync / LoadZcodeText / HasZcodeData）+ 真实数据对账通过

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 归一化与门槛（对照蓝本 parser.rs 内联测试）

Steps:
1. 临时 SQLite 库写入合成行（含双缓存桶、截断、余量、zai/自定义 provider、legacy schema、非 completed、无效身份、WAL 未提交事务等场景），运行适配器断言记录与诊断。

Expected:
- 双桶归一化 input=60/creation=15/read=25（zai 行 creation 并入 input=75、总量守恒）；截断与余量并入 output；仅 completed；无效/全零行计 InvalidRecords；自定义 provider CostUsd=0；WAL 下只读到已提交行。

Result: 通过（13/13：Normalizes_inclusive_cache_buckets、Zai_provider_rows_fold_cache_creation_into_input、Legacy_rows_without_provider_use_model_name_for_zai_gate、Custom_provider_rows_record_zero_cost、Clamps_cache_buckets_and_folds_extra_total、Filters_non_completed_rows、Rejects_rows_with_invalid_identity_or_zero_tokens、Reads_legacy_schema_without_optional_columns、Skips_unsupported_schema_and_degrades_to_partial、Deduplicates_cross_database_rows_by_id_keeping_first_root、Reads_committed_rows_from_wal_database_while_writer_holds_transaction、Missing_database_returns_no_data、Sorts_records_by_timestamp）。

### TC-002: 本机真实数据对账（Q-001 harness，CCUSAGE_RECONCILE_REAL=1 门控）

Steps:
1. `npx --yes ccusage@20.0.29 zcode daily --json --offline --timezone UTC` 取 CLI 口径。
2. `AggregateZcodeAsync()`（无 configuredHome，与 CLI 读同一套探测逻辑）全链路聚合。
3. ReconciliationComparator 按 日期×模型 与 每日/全期总计比较。

Expected:
- tokens 精确一致；成本两侧同为 0（本机数据全部为自定义 provider）。

Result: 通过——本机 22,315 条记录，自研 input=76,028,203 / output=18,539,131 / cacheCreation=0 / cacheRead=2,323,656,640 / total=2,418,223,974 / cost=0，与 CLI 逐位一致；per-model 36 组、每日 23 组 tokens 全等。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/zcode/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 zcode/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；fixtures 全部合成，无真实数据入仓）：

- **定位**（`ZCodeLogLocator`）：配置根优先（与 ZCODE_HOME 同语义，逗号分隔多根、~ 展开、GetFullPath 规范化、按序去重）→ ZCODE_HOME → ~/.zcode（USERPROFILE 优先，复用 JsonlHomePath）；库固定 <root>/cli/db/db.sqlite，根无效或库缺失即跳过、不回退默认目录（对齐蓝本"do not silently fall back"）；多根解析出同一库文件按序去重。
- **schema 检查**（`ZCodeSchemaInspector`，PRAGMA table_info）：必需列 model_usage[id, session_id, started_at, model_id, status, input_tokens, output_tokens] + session[id, directory]；可选列 cache_creation/cache_read/computed_total/provider_id 按存在性拼查询（缺失列以常量 0/NULL 占位保持列位固定），computed_total 缺失回退 `input + output`。蓝本的 session.version 仅用于诊断标签，本实现不读取（统一记录无对应字段）。
- **行映射**（`ZCodeRecordMapper`）：门槛（id/session_id/model_id 非空 + started_at>0）→ 负值行作废（对应蓝本 i64→u64 转换失败整行跳过）→ cache_read=min(read, input)、cache_creation=min(creation, input−read)、input=input−两桶（饱和）→ 全零行作废 → computed_total 余量并入 OutputTokens（蓝本计价同样把余量计入 output 价；统一记录无 extra_total_tokens 字段，并入使聚合总额与蓝本 display 总额一致——**口径偏差**：出现余量的行逐桶 output 显示含余量，实测本机数据 computed_total 与分量和恒相等，余量路径为 0）→ Z.ai 判定（显式 provider 枚举/前缀，或无 provider 的 glm-*）。
- **计价门槛**：Z.ai 行 CostUsd=null 走定价引擎，且 cache_creation 并入 input（Z.ai 把新内容展示为 creation 桶但按标准 input 价计费，蓝本 cost_usage 同式，逐 token 一致）；其余 provider 行 CostUsd=0m（LogCost 短路），对齐本机 CLI 实测 missingPricing/cost 0。**已知偏差**：蓝本对自定义 provider 行仍尊重 raw 模型用户覆盖，本实现 0m 短路在覆盖规则之下——用户覆盖与自定义 provider 同时出现时以 0 为准（真实场景未出现，后续有需求再评估）。
- **定价快照扩充**（联动 I-003）：内置快照新增 `zai` 段 22 个 GLM 条目（取自蓝本 models-dev-pricing.json 的 zai/* 键；input/output 缺失的条目丢弃，cache_read/cache_write 缺失省略键让解析器走与蓝本 pricing.rs 相同的缺省乘数 write=×1.25 / read=×0.1）——Z.ai 限定 provider 的行离线即可计价，与本机无关（本机全为自定义 provider）但对 zai plan 用户保证与 CLI 内嵌价一致。
- **读取语义**：Microsoft.Data.Sqlite `Mode=ReadOnly`，不改 journal mode——WAL 兼容经双保险验证（单测：写连接持未提交事务时只读连接读到已提交行；实测：对账测试在本会话运行期间对活跃 WAL 库全量读取成功）。每次扫描全量重读（账本单文件持续变化，mtime 增量不适用；completed 行万级、对账实测 22k 行全链路 5s 内含 CLI 进程）。
- **诊断口径**：沿用 JsonlScanDiagnostics——FilesDiscovered=发现的库数、FilesRead=打开且 schema 支持并完成查询的库数、InvalidRecords=行级拒绝（NULL 身份/坏列类型/身份无效/负值/全零，对应蓝本静默跳过的行，此处显式计数）、DuplicateRecords=跨库 id 重复折叠；MalformedLines/LinesSkipped 恒 0（无行解析概念）。状态：有记录且无问题=Ok，有问题=Partial，无记录=NoData。
- **管道接线**：`IZCodeSqliteAdapter` 单例 + LocalIngestionDataSource 增加第三路扫描（claude/codex/zcode concat）；适配器无扫描状态（与 JSONL 适配器的增量状态不同，注释已说明）；其余成本补价 / facts 落库路径零改动。

验证：`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` 255 通过 / 6 跳过（基线 242/5 原样保留，新增 13 项 ZCode 单测 + 1 项门控真实对账，门控开时实跑通过）；真实数据对账数字见 TC-002。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/ZCodeSqlite/`（ZCodeSqliteAdapter、ZCodeLogLocator、ZCodeSchemaInspector、ZCodeRecordMapper、ZCodeUsageRow）、`src/CCUsage.Core/Abstractions/IZCodeSqliteAdapter.cs`、`src/CCUsage.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、`src/CCUsage.App/DependencyInjection/AppServices.cs`、`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`（zai 段）、`tests/CCUsage.Infrastructure.Tests/ZCodeSqlite/ZCodeSqliteAdapterTests.cs`、`tests/CCUsage.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第三适配器）、`tests/CCUsage.Reconciliation.Tests/`（RealDataReconciliationTests、Support/UsageChainAggregator、Support/RealReconciliationEnvironment、CCUsageCli/CcusageGoldenFiles）
- How to run/verify: `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ZCodeSqlite"`；真实对账 `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests/CCUsage.Reconciliation.Tests.csproj --filter "FullyQualifiedName~Real_zcode"`
- Results: 2026-10-11 — 构建 0/0；单测 13/13；全仓 255 通过 / 6 跳过；真实对账 tokens 两侧逐位一致（22,315 条 / total 2,418,223,974），cost 两侧同为 0
