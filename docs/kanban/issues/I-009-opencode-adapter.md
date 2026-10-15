---
id: I-009
title: OpenCode 适配器
type: feature
priority: P2
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 OpenCode 本地 SQLite 数据（OPENCODE_DATA_DIR 或 ~/.local/share/opencode），产出 agent=OpenCode 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/opencode/。SQLite 直读模式与 I-006 ZCode 适配器同构（只读打开、schema 检查、动态列），可复用其定位器/检查器骨架。本机暂无该源数据，fixture 依蓝本合成，对账仅能做 fixture 级（CLI 有数据时才可真实对账）。

## Acceptance Criteria

- [x] 数据目录发现（OPENCODE_DATA_DIR → XDG 口径回退）与 SQLite 只读接入
  - ✅ 2026-10-11：`OpenCodeDataLocator` 逐条移植 paths.rs（OPENCODE_DATA_DIR 设置即只用配置目录、
    无效/为空不回退；XDG_DATA_HOME 须绝对路径 → `$HOME/.local/share`，均属仓库 JsonlHomePath 共享约定）；
    每目录选一个库（opencode.db 优先，否则按名排序取首个 opencode-*.db 频道库），
    `SqliteReadonly` 只读打开（WAL 兼容）
- [x] 按蓝本完成行过滤、token 口径映射与模型映射
  - ✅ v1 message 全行扫描（无 tokens 载荷 = user 消息，跳过）；v2 session_message 仅 assistant 行；
    lenient JSON 语义逐条移植（lenient_u64/i64/f64/object、non_empty_string）；tokens.cache.write/read →
    cache_creation/read；reasoning 与 tokens.total 余量按蓝本 apply_total_token_fallback 处理后并入
    output（统一记录无 extra_total_tokens 字段，I-006 口径）；time.created ≤ 0 → epoch（蓝本 keeps_epoch）；
    模型候选三函数（resolve/normalize/provider 前缀）逐条移植为纯函数并移植蓝本内联测试
- [x] 复用/泛化 I-006 的 SQLite 扫描骨架（避免复制分叉）
  - ✅ 新建共享原语 `SqliteSource/`（SqliteReadonly 只读打开 + SqliteTableSchema 表存在/列集检查），
    ZCodeSchemaInspector / ZCodeSqliteAdapter 同步改用（行为不变，I-006 全部单测原样通过）；
    不能共享的部分（定位器、行读取/映射、去重键、查询构造）与理由见 Dev Log
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 32 项单测（临时 SQLite 库，fixtures 全合成）+ 合成 fixture（opencode.db）入仓 +
    CLI golden 录制逐位一致 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip）；
    `IOpenCodeSqliteAdapter` + LocalIngestionDataSource 第六路 concat + App DI 双注册

## Subtasks

- [x] 共享骨架泛化：`SqliteSource/SqliteReadonly`（Mode=ReadOnly，WAL 兼容，I-006 OpenReadOnly 抽出）+
  `SqliteSource/SqliteTableSchema`（sqlite_master 表存在性 + PRAGMA table_info 列集 + 必需列判断），
  ZCode 侧改为引用共享原语（I-006 18 项单测零改动通过）
- [x] `OpenCodeDataLocator`：configuredDataDir → OPENCODE_DATA_DIR（逗号分隔、去空白、仅存目录、按序去重、
  设置即不回退）→ XDG_DATA_HOME（绝对路径才生效）→ `$HOME/.local/share/opencode`（目录不存在即空，不回退）
- [x] `OpenCodeMessageParser`（parser.rs 移植）：lenient JSON 读取助手（GetU64/GetI64/GetF64/GetObject/
  GetNonEmptyString，与蓝本 jsonl.rs 助手一一对应）→ v1 MapMessage / v2 MapSessionMessage（model 嵌套对象
  id/modelID/providerID 优先、time.created 缺失回退列值）→ apply_total_token_fallback（reasoning 记入 extra、
  total 余量补入，output 为 0 时优先补 output）→ 全零行跳过 / 缺 modelID·providerID 拒绝 →
  stored cost > 0 直采（REAL→decimal 经最短往返文本保精确十进制）、否则 CostUsd=null 交定价引擎；
  模型候选三函数（ResolveModelName：gemini-3-pro-high→gemini-3-pro-preview、k2p6→kimi-k2.6；
  NormalizeModelName：claude-{haiku,opus,sonnet}- 点分/两位数字归一；BuildModelCandidates：原名+归一化名+
  provider '-'→'_' 前缀变体、相邻去重）
- [x] `OpenCodeSqliteAdapter`（loader.rs SQLite 主线移植）：每目录选库（opencode.db 优先 → 频道库按名排序
  取首个，中段字符集校验）→ v1 `message` 全行扫描 → v2 `session_message`（必需列 [id,session_id,type,data]，
  可选 time_created/seq 按存在性拼查询，缺列整表静默跳过——蓝本 prepare 失败同口径）→ fork 拷贝剔除
  （fork_copy_cutoffs/fork_copy_cutoff/last_parent_seq_before/is_fork_copy 全量移植：through/before 边界、
  不可解析保留全行、无 seq 列保留全行、fork-of-fork 链式解析）→ message id 去重（映射成功后，跨目录共用
  一个集合，v1 先扫）→ 按时间稳定排序 → 诊断计数（ZCodeSqlite 口径映射）与状态机
- [x] 定价快照扩充：新增 `moonshot` 段 kimi-k2.6（input 0.95 / output 4 / cache_read 0.16，取自蓝本
  models-dev-pricing.json 的 bare "kimi-k2.6" 条目，与 CLI 内嵌价一致）。**注意**：真实数据 record.Model
  仍为 "k2p6"，引擎按原始名定价、快照键是 kimi-k2.6——真实 k2p6 行仍记 0（UnpricedModels fail-loud）；
  快照当前只服务"手工 Resolve 后计价"的单测（Prices_k2p6_alias_through_snapshot_after_model_resolution），
  端到端接线拆卡 I-024
- [x] 单元测试 ×34（`OpenCodeSqliteAdapterTests`，临时 SQLite 库：v1/v2 映射、cache 非对象、total 兜底、
  epoch、k2p6/覆盖计价、type 过滤、无 time_created 列、列值时间回退、跨表去重、fork 七场景、诊断计数、
  映射后去重、跨目录去重、频道库选择、坏库、排序、定位器八场景；含独立审查新增的 v1 列门槛与 v2
  读取顺序 2 项回归）
- [x] 对账：合成 fixture（v1+v2+fork+user+坏 JSON+stored cost 双路径+跨表去重）入仓 +
  CLI golden 录制（fixtures/golden/opencode-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasOpenCodeData 数据存在性 Skip）
- [x] 管道接线：`IOpenCodeSqliteAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第六路 concat +
  App DI 双注册（具体类 + 接口）

## Dependencies

- I-006（SQLite 骨架）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 loader.rs / parser.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 SQLite 库 fixture（v1 message 表、v2 session_message 表、session_v2 fork 边界、
   频道库命名、坏 JSON、user 行、cache 非对象、total 兜底、缺时间列、跨表同 id、坏库、多目录），运行
   `OpenCodeSqliteAdapter.ScanAsync` 断言记录、诊断与 fork 剔除行为；模型候选与计价用纯函数 + PricingEngine
   （内置快照 / 用户覆盖）断言蓝本内联测试同款数字。

Expected:
- v1/v2 行映射（列值 id/session_id 优先、model 嵌套对象优先、time.created 缺失回退列值/epoch）；
  cache 非对象按缺失；total 123 → output 123；k2p6 → kimi-k2.6 后按快照计 0.000143（100/10/read50），
  覆盖价 1/10/0.1（每百万）计 0.000205；stored cost 0.02/0.005 直采；fork through/before/不可解析/无元数据/
  无 seq/fork-of-fork/seq 稀疏七场景与蓝本断言一致；坏 JSON 计 malformed、user 行与非 assistant 行计 skipped、
  有载荷缺 modelID 计 invalid；同 id 跨表/跨目录去重保先扫者、无载荷首现行不占去重名额；频道库按名排序、
  默认库优先；OPENCODE_DATA_DIR 设置即不回退、XDG 绝对路径口径、配置目录优先于环境变量。

Result: 通过（Infrastructure 32/32，用例名对照蓝本内联测试：Maps_message_rows_from_sqlite_database、
Keeps_record_when_cache_field_is_not_an_object、Falls_back_to_total_tokens_when_token_parts_are_missing、
Keeps_epoch_when_message_timestamp_is_missing、Prices_k2p6_alias_through_snapshot_after_model_resolution、
Prices_zero_stored_cost_from_tokens_with_override_rates、Maps_v2_assistant_rows_and_ignores_other_types、
Maps_v2_rows_without_time_created_column、Falls_back_to_time_created_column_when_payload_time_is_missing、
Deduplicates_legacy_and_v2_rows_by_message_id、Excludes_copied_history_through_the_fork_boundary、
Excludes_copied_history_before_the_fork_boundary、Keeps_fork_rows_when_the_boundary_cannot_be_resolved、
Keeps_fork_rows_without_fork_metadata、Keeps_fork_rows_when_session_messages_have_no_seq_column、
Excludes_copied_history_from_a_fork_of_a_fork、Keeps_fork_rows_after_the_last_copy_when_before_seqs_are_sparse、
Counts_row_level_rejections_by_diagnostics_kind、Dedup_happens_after_mapping_so_valid_row_survives_payload_less_first_occurrence、
Deduplicates_cross_directory_rows_keeping_first_directory、Loads_channel_sqlite_database、
Channel_database_selection_prefers_default_then_sorted_channel_names、Missing_database_returns_no_data、
Undreadable_database_degrades_to_partial、Sorts_records_by_timestamp_across_tables、定位器八项等）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/opencode/opencode.db`（合成 SQLite 库，
   覆盖：v1/v2 双表、同 message id 跨表去重（v2 的 msg-a 999/999/0.99 被 v1 压制）、fork through 边界拷贝剔除、
   user 行（v1 无 tokens / v2 非 assistant）、坏 JSON 载荷、stored cost 直采（gpt-5.1 0.005）与为 0 走计价
   （claude-sonnet-4-5、gpt-5.1 命中内置快照）两路径、多模型多日）。
2. 录制 golden：`OPENCODE_DATA_DIR=$PWD/$FX/opencode npx --yes ccusage@20.0.29 opencode daily --json
   --offline --timezone UTC > $FX/golden/opencode-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateOpenCodeAsync(OpenCodeRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型清单两侧同；
  自研定价未命中清单为空；诊断 status=Partial（fixture 故意含 1 行坏 JSON）、malformed=1、dedup=1、records=6。

Result: 通过——两侧全量 **input=2,233 / output=323 / cacheCreation=120 / cacheRead=340 / total=3,016 tokens，
totalCost=$0.042626250000000004** 逐位一致；分日 2026-09-14（1,500/250/100/300/2,150，cost 0.011465：
claude-sonnet-4-5 计价 0.006465 + gpt-5.1 stored 0.005）与 2026-09-15（733/73/20/40/866，
cost 0.03116125：claude 0.03045 = stored 0.01+0.02+计价 0.00045、gpt-5.1 计价 0.00071125）逐位一致；
per-model（claude-sonnet-4-5 1,433/243/100/300、gpt-5.1 800/80/20/40）逐位一致；诊断 status=Partial、
malformed=1、linesSkipped=4（msg-user + v2-user + 两行 fork 拷贝）、dedup=1、records=6、UnpricedModels=∅。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_opencode"`。
2. 数据存在性门控：定位器（OPENCODE_DATA_DIR → XDG 默认根）下至少一个目录选出库文件才运行（与 CLI 子进程
   同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 opencode daily --json --offline --timezone UTC`（golden）
   vs `AggregateOpenCodeAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（含 reasoning 的真实数据在 output 分量上会 fail-loud，见偏差④）。

Result: 本机（2026-10-11）**无 OpenCode 数据 → 测试按门控 Skip**（与 claude/copilot 门控同态）。门控链路
本身已在本机验证：以 `OPENCODE_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与自研
全链路对账**通过**（`Real_opencode_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行该门控即得
真实数字。同轮 zcode / copilot 等门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/opencode/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 opencode/ 的 loader.rs / parser.rs / paths.rs SQLite 主线逐条对齐；蓝本许可
MIT License，Copyright (c) 2025 ryoppippi，声明保留在 OpenCodeDataLocator / OpenCodeMessageParser /
OpenCodeSqliteAdapter 文件头；fixtures 全部合成，无真实数据入仓）：

- **骨架共享方式（AC-3）**：从 I-006 抽出两级共享原语 `src/Tokenmetry.Infrastructure/SqliteSource/`——
  `SqliteReadonly`（Mode=ReadOnly 打开，WAL 兼容）与 `SqliteTableSchema`（sqlite_master 表存在性 +
  PRAGMA table_info 列集 + 必需列判断），ZCodeSchemaInspector / ZCodeSqliteAdapter 同步改为引用共享
  原语（行为不变，I-006 全部单测零改动通过，避免复制分叉）。**不共享并说明理由**：①定位器——
  OPENCODE_DATA_DIR/XDG 双默认根语义与 ZCODE_HOME 多根语义完全不同，仅共享 JsonlHomePath 的
  home/`~` 展开约定；②行读取与映射——ZCode 是类型化 token 列（read_token_column 钳 0），OpenCode 是
  整行 JSON 载荷的 lenient 语义（serde 助手等价移植），容错模型不可复用；③去重键与查询构造——
  message id vs usage id、动态列表查询形状不同；④诊断口径——沿用同一套 JsonlScanDiagnostics 字段
  与状态机（共享），字段到行为的映射各源自定（见偏差⑥）。
- **每目录一库**（蓝本 db_path）：opencode.db 存在即用；否则文件名匹配频道库模式（opencode-*.db，
  中段仅字母数字/_/-）按完整路径 Ordinal 排序取首个。多目录各自选库，跨目录按 message id 去重
  （先配置的目录优先，蓝本 seen 集合同序）。
- **v1/v2 双轮扫描**（蓝本 load_entries_from_database 顺序）：v1 `message` 全行（无 type 过滤，靠
  "无 tokens 载荷 = 非 usage 行" 自然跳过 user 消息）；v2 `session_message` 必需列 [id, session_id,
  type, data]（缺失整表静默跳过 = 蓝本 prepare 失败）、可选 time_created/seq 按存在性拼查询（缺 seq 时
  fork 分类无从判定、行保留）、仅 type='assistant' 行；载荷 time.created 缺失回退列值 created
  （蓝本 into_legacy_message 的 or(Some(created))）。同库内 v1 先扫先去重（蓝本顺序，v1 行压制 v2 同 id 行）。
- **fork 拷贝剔除全量移植**：session_v2[id, fork_session_id, fork_boundary] + session_message[id,
  session_id, seq] 列集门槛；边界 JSON {type, messageID} → 父会话边界行 seq → through 取该 seq、
  before 取 MAX(seq)<边界seq（空集 = 无拷贝全保留）、其他类型/不可解析/缺父行不记截断点（fork 全保留）；
  seq ≤ 截断点的 fork 行剔除且不占去重名额（蓝本"Copies neither count nor mark the fork as covered"）。
- **token 口径**：tokens.cache.write/read → cache_creation/read；reasoning 记入蓝本 extra_total_tokens、
  tokens.total 超出分量和的余量按 apply_total_token_fallback 补入（output 为 0 时优先补 output）——
  统一记录无 extra 字段，按 I-006 口径把 extra（reasoning + 余量）并入 OutputTokens；蓝本计价把 extra
  按输出价计费（cost_usage.output = output+extra），故 total 与 cost 两侧一致。
- **成本落点**（蓝本 calculate_open_code_cost）：消息自带正 cost（REAL，f64→decimal 经最短往返文本解析
  保持 0.005 等精确十进制）直接采用（CostUsd 非 null，管道 LogCost 短路）；否则 CostUsd=null 交定价引擎
  （未命中 → 聚合记 0 + UnpricedModels，与 CLI missingPricing/0 同态）。
- **诊断口径**（ZCodeSqlite 映射的 OpenCode 版，蓝本静默跳过的行显式计数）：FilesDiscovered = 选中的库数、
  FilesRead = 打开且查询完成的库数、LinesSkipped = 无 usage 载荷行（user 消息）+ 非 assistant v2 行 +
  fork 拷贝行、MalformedLines = data 载荷非法 JSON 行、InvalidRecords = 身份列不可读 + 有载荷但缺
  modelID/providerID + 时间戳越界（MaxUnixMilliseconds 防御边界，I-006 同款）、DuplicateRecords = message id
  折叠；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IOpenCodeSqliteAdapter` 单例 + LocalIngestionDataSource 第六路 concat；适配器无扫描状态
  （全量重读，与 ZCode SQLite 适配器同理由）。
- **定价快照扩充**：内置快照新增 `moonshot` 段 kimi-k2.6（input 0.95 / output 4 / cache_read 0.16，取自
  蓝本 models-dev-pricing.json bare "kimi-k2.6" 条目）。**口径澄清**：真实数据 record.Model = "k2p6"
  （原始名），自研引擎按原始名定价、快照键是 kimi-k2.6——真实 k2p6 行仍记 0（UnpricedModels fail-loud），
  快照目前只服务移植测试里的手工 Resolve 后计价场景；候选计价端到端接线拆卡 I-024（偏差⑤的落地路径）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **storage/message legacy JSON 回退未实现**：蓝本把 `<dir>/storage/message/**/*.json` 作为 SQLite 之外
  的兜底源并按 message id 幂等（DB 优先）。本卡 AC 与桌面定位均为 SQLite 直读；旧版 OpenCode 的纯 JSON
  安装在本卡范围外（后续有需求再评估，去重键/幂等语义蓝本已具备）。
- ② **会话累计表聚合回退未实现**：蓝本 session/session_v2 累计表仅在 session 报表且无时间过滤时作为
  "无消息级用量" 的兜底（daily 报表不消费）。桌面统一记录面向日聚合（= daily 口径），引入累计快照反而
  有双计风险；随 fork 场景保留 fork-local 计数器数据在库内，未消费。
- ③ **since/until 窗口与 SQL pushdown 未实现**（含 time_created 毫秒刻度采样、payload 时间预提取）：桌面
  六路适配器统一全量出账、日期过滤/分桶由下游聚合承担（I-008 偏差⑦同款架构）。窗口宽化一天等保守逻辑
  随 pushdown 一起不适用。
- ④ **extra_total_tokens（reasoning + total 余量）并入 OutputTokens**：CLI daily 的 outputTokens 不含
  reasoning（extra 单列、skip 序列化），但 totalTokens 含之。统一记录四桶无法表达第五桶——并入 output 使
  total 与 cost 两侧一致、output 分量在含 reasoning 的数据上大于 CLI 显示值。**对账后果**：真实机器上
  若数据含 reasoning，output 分量会以差异形式响亮失败（fail-loud，非静默漂移）；fixture 不含 reasoning
  保证五分量逐位一致。蓝本解析层行为（apply_total_token_fallback 的 extra 归属）逐条移植，仅落点不同。
- ⑤ **候选计价未端到端接入**：CLI 计价逐个尝试 [resolve 名, normalize 名, provider/前缀变体]（首个正成本
  生效），显示名保持原始 modelID。自研 record.Model = 原始名（对齐 CLI 显示/分组），定价引擎按原始名匹配
  ——三个候选函数已逐条移植为纯函数并有移植测试（候选清单逐位一致、k2p6→kimi-k2.6 计价 0.000143），但
  引擎不对候选循环。**对账后果**：价格仅挂在别名/归一化名/provider 前缀键下的模型自研侧记 0（fail-loud）；
  fixture 模型（claude-sonnet-4-5、gpt-5.1）按原名命中快照，不受影响。moonshot 段仅服务手工 Resolve 后
  计价的移植测试，端到端接线拆卡 I-024。
- ⑥ **行级拒绝显式计数**（蓝本全部静默 continue）：映射到 LinesSkipped/MalformedLines/InvalidRecords 三类
  （口径见上），诊断不改变记录内容，仅状态机 Partial 判定多了 malformed/invalid 输入。
- ⑦ **定位器增强**：`~` 展开与 GetFullPath 规范化为仓库 ZCode/Copilot 定位器共有增强（蓝本 PathBuf 不展开）；
  home 解析沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 home.rs HOME 优先——常规环境两者一致，与
  I-006/I-008 已登记差异同款）。
- ⑧ **u64 → long 边界**：蓝本 token 计数为 u64，统一记录为 long——超出 long.MaxValue 的计数钳到
  long.MaxValue（saturating；实际 token 计数不可达的防御边界，与蓝本 saturating_add 语义兼容）。
- ⑨ **非 UTF-8 环境变量不可表达**：蓝本对 OPENCODE_DATA_DIR 的 OsString 分支（非 UTF-8 值"设置了但读不出"
  → 不回退默认目录）在 C# 中环境变量统一解码为字符串（无效字节替换为 U+FFFD），得到"非空但目录不存在"
  的同款结果（不回退、空列表）；Linux 发现非 UTF-8 目录名的场景（蓝本 discovers_non_utf8）不可复现，登记
  为平台差异。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **349 通过 / 9 跳过**
（基线 315/8 原样保留 + 新增 32 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；门控以 OPENCODE_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（四卡全部裁定可收口，无 P0/P1；fork 七场景、时间戳方向等易错点的
移植忠实度核对证实）。修复与登记：

- **修复 [P2] v1 message 表缺列集门槛**：原实现对 v1 表直接 SELECT（v2 有 RequiredSessionMessageColumns
  门槛、v1 没有），畸形 message 表缺核心列时 ExecuteReader 抛 SqliteException → 整库标 problems、v2 扫描
  一并跳过；蓝本 prepare 失败是"静默跳过 v1、继续扫 v2、**不标问题**"。修复：与 v2 对称加
  ContainsAll(columns, [id, session_id, data]) 门槛。新增回归测试
  Skips_malformed_message_table_but_still_scans_session_messages（畸形 v1 跳过、v2 正常扫描、状态 Ok）。
- **修复 [P2] v2 行诊断桶归类**：原实现在 type 过滤之前读全部四个文本列，非 assistant 行 data 不可读会计
  InvalidRecords；蓝本先查 type 再读 data（该场景是无声非事件）。修复：读取顺序改为"先判 type 是否
  assistant、再读 data"。新增回归测试 Non_assistant_rows_with_unreadable_data_count_as_skipped
  （非 assistant 行带坏 data → LinesSkipped 而非 InvalidRecords）。
- **[表述修正] moonshot 快照定位**：原 Dev Log/Subtasks 的"服务真实 kimi 场景"偏乐观——真实数据
  record.Model = "k2p6"、引擎按原始名定价、快照键是 kimi-k2.6，真实 k2p6 行仍记 0（UnpricedModels
  fail-loud）；快照目前只服务"手工 Resolve 后计价"的移植测试。三处表述同步改写（Dev Log / Subtasks /
  偏差⑤）。
- **[拆卡] 候选计价端到端接线 → [I-024](I-024-candidate-pricing-wiring.md)**：Bugs 表登记 deferred
  （I-009-B1）；board.md Todo 加 I-024 行（PM 委托的唯一 board 改动）。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；全仓 `dotnet test` **417 通过 / 12 跳过**（新增 2 项
  OpenCode 单测）；OpenCode 单测 34/34；fixture 对账复验不变（golden 逐位一致）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-009-B1 | P2 | 候选计价未端到端（resolve/normalize/provider 前缀三函数已移植为纯函数并测），别名/限定键模型记 0 fail-loud | deferred | 拆卡 [I-024](I-024-candidate-pricing-wiring.md)（候选计价端到端接线） |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/OpenCodeSqlite/`（OpenCodeSqliteAdapter、OpenCodeMessageParser、
  OpenCodeDataLocator）、`src/Tokenmetry.Infrastructure/SqliteSource/`（SqliteReadonly、SqliteTableSchema，
  自 I-006 泛化的共享骨架）、`src/Tokenmetry.Infrastructure/ZCodeSqlite/`（改用共享原语，行为不变）、
  `src/Tokenmetry.Core/Abstractions/IOpenCodeSqliteAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第六路）、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`（DI）、
  `src/Tokenmetry.Core/Pricing/model-pricing-snapshot.json`（moonshot 段）、
  `tests/Tokenmetry.Infrastructure.Tests/OpenCodeSqlite/OpenCodeSqliteAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第六适配器）、
  `tests/Tokenmetry.Reconciliation.Tests/`（OpenCodeFixtureReconciliationTests、RealDataReconciliationTests、
  Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/FixturePaths、
  CCUsageCli/CcusageGoldenFiles）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/opencode/opencode.db`
  （合成 fixture）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/opencode-fixture-daily-v20.0.29.json`
  （CLI 录制 golden + README 录制命令）
- How to run/verify: 全量 `dotnet build Tokenmetry.sln && dotnet test Tokenmetry.sln`；OpenCode 单测
  `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OpenCodeSqlite"`；
  fixture 对账 `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~OpenCode"`；
  真实对账（有数据机器）`TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests
  --filter "FullyQualifiedName~Real_opencode"`（无数据自动 Skip）；golden 重录命令见
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`
- Results: 2026-10-11 — 构建 0 警告 0 错误；单测 32/32；全仓 349 通过 / 9 跳过；fixture 对账 tokens
  五分量 + cost 两侧逐位一致（全量 input=2,233 / output=323 / cacheCreation=120 / cacheRead=340 /
  total=3,016，cost=$0.042626250000000004，见 TC-002）；真实数据对账门控测试就绪，本机无 OpenCode 数据
  按门控 Skip，门控链路经 OPENCODE_DATA_DIR 钉 fixture 全链路验证通过。
  2026-10-12（独立审查修复复验）— 构建 0 警告 0 错误；单测 34/34；全仓 **417 通过 / 12 跳过**；
  fixture 对账复验不变（golden 逐位一致）；其余源门控对账复跑无回归
