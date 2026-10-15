---
id: I-008
title: GitHub Copilot CLI 适配器
type: feature
priority: P2
size: L
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 GitHub Copilot CLI 本地数据（~/.copilot/session-state/*/events.jsonl 与 otel/*.jsonl），产出 agent=Copilot CLI 的统一 usage 记录。

## Background

本机有真实数据（~/.copilot，约 68KB）。蓝本：../ccusage/rust/adapters/copilot/（该源是 18 源中语义最复杂的：session-state 的 shutdown 记录是按 (session, model) 累计快照，resume 会重复出账，需做相邻快照差分；cache 读写含在 inputTokens 内需拆分；reasoning 已含在 output 内；-1m 等内部后缀需剥离后再定价；OpenTelemetry 行与 shutdown 区间有取优合并逻辑）。建议分两步实现：先 session-state JSONL（覆盖主要数据），otel 合并作为进阶验收。

> 勘误（2026-10-11 实现时核实）：本机 `~/.copilot` 实测只有 `config.json` / `ide/` / `logs/`（约 60KB 日志），
> **没有 session-state/ 与 otel/ 用量数据**；`npx ccusage@20.0.29 copilot daily --json --offline` golden 输出
> `daily: []`、totals 全 0。真实数据对账因此改为"COPILOT_HOME 钉住仓库内合成 fixture 的 CLI golden 录制 +
> 数据存在性门控的真实对账测试"双轨口径（见 TC-002 / TC-003）。

## Acceptance Criteria

- [x] session-state shutdown 累计快照 → 区间差分（首条保留、后条减前条），resume 场景总量守恒
  - ✅ 2026-10-11：`CopilotSessionParser` 内完成（同 key 去重保最新 → (session, model) 分组 →
    (时间戳, 出现序) 排序 → 首条保留、后条 saturating 减前条）；resume 跨日回归测试 + fixture golden 逐位一致
- [x] token 拆分：uncached input = inputTokens − cache 读写；reasoning 不单列
  - ✅ 解析时先拆（saturating），差分作用在 uncached 累计值上（蓝本 uncached_session_input_tokens +
    subtract_usage 口径）；reasoningTokens 仅参与无事件 id 的 dedup key，不落入统一记录（已含在 output）
- [x] 内部模型后缀（-1m / -1m-internal 等）剥离后再匹配定价
  - ✅ `NormalizeModel` 先 -1m-internal 后 -1m、仅一次、前后空白去除（蓝本 normalize_copilot_model 同序）；
    剥离发生在分组之前，后缀变体与基础模型同组累计差分、命中基础模型价格
- [x] （进阶）otel 行与 shutdown 区间取优合并；进程无 shutdown 结束的调用不计（对齐蓝本口径）
  - ✅ **2026-10-11 由 [I-023](I-023-copilot-otel-merge.md) 收口**（otel 四形态解析/互斥去重/取优
    合并 + totalNanoAiu credit gap）：`CopilotOtelParser` + `CopilotUsageReconciler` 落地，
    蓝本 31 个内联测试锚点逐条对齐（35 项单测），fixture golden（含 otel 接管与 checkpoint 数据）
    重录后逐位一致；since/until 相关逻辑按桌面全量出账架构塌缩（偏差清单见 I-023 卡）。
    totalNanoAiu credit gap 为 cost-only（0 token）条目落统一记录 unknown 模型。
- [x] fixtures + 单元测试（含 resume 差分回归）+ 真实数据对账
  - ✅ Infrastructure 16 项单测（fixtures 全合成）+ 对账 fixture 入仓 + CLI golden 录制逐位一致 +
    `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip；COPILOT_HOME 钉 fixture 时全链路通过）

## Subtasks

- [x] `CopilotLogLocator`：configuredHome → COPILOT_HOME（trim、非空才生效）→ ~/.copilot
  （USERPROFILE 优先，复用 JsonlHomePath）；session-state **一级**会话目录的 events.jsonl（非递归），
  路径排序（蓝本 paths.rs 口径）；otel/ 目录与 COPILOT_OTEL_FILE_EXPORTER_PATH 留 I-023
- [x] `CopilotSessionParser`：行级预过滤（含 shutdown/checkpoint 标记才进解析，蓝本 LinePrefilter::any）→
  仅消费 session.shutdown / session.usage_checkpoint → modelMetrics 累计快照抽取（lenient_u64 口径：
  仅非负整数生效；modelMetrics 任一模型值非对象则整表按缺失——蓝本 lenient_object all-or-nothing）→
  uncached 拆分 + 后缀剥离 + dedup key（事件 id 优先，无 id 按蓝本字段序拼原始累计用量）
- [x] 累计快照差分：同 key 保留最新时间戳（相等取后出现者，蓝本 deduplicate_session_entries）→
  (session, model) 分组 → 组内 (时间戳, 出现序) 排序 → 首条保留、后条 saturating 减前条 →
  有 token 的区间产出，按 (时间戳, dedup key) 排序
- [x] `CopilotSessionAdapter`：共享 `IncrementalJsonlScanner` 接线（mtime/大小记账，变更文件整文件重解析）；
  差分状态只在单文件解析内存活（分组键含会话目录名，一个根目录内会话目录唯一 ⇒ 组不跨文件，
  文件内差分 == 蓝本"池化→去重→分组→差分"）；MessageId/RequestId 恒 null（共享去重器直通）
- [x] 定价：记录 CostUsd 恒 null，由 PricingEngine 按区间快照时间戳计价（CLI cost-at-timestamp 口径）；
  快照**无新增定价段**——验证 fixture 覆盖的 claude-sonnet-4 / gpt-5.1 / gpt-5-mini 均已命中且单价与
  CLI --offline 一致（0.0537 / 0.0171875 / 0.0009 两侧逐位同），无 I-006 式补段需求
- [x] 管道接线：`ICopilotSessionAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第五路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 单元测试 ×16（Infrastructure.Tests/CopilotSession，fixtures 全合成，含 resume 差分/总量守恒回归、
  同 id 重复 shutdown 去重、嵌套目录不发现、COPILOT_HOME 生效、增量重扫幂等）
- [x] 对账：合成 fixture 入仓 + CLI golden 录制（fixtures/golden/copilot-fixture-daily-v20.0.29.json）+
  fixture 对账测试（tokens 五分量 + cost 逐位一致）+ TOKENMETRY_RECONCILE_REAL 门控真实对账测试
- [x] otel 合并 + totalNanoAiu credit gap → 已由 I-023 收口（2026-10-11）：`CopilotLogLocator`
  补 otel/ 递归与 COPILOT_OTEL_FILE_EXPORTER_PATH 单文件收集；`CopilotOtelParser` 四形态解析 +
  互斥去重；`CopilotUsageReconciler` 取优合并（差分移至合并器池化重算）+ credit gap cost-only
  unknown 条目；fixtures/golden 扩充重录逐位一致（I-023 卡 Verification / TC-002）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与差分语义（对照蓝本 parser.rs / loader.rs 内联测试）

Steps:
1. 测试内临时目录合成 session-state 会话 fixture（多模型 / 缓存读写 / reasoning / 请求数 / 全零条目 /
   后缀变体 / 同 id 重复 shutdown / 坏 JSON / 非目标事件 / checkpoint / 缺时间戳 / 嵌套目录），运行
   `CopilotSessionAdapter.ScanAsync` 断言记录、诊断与差分行为（蓝本内联测试同名场景逐一对齐）。

Expected:
- uncached input 拆分（100−10−20=70 型）；reasoning 不单列；后缀剥离先 -1m-internal 后 -1m、仅一次、
  去空白，且后缀变体与基础模型同组累计差分；同 id 重复 shutdown 去重不出双份；resume 跨日差分
  （首条原样出账、后条减前条）且区间之和 == 末条累计（总量守恒）；全零用量无请求数条目跳过、
  全零但有请求数条目保留作差分基线；坏 JSON（过预过滤）计 malformed 不中断、普通事件与 checkpoint
  计 skipped、缺可解析时间戳计 invalid；嵌套会话目录不发现；COPILOT_HOME 覆盖默认目录；
  配置目录不存在 → NoData；增量重扫只重解析变更文件且幂等；claude-sonnet-4 按内置快照计价
  （4000×$3/M + 1000×$15/M + 2000×$3.75/M + 4000×$0.3/M = 0.0357，与 CLI 同参数口径一致）。

Result: 通过（Infrastructure 16/16：Parses_session_shutdown_model_metrics_with_uncached_split、
Normalizes_copilot_internal_model_ids、Skips_malformed_lines_and_non_shutdown_events、
Parses_each_model_and_ignores_request_cost_and_empty_usage、Skips_lines_without_parseable_shutdown_timestamp_as_invalid、
Splits_cumulative_shutdowns_into_intervals_without_double_counting、
Splits_resumed_shutdowns_across_dates_with_conserved_totals、Keeps_one_entry_for_a_single_shutdown_snapshot、
Groups_intervals_per_session_and_model、Drops_zero_token_intervals_but_keeps_them_as_subtraction_baseline、
Merges_suffix_variants_into_one_model_group_for_differencing、
Prices_intervals_by_snapshot_pricing_at_interval_timestamp、
Discovers_session_state_files_without_nested_session_files、Copilot_home_environment_variable_is_respected、
Missing_home_yields_no_data、Incremental_rescan_picks_up_appended_snapshot_once）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/copilot/`（合成数据，覆盖：resume 跨日
   累计差分、同 id 重复 shutdown 去重、uncached 拆分、checkpoint 行、全零模型条目、-1m 后缀归并
   （gpt-5.1-1m → gpt-5.1）、session-state 嵌套目录不发现、坏 JSON 行）。
2. 录制 golden：`COPILOT_HOME=$PWD/$FX/copilot npx --yes ccusage@20.0.29 copilot daily --json --offline
   --timezone UTC > $FX/golden/copilot-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateCopilotAsync(CopilotRoot)`（适配器 → PricingEngine 内置快照
   → UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；
  模型清单两侧同（无 gpt-5.1-1m）；自研定价未命中清单为空。

Result: 通过——两侧全量 **input=28,200 / output=3,400 / cacheCreation=2,500 / cacheRead=7,500 /
total=41,600 tokens，totalCost=$0.1240625** 逐位一致；分日 2026-09-14（10,000/1,000/2,000/4,000/17,000，
cost 0.0537）与 2026-09-15（18,200/2,400/500/3,500/24,600，cost 0.0703625）逐位一致；per-model
（claude-sonnet-4 20,500/2,200/2,500/7,000、gpt-5.1 6,500/900/0/500、gpt-5-mini 1,200/300/0/0）逐位一致；
诊断 status=Partial（fixture 故意含 1 行坏 JSON）、malformed=1、invalid=0、records=5、UnpricedModels=∅。
（以上为 I-008 收口时数字；I-023 扩充 fixture 后已重录 golden，最新数字见 I-023 卡 TC-002。）

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_copilot"`。
2. 数据存在性门控：`CopilotLogLocator.CollectSessionStateFiles(null)` 非空才运行（COPILOT_HOME →
   ~/.copilot，与 CLI 子进程同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 copilot daily --json --offline --timezone UTC`（golden）
   vs `AggregateCopilotAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（容差比较器口径同 claude/codex/zcode 对账）。

Result: 本机（2026-10-11）**无 Copilot session-state 用量数据 → 测试按门控 Skip**（与 claude 门控同态）；
实测本机 CLI golden 输出 `daily: []`、totals 全 0（两侧全 0，即 0=0 的平凡一致）。门控链路本身已在
本机验证：以 `COPILOT_HOME=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与自研全链路对账
**通过**（`Real_copilot_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行该门控即得
真实数字。同轮 codex / zcode / dsh 门控对账原样通过（3 Passed），claude Skip，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/copilot/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 ../ccusage/rust/adapters/copilot：paths.rs 目录发现、parser.rs
parse_session_state_file、loader.rs reconcile_session_state_entries 的 session-state 主线；
蓝本许可 MIT License，Copyright (c) 2025 ryoppippi，声明保留在 CopilotLogLocator / CopilotSessionParser
文件头；fixtures 全部合成，无真实数据入仓）：

- **数据勘察勘误**：本机 `~/.copilot` 实测无 session-state/otel 用量数据（Background 勘误已注明），
  对账锚点改为"CLI golden 录制（COPILOT_HOME 钉合成 fixture）+ 数据存在性门控真实对账"双轨
  （TC-002/TC-003），与仓库 claude/codex fixture-golden 模式同构。
- **差分状态方案**（关键设计）：蓝本把全部 session-state 条目池化 → dedup → 按 (session, model) 分组 →
  相邻快照差分。本实现将差分放进**单文件解析**（CopilotSessionParser）：分组键含 session id
  （= events.jsonl 一级父目录名），一个数据根内会话目录唯一 ⇒ (session, model) 组不会跨文件，
  蓝本的跨文件池化在单根场景下与"文件内独立差分"**结果严格等价**（dedup key 亦含 session id，
  文件内去重 == 池化去重）。由此差分状态只在单文件解析内存活，跨扫描增量由共享
  IncrementalJsonlScanner 的 mtime/大小记账承担（变更文件整文件重解析），无跨扫描差分状态可失步
  （I-007"fold 状态单文件存活"同款论证）。适配器保持薄壳（DshSessionAdapter 同构）。
- **快照条目筛选**（蓝本口径逐条对齐）：modelMetrics 缺失/非对象 → 无快照；任一模型值非对象 →
  整表按缺失（蓝本 lenient_object 对 BTreeMap 的 all-or-nothing）；usage 缺失或非对象 → 跳过该模型；
  空模型名 / 全零用量且请求数为 0 → 跳过；全零但有请求数 → 保留（作差分基线参与后续减法）。
  lenient_u64 口径：仅非负整数字面量生效，浮点/字符串/负数一律 0。
- **dedup key**（蓝本 session_state_dedup_key）：事件带非空 id → `shutdown:{session}:{id}:{model}`；
  否则按蓝本字段序拼**原始累计用量**（含 reasoningTokens）+ 时间戳文本（RFC3339 毫秒）。
  去重保留时间戳最新的一条（相等取后出现者，蓝本 `<=` 判定），并保持原相对序。
- **成本落点**：记录 CostUsd 恒 null → PricingEngine 按区间快照时间戳计价（CLI
  calculate_cost_for_usage_at 的 cost-at-timestamp 口径）；cache_creation 无 5m/1h 细分时整体按
  cache write 价（CLI cache_creation: None 同口径）。定价快照无需扩充（验证见 Subtasks 定价条）。
- **管道接线**：`ICopilotSessionAdapter` 单例 + LocalIngestionDataSource 第五路 concat；
  对账链路 `UsageChainAggregator.AggregateCopilotAsync` 按 UTC 日分桶（CLI --timezone UTC 口径）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **otel 合并与 totalNanoAiu credit gap 未实现**，拆卡 I-023。理由：本机无 otel 行与 checkpoint
  数据可验（蓝本该块的正确性无法在本机对账背书）；蓝本该块约 800 行 Rust（四种记录形态、
  trace/response id 互斥优先级、取优合并、区间无关性），超出单卡合理范围；credit gap 为 cost-only
  （0 token）条目，不影响 token 对账与总量守恒。I-008 卡 Background 的分两步建议即为此预留。
- ② **零 token 区间不出记录**（蓝本 has_usage 还接受 request_count>0 的零 token 区间）：统一记录
  无请求数字段，零 token 记录对 token/成本聚合零贡献；副作用是 CLI daily 的 `messageCount` 字段
  自研侧不复现（对账不含该字段）。全零快照仍保留作差分基线，减法用原始累计值，无漂移。
  **对账后果**：真实机器上若某 (日期, 模型) 的区间全部为"零 token 但 request_count>0"，
  CLI 日报有 0-token 行而自研无行 → 门控对账将以 MissingInOurs **响亮失败**（非静默漂移）；
  实际 Copilot 数据中模型调用均带 token，极罕见，可接受。
- ③ **行级容错为仓库 I-004 口径而非蓝本静默跳过**：坏 JSON（须先过预过滤）计 MalformedLines；
  预过滤未命中 / 非目标事件 / checkpoint 行计 LinesSkipped；蓝本 records() 对反序列化失败的行
  静默丢弃且预过滤未命中不计数。
- ④ **shutdown/checkpoint 行缺可解析时间戳计 InvalidRecords**（蓝本静默 continue）：区间无法归日，
  丢弃并以诊断暴露（DSH 适配器口径偏差 ③ 同款）。
- ⑤ **usage_checkpoint 行不产出记录**（计 LinesSkipped）：checkpoint 只服务 credit 对账（totalNanoAiu），
  属 I-023 范围；token 主线不受影响。
- ⑥ **request_count / reasoning 不落入统一记录**：UsageRecord 无对应字段（reasoning 已含 output，
  request 只影响 CLI messageCount 展示）；no-id dedup key 仍按蓝本字段序含两者，保证键形状一致。
- ⑦ **无 since/until 日期过滤**：适配器全量出账，日期过滤/分桶由下游聚合承担（仓库五路适配器统一
  架构）；蓝本与 `--until` 相关的"取最新可见 shutdown""区间无关性"等逻辑随 otel 合并留在 I-023。
- ⑧ **时间戳解析为蓝本严格 RFC3339 的超集**：C# `DateTimeOffset.TryParse`（AssumeUniversal 口径，
  JsonlTimestamp 共享实现）会接受蓝本 parse_ts_timestamp 静默丢弃的行——空格分隔、微秒/纳秒精度、
  缺时区等——并出账；真实 Copilot 数据为 RFC3339 毫秒，仅极端输入下产生两侧差异，且以对账差异
  形式 fail-loud，不会静默漂移。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **313 通过 / 8 跳过**
（HEAD 基线 295/7 + 新增 16 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；`TOKENMETRY_RECONCILE_REAL=1` 开轮复跑门控套件：codex / zcode / dsh Passed、
claude / copilot Skip（无数据），无回归；门控 Copilot 对账以 COPILOT_HOME 钉 fixture 验证 Passed。
对账数字见 TC-002 / TC-003。

### 2026-10-11（独立审查修复）

独立审查结论 approve-with-nits（无 P0/P1；差分等价性成立、口径偏差全接受、otel 拆卡裁定合理），
提交前顺手修复的 P3 项：

- **[补测 ×2] dedup key 形状等价性角落**（`Keeps_same_event_id_across_different_session_directories`、
  `Deduplicates_reused_event_id_keeping_latest_timestamp`）：同事件 id 分属两个会话目录时两侧各留
  （key 含 session id，文件内差分 == 蓝本池化的等价性依赖此形状）；同 id 复用于不同时间戳只留最新
  （蓝本 `<=` 判定），差分基于留下的序列。
- **[注释] SaturatingAdd 负数分支**：唯一调用点两操作数经 ReadTokens 保证 ≥ 0，负数分支按当前
  调用图不可达，补注释说明（long.MaxValue 仅溢出兜底）。
- **[注释] 定位器 home 优先级表述修正**：原文"USERPROFILE 优先于 HOME，与 claude.rs / codex.rs
  同规则"不准确——蓝本 ccusage-core/home.rs 实为 **HOME 优先**；改为如实描述（沿用仓库共享
  JsonlHomePath 约定 USERPROFILE 优先，常规环境两者一致，仅两变量指向不同目录时有可观察区别）。
- **[卡片] 偏差②补对账后果**（零 token 区间在真实机器上会以 MissingInOurs 响亮失败而非静默漂移）、
  **偏差清单补 ⑧**（C# 时间戳解析为蓝本严格 RFC3339 的超集，极端输入 fail-loud）。
- **[I-023 卡] Background 补蓝本内联测试锚点、AC 补"完成后回勾 I-008 进阶 AC"**。
- 验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **315 通过 / 8 跳过**
  （新增 2 项 dedup key 形状测试）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-008-B1 | P3 | 工件枚举经 Directory.EnumerateDirectories 跟随符号链接；蓝本 readdir 亦不防 symlink 环（与 I-007-B2 同类） | deferred | 现实概率极低；后续在定位器跳过重解析点 / 加深度上限 |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/CopilotSession/`（CopilotSessionAdapter、CopilotSessionParser、
  CopilotLogLocator）、`src/Tokenmetry.Core/Abstractions/ICopilotSessionAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第五路）、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`（DI）、
  `tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第五适配器）、
  `tests/Tokenmetry.Reconciliation.Tests/`（CopilotFixtureReconciliationTests、RealDataReconciliationTests、
  Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/FixturePaths、
  CCUsageCli/CcusageGoldenFiles）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/copilot/`（合成 fixture）、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/copilot-fixture-daily-v20.0.29.json`（CLI 录制 golden +
  README 录制命令）、`docs/kanban/issues/I-023-copilot-otel-merge.md`（拆卡）
- How to run/verify: 全量 `dotnet build Tokenmetry.sln && dotnet test Tokenmetry.sln`；Copilot 单测
  `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CopilotSession"`；
  fixture 对账 `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~CopilotFixture"`；
  真实对账（有数据机器）`TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests
  --filter "FullyQualifiedName~Real_copilot"`（无数据自动 Skip）；golden 重录命令见
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`
- Results: 2026-10-11（含审查修复复验）— 构建 0 警告 0 错误；全仓 315 通过 / 8 跳过（基线 295/7 保留，
  新增 21 项测试：18 单测 + 2 fixture 对账 + 1 门控真实对账）；fixture 对账 tokens 五分量 + cost 两侧
  逐位一致（全量 input=28,200 / output=3,400 / cacheCreation=2,500 / cacheRead=7,500 / total=41,600，
  cost=$0.1240625，见 TC-002）；真实数据对账门控测试就绪，本机无 session-state 数据按门控 Skip
  （CLI golden 实测全 0），门控链路经 COPILOT_HOME 钉 fixture 全链路验证通过
