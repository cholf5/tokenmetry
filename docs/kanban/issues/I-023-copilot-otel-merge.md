---
id: I-023
title: Copilot CLI OpenTelemetry 合并与积分对账
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

为 Copilot CLI 适配器（I-008）补齐蓝本 ../ccusage/rust/adapters/copilot 的两块进阶语义：
otel JSONL 行与 session-state shutdown 区间的取优合并，以及 session-state `totalNanoAiu`
积分缺口（credit gap）的 cost-only `unknown` 条目。

## Background

I-008 已完成 session-state 主线（累计快照差分、uncached input 拆分、后缀剥离、fixture
golden 对账逐位一致）。蓝本 README 与 loader.rs/parser.rs 中剩余两块语义当时判定超出
单卡合理范围且本机无数据可验（2026-10-11 实测 ~/.copilot 无 session-state/otel 用量数据），
从 I-008 拆出本卡：

- **otel 合并（loader.rs reconcile_session_state_entries 的 otel 分支 + parser.rs parse_otel_file）**：
  - 数据源：`${COPILOT_HOME:-~/.copilot}/otel/**/*.jsonl`（递归）与
    `COPILOT_OTEL_FILE_EXPORTER_PATH`（单文件），四种记录形态（chat span / inference log /
    agent turn log / agent summary span）按 trace/response id 优先级互斥去重；chat span 双导出
    （同 response id + 同 usage）计一次。
  - token 口径：`gen_ai.usage.input_tokens` 同样含缓存读写（拆分同 session-state）；仅当
    `gen_ai.usage.total_tokens` 显示 reasoning 另计时不双计（extra_total_tokens 回退）。
  - 合并规则：otel 行时间戳 ≤ 该 (session, model) 最新可见 shutdown（--until 口径）的被抑制；
    当 otel 对某对的调用数多于 shutdown 请求数差分（进程无 shutdown 结束的调用只存在于 otel）时，
    从第一个有 otel 行的 shutdown 区间起由 otel 接管；比较覆盖全部 shutdown，与日期范围无关。
  - 有 otel 行的会话不再产出 credit gap 条目。
- **credit gap（loader.rs credit_gap_entries）**：`modelMetrics` 只覆盖运行中进程，session 级
  `totalNanoAiu`（1 AIU = $0.01）覆盖全会话；有 checkpoint 的会话中，每快照"session 总额 −
  已归因模型积分"持续未被解释的部分成为 cost-only `unknown` 条目（cost = nanoAiu / 1e11）。
  无 checkpoint（旧版 Copilot resume 重置双计数）、shutdown 缺任一总额、或有 otel 行的会话不产出。

## Acceptance Criteria

- [x] otel 四种记录形态解析 + 互斥去重（含 chat span 双导出计一次）单测（fixtures 全合成）
  - ✅ 2026-10-11：`CopilotOtelParser`（蓝本 parser.rs otel 块逐条移植）——四形态判别按蓝本
    if/else 链保真（chat span → inference log → agent turn → agent summary；span 判别 type="span"
    或 name+span 字段启发式）、trace 上下文（同 traceId 首个非空模型属性 + 会话属性优先级
    max_by_key 尾部胜出）、CandidateSets 互斥抑制 + chat span 重导出折叠（保留时间戳更早者）；
    单测 Parses_copilot_chat_spans / Suppresses_lower_priority_records_for_same_response /
    Counts_a_re_exported_chat_span_once / Keeps_calls_of_one_turn_that_share_a_response_id
- [x] otel token 拆分 / reasoning / total_tokens 回退与蓝本一致
  - ✅ Splits_otel_input_tokens_like_session_state（两源拆分同构）、Does_not_double_count_reasoning_tokens
    （无 total 属性不双计）、Falls_back_to_total_tokens_when_copilot_parts_are_missing（total 兜底补
    output）；total 显示 reasoning 另计时蓝本产 extra 分量——内部条目保真（Includes_separate… 断言
    extra=5），统一记录丢弃（桌面无 extra 字段，见偏差③）
- [x] otel 行与 shutdown 区间取优合并（含"进程无 shutdown 结束的调用由 otel 补计"）单测 + fixture 对账
  - ✅ `CopilotUsageReconciler`（蓝本 reconcile_session_state_entries + otel_replacement_start 逐条移植）：
    Reports_otel_calls_of_a_process_that_ended_without_a_shutdown（7 otel 行接管、区间全抑制）等
    9 项合并单测 + fixture session-merge / session-retained 场景，golden 对账逐位一致
- [x] credit gap `unknown` cost-only 条目（有 checkpoint 会话、区间无关性、豁免条件）单测
  - ✅ cost = nanoAiu/1e11 decimal 精确（0.51m / 10.72m / 0.20m 型断言）；持续未解释（后缀最小）
    逐快照出账；豁免三条件各有测试（无 checkpoint / 缺总额 / 有 otel 行）；蓝本 since/until 用例
    在桌面全量口径下塌缩（见偏差④）
- [x] `copilot daily` fixture golden（含 otel 与 checkpoint 数据）对账逐位一致
  - ✅ fixtures/copilot 扩充 otel/merge.jsonl + 7 个积分场景会话，golden 重录（CLI 20.0.29，
    COPILOT_HOME 钉 fixture），tokens 五分量 + cost 全粒度逐位一致（见 TC-002）
- [x] 完成后**回勾 I-008 卡的进阶 AC**（"otel 行与 shutdown 区间取优合并"项）并更新其 Subtasks 末条
  - ✅ I-008 卡已回勾（2026-10-11）

## 蓝本内联测试锚点（`../ccusage/rust/adapters/copilot/src/`，grep `#[test]` 可复核）

实现与单测须逐条对照以下蓝本内联测试（loader.rs / parser.rs / paths.rs）：

- otel 记录解析与去重（parser.rs）：`parses_copilot_chat_spans`、`suppresses_lower_priority_records_for_same_response`、
  `counts_a_re_exported_chat_span_once`、`keeps_calls_of_one_turn_that_share_a_response_id`、
  `does_not_double_count_reasoning_tokens`、`includes_separate_otel_reasoning_tokens_in_total_and_cost`、
  `splits_otel_input_tokens_like_session_state`、`falls_back_to_total_tokens_when_copilot_parts_are_missing`
- otel × shutdown 取优合并（loader.rs）：`splits_cumulative_shutdowns_into_intervals_and_keeps_unmatched_otel_rows`、
  `uses_shutdown_snapshot_as_of_until_for_otel_reconciliation`、`subtracts_the_pre_since_shutdown_before_retaining_resumed_otel_rows`、
  `reports_otel_calls_of_a_process_that_ended_without_a_shutdown`、`otel_reconciliation_does_not_depend_on_the_date_range`、
  `keeps_shutdown_usage_from_before_the_otel_export_started`、`keeps_shutdown_intervals_when_otel_has_no_calls_they_miss`、
  `does_not_double_count_calls_a_concurrent_process_shuts_down_with`、`leaves_shutdowns_without_request_counts_to_the_shutdown_breakdown`
- credit gap（loader.rs / parser.rs）：`reports_session_credits_missing_from_model_metrics`、
  `reports_checkpoint_credits_of_sessions_without_a_shutdown`、`reports_resumed_session_credits_when_the_last_shutdown_has_no_model_metrics`、
  `subtracts_credit_snapshots_before_since`、`does_not_count_credits_that_a_later_shutdown_attributes`、
  `credit_gaps_do_not_depend_on_until`、`leaves_sessions_without_checkpoints_unchanged`、
  `leaves_sessions_with_incomplete_credit_totals_unchanged`、`leaves_credit_gaps_to_otel_when_a_session_has_otel_rows`、
  `calculate_mode_does_not_price_credit_only_entries`、`parses_credit_totals_from_checkpoints_and_shutdowns`、
  `marks_shutdown_credits_unknown_when_a_total_is_missing`
- 文件发现（paths.rs）：`discovers_session_state_and_otel_files_without_nested_session_files`、
  `discovers_files_from_copilot_home`（otel/ 递归 + `COPILOT_OTEL_FILE_EXPORTER_PATH` 单文件去重）

## Dependencies

- I-008（session-state 主线已收口）

## Test Cases

### TC-001: otel 解析 / 取优合并 / credit gap 语义（对照蓝本内联测试锚点）

Steps:
1. 测试内临时目录合成 session-state + otel fixture（四形态记录 / trace 与 response id 互斥 /
   chat span 重导出 / [秒,纳秒]·标量·timeUnixNano 时间戳 / mtime 回退 / 坏行与未归类行 /
   进程崩溃接管 / 并发进程 shutdown / 缺请求数 shutdown / checkpoint 积分快照 / 缺总额豁免 /
   otel 行豁免），运行 `CopilotSessionAdapter.ScanAsync`（必要时直测 `CopilotOtelParser` /
   `CopilotSessionParser`）断言记录、诊断与合并行为（蓝本 31 个内联测试锚点逐一对齐，
   since/until 用例按桌面全量口径塌缩——见 TC-001 偏差说明与卡片 Dev Log）。

Expected:
- otel：chat span（trace:span dedup key）保留；同 response id 的 inference log / agent summary
  抑制、同 trace 亦然；双导出（同 response + 同用量八元组）计一次保留更早者；同 turn 共享
  response id 按用量区分全保留；uncached input 拆分与 session-state 同构；total_tokens 兜底补
  output；reasoning 另计产生 extra（内部条目保真、统一记录丢弃）；顶层级非对象 / attributes
  非对象计 malformed，预过滤未命中 / 未归类 / 零用量计 skipped；mtime 兜底时间戳。
- 合并：otel 调用数 > shutdown 请求数差分 → 从第一个有行区间起接管（区间全抑制、otel 行全保留，
  含无 shutdown 结束进程的调用）；否则仅保留最新 shutdown 之后的行；任一快照缺请求数不接管；
  比较覆盖全部 shutdown（与日期范围无关）；并发进程双 shutdown 不双计。
- credit gap：shutdown 后 checkpoint 的持续未解释份额逐快照出 cost-only unknown 条目
  （0 token、cost = nanoAiu/1e11 decimal 精确、LogCost 权威）；checkpoint-only 会话全额；
  无 checkpoint / shutdown 缺总额（含模型侧未全报）/ 有 otel 行豁免；积分快照 first-wins 去重
  （同事件 id 保留首个，与 token 快照 latest-wins 相区别）。

Result: 通过（Infrastructure 35/35：CopilotOtelMergeTests 35 项，蓝本锚点同名场景逐条对齐 +
容错/时间戳/发现/增量角落；详见 Verification）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/copilot/`（合成数据；I-008 主线
   覆盖 resume 跨日累计差分、同 id 重复 shutdown 去重、uncached 拆分、全零模型条目、-1m 后缀
   归并、session-state 嵌套目录不发现、坏 JSON 行；I-023 增补 otel/merge.jsonl——四形态互斥去重、
   双导出计一次、同 turn 多调用、进程崩溃接管、最新 shutdown 后保留/前抑制——与 8 个场景
   会话目录（session-merge / session-retained / session-credit / session-exempt /
   session-incomplete / session-otelgap / session-cponly / session-resumegap））。
2. 录制 golden：`COPILOT_HOME=$PWD/$FX/copilot npx --yes ccusage@20.0.29 copilot daily --json
   --offline --timezone UTC > $FX/golden/copilot-fixture-daily-v20.0.29.json`（命令入仓
   golden/README.md；I-023 扩充后已重录）。
3. 自研链路：`UsageChainAggregator.AggregateCopilotAsync(CopilotRoot)`（适配器 → PricingEngine
   内置快照 → UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；
  模型清单两侧同（含 credit gap 的 unknown 条目，无 gpt-5.1-1m）；自研定价未命中清单为空。

Result: 通过——两侧全量 **input=31,614 / output=3,750 / cacheCreation=2,500 / cacheRead=7,500 /
total=45,364 tokens，totalCost=$0.1723295** 逐位一致；分日 2026-09-14（10,000/1,000/2,000/4,000/
17,000，cost 0.0537）、2026-09-15（18,200/2,400/500/3,500/24,600，cost 0.0703625）、
2026-09-16（3,411/347，含 otel 接管行、互斥去重行、credit gap 首日 0.013）、2026-09-17（3/3，
credit gap 0.015）、2026-09-18（0 token，credit gap 0.005——cost-only 条目）逐位一致；
per-model（claude-sonnet-4 23,814/2,540/2,500/7,000、gpt-5.1 6,600/910/0/500、gpt-5-mini
1,200/300/0/0、unknown 0/0/0/0 + cost 0.033）逐位一致；诊断 status=Partial（fixture 故意含
1 行坏 JSON）、malformed=1、invalid=0、records=27（区间 10 + otel 13 + gap 4）、UnpricedModels=∅。

## Development Log

### 2026-10-11

- 自 I-008 拆出（多源覆盖批次；I-008 实现时判定 otel 合并 + credit gap 超出单卡合理范围，
  且本机无 otel/checkpoint 真实数据可验——见 I-008 卡 Development Log"实现边界"节）。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 ../ccusage/rust/adapters/copilot：paths.rs otel 收集、parser.rs
parse_otel_file + parse_session_state_file 积分段、loader.rs reconcile_session_state_entries +
credit_gap_entries + otel 保留过滤；蓝本许可 MIT License，Copyright (c) 2025 ryoppippi，声明
保留在 CopilotLogLocator / CopilotSessionParser / CopilotOtelParser / CopilotUsageReconciler
文件头；fixtures 全部合成，无真实数据入仓）：

- **五件结构**（`src/Tokenmetry.Infrastructure/CopilotSession/`）：`CopilotLogLocator` 补 otel 目录
  递归收集（蓝本 collect_files_with_extension：扩展名 Ordinal 精确匹配、读不到子目录静默跳过）
  与 `COPILOT_OTEL_FILE_EXPORTER_PATH` 单文件（非空去空白且确为文件才生效），清单按路径去重
  排序（蓝本 add_file seen + 末尾 sort_by）；`CopilotOtelParser`（新）逐文件四形态解析 + 行内
  互斥去重；`CopilotSessionParser` 扩展积分快照（data.totalNanoAiu lenient_f64 + 模型侧
  totalNanoAiu + models_report_credits 过滤；checkpoint 带总额出积分快照、无总额跳过）；
  `CopilotUsageReconciler`（新）纯函数合并；`CopilotSessionAdapter` 接线（增量记账见下）。
- **合并落位（关键设计）**：蓝本把全部 session-state 条目池化 → dedup → (session, model) 分组 →
  差分 + otel 接管判定 + 归因积分累计。桌面把差分从 I-008 的"单文件解析"移至
  `CopilotUsageReconciler` 对池化快照整体重算（I-008 的等价性论证不变：分组键含 session id、
  组不跨文件，池化差分 == 文件内差分；且这更贴近蓝本原貌）。适配器自有 per-file（mtime/大小）
  增量记账（语义镜像共享 IncrementalJsonlScanner：打不开计 LinesSkipped 不落账下次重试、变更
  文件整文件重解析），不复用共享引擎——合并需要跨文件池化的中间记录（快照请求数 / nanoAiu /
  积分快照），统一记录 UsageRecord 无对应字段可承载（共享引擎仅缓存统一记录）。合并是纯函数，
  每次扫描对当前池重算，无跨扫描状态可失步。
- **otel 合并语义落法**：otel 行按 (session, model) 池化调用时间戳（毫秒域二分，蓝本
  partition_point 同构）；`otel_replacement_start` 逐条对照蓝本（任一快照 request_count==0 →
  不接管；逐 shutdown 累计 otel 调用增量 vs 请求数差分，otel > reported 才接管，起点 = 第一个
  有行区间）；接管时 otel_after 不设（otel 行全保留、区间全抑制），否则 otel_after = 组内最后
  一条快照（无 --until 时蓝本 latest_visible 退化为最后一条），otel 行保留严格晚于 otel_after
  者。归因积分按 (session, shutdown 毫秒) 累计差分后模型 nanoAiu——在接管/产出筛选之前完成
  （蓝本同序：全部 shutdown 都参与积分归因）。产出顺序蓝本化：区间（(时间戳, dedup key) 排序）
  → otel（文件序）→ gaps，最终按时间戳稳定排序。
- **credit gap 落统一记录方式**：cost-only `unknown` 条目直接落 `UsageRecord`（Model="unknown"、
  四桶全 0、`CostUsd = nanoAiu / 1e11` decimal 精确换算——从 long 构造不经 double，Grok ticks
  先例同款；1 AIU = $0.01、1e9 nano/AIU），定价引擎 LogCost 短路采信（桌面无 calculate 模式，
  该成本即权威）；MessageId/RequestId 恒 null、无请求数字段（统一记录口径不变）。
- **积分快照语义**（蓝本逐条）：checkpoint 缺总额不产出任何快照；shutdown 恒产出（总额可空）；
  任一"参与上报"模型（有 usage 且非全零）缺模型侧 totalNanoAiu → 总额按缺失（不可对账）；
  modelMetrics 表结构性失败（任一值非对象）→ 空表（不视为"未上报"，总额仍取会话值）；
  快照去重 first-wins（蓝本 seen.insert，与 token 快照 latest-wins 相区别）；无事件 id 的
  dedup key 含 Option Debug 形状（`credits:{session}:{is_shutdown}:{millis}:Some(N)/None`）。
- **行级容错**（仓库 I-004 口径显式化，蓝本静默跳过）：otel 行——顶层级非对象 / attributes 键
  存在但非对象（serde 结构性失败）计 MalformedLines；预过滤未命中（无 "attributes" 字面量）、
  attributes 缺失、未归类四形态、总用量为 0 计 LinesSkipped；InvalidRecords 恒 0（时间戳有
  mtime 兜底）。session-state 侧——无总额 checkpoint 计 LinesSkipped（带总额 checkpoint 自本卡
  起消费、不计跳过；I-008 旧断言随语义更新）。
- **fixture/golden**：fixtures/copilot 增 otel/merge.jsonl（17 行：接管 6 调用 + 保留/抑制 +
  去重互斥 9 行 + otel 豁免 1 行）与 7 个积分场景会话；golden 以 CLI 20.0.29 重录（TC-002 数字
  全部更新）；场景级数字先经 CLI 逐场景二分核验（sA/sB/sC 隔离 fixture 实跑 CLI 确认接管 /
  保留 / 去重口径）再入 golden，两侧逐位一致。门控真实对账就绪：`HasCopilotData` 扩为
  CollectSourceFiles 非空（session-state 或 otel 任一），COPILOT_HOME 钉 fixture 全链路
  （真实 CLI 子进程 vs 自研）验证 Passed。

与蓝本的口径偏差清单（全部显式列出；I-008 偏差①—⑧中①已由本卡消除，②⑥⑦⑧延续并细化）：

- ① **（已消除）otel 合并与 credit gap 已实现**——I-008 拆卡至此收口。
- ② **零 token 区间不出记录**（I-008 偏差②延续；蓝本 has_usage 含 request_count>0 的零 token
  区间）：统一记录无请求数字段。credit gap 条目不受此限（成本来自积分而非 token，成本即产出
  意义）；零 token 但有请求的 shutdown 区间仍丢弃（对账后果同 I-008：真实机器上将以
  MissingInOurs 响亮失败，非静默漂移）。
- ③ **otel extra_total_tokens 分量丢弃**：蓝本当 total_tokens 显示 reasoning 另计时把缺口补入
  extra 并计入 CLI totalTokens 与计价 output；桌面统一记录无 extra 字段——分量在内部条目保真
  （单测断言 extra=5），落记录丢弃。**对账后果**：真实机器上若 otel 行存在"total > 四桶和"
  （reasoning 另计）数据，CLI totalTokens/成本将高于自研 → 差异响亮暴露（非静默漂移）；fixture
  规避该形态（I-024 AG-1 欠账同族，桌面统一记录增加 extra 字段时可补齐）。
- ④ **since/until 相关逻辑塌缩为全量口径**（I-008 偏差⑦延续）：适配器全量出账、下游分桶——
  蓝本 `uses_shutdown_snapshot_as_of_until_for_otel_reconciliation`（otel_after 取 --until 内
  最新可见 shutdown）、`subtracts_the_pre_since_shutdown_before_retaining_resumed_otel_rows`
  （区间照差分、行按 since 过滤）、`subtracts_credit_snapshots_before_since` / 
  `credit_gaps_do_not_depend_on_until`（缺口逐快照出账、早于 since 的份额被吸收进后续快照）在
  桌面退化为"最新 shutdown = 全部 shutdown 的最后一条、全部快照出账"；无日期参数时蓝本行为与
  桌面一致（全量出账 == 蓝本不带 since/until 的运行）。塌缩后的等价性质有对应单测
  （Suppresses_otel_rows_between_shutdowns_against_the_latest_shutdown /
  Subtracts_the_pre_resume_shutdown_before_retaining_resumed_otel_rows /
  Emits_each_snapshots_persistent_unexplained_share_in_full_range /
  Otel_reconciliation_comparison_covers_all_shutdowns）。
- ⑤ **calculate 模式不重价 credit gap**（蓝本 calculate_mode_does_not_price_credit_only_entries）：
  桌面无 calculate 模式，LogCost 短路即权威（Credit_gap_cost_is_logcost_authoritative 断言）。
- ⑥ **request_count / reasoning 不落统一记录**（I-008 偏差⑥延续）：快照条目内部携带请求数与
  nanoAiu（接管比较与积分归因依赖），区间/otel 记录不携带；no-id dedup key 仍按蓝本字段序含
  reasoning 与请求数，保证键形状一致。
- ⑦ **otel 时间戳解析为蓝本超集**（I-008 偏差⑧同族）：标量时间戳按数量级猜测刻度（蓝本同式），
  RFC3339 行由共享 JsonlTimestamp 宽松解析；极端输入 fail-loud。另：超出可表示域的毫秒值蓝本
  钳位 i64::MAX（jiff 可表示），C#（DateTimeOffset 上限 ~9999 年）返回 null 落下一时间戳来源
  直至 mtime 兜底——钳位 vs 丢弃，同一 fail-loud 族（`CopilotOtelParser.FromMillis`）。
- ⑧ **差分位置从单文件解析移至适配器级合并器**：可观察行为与 I-008 等价（等价性论证见上），
  I-008 卡"差分状态只在单文件解析内存活"的表述由"合并对当前池整体重算（纯函数）+ 文件级
  增量记账"取代——蓝本 load_entries_inner 本就是每次调用全量重算。
- ⑨ **otel 行跨文件不去重**（蓝本口径）：互斥去重与重导出折叠均在单文件内（parse_otel_file
  局部集合）；跨文件同 dedup key 的 otel 行两侧各留（蓝本同）；MessageId 恒 null，蓝本
  message.id=dedup_key 不复现（桌面管道无消费方）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **793 通过 / 22 跳过**
（基线 757/22 + 新增 36：Infrastructure CopilotOtelMergeTests 35 项 + Reconciliation 场景测试
1 项；fixture 对账 3 项全绿含重录 golden）。`TOKENMETRY_RECONCILE_REAL=1` 门控轮：Real_copilot
本机 Skip（无数据，与 I-008 同态）、其余门控 3 Passed / 16 Skipped 无回归；COPILOT_HOME 钉
fixture 时 Real_copilot Passed（真实 CLI 子进程 vs 自研全链路，含 otel 与 credit gap 数据）。

### 2026-10-12（独立审查修复）

独立审查结论 request-changes（唯一阻塞项一个 P1；语义面四形态/合并/credit gap/31 锚点/9 偏差/
golden 全部核对通过，修复后无需重审）。修复包：

- **[P1] 池化索引器崩溃**（`CopilotSessionAdapter`）："发现但从未成功解析"的文件（TryGetFileStamp
  失败 continue 的；首扫打不开 / 读中途失败 Interrupted 不落账的）没有字典条目，池化用
  `_sessionStateFiles[file.Path]` / otel 同款索引器 → KeyNotFoundException 冲出 ScanAsync 崩掉
  整次刷新；`COPILOT_OTEL_FILE_EXPORTER_PATH` 可指向任意路径（无读权限 / 被轮转），条件持续
  存在时每次扫描都崩。修复：池化改 TryGetValue，缺失文件以空 parse 参与（共享
  IncrementalJsonlScanner 的"不贡献记录"语义）。回归测试
  `Unreadable_otel_exporter_file_is_skipped_without_breaking_the_scan`（导出变量指向 chmod 000
  文件）：不崩溃、该文件计 LinesSkipped 不计 FilesRead、其余数据正常出账、失败条件持续存在时
  重复扫描同样安全。
- **[P3] 清理 ×3**：CopilotSessionParser 删除死引用 using SqliteSource；卡片偏差⑦补
  `FromMillis` 钳位（i64::MAX）vs 丢弃（null 落下一来源直至 mtime）登记；并发进程测试注释措辞
  更正（逐 shutdown 比较 otel 调用增量 1+2+0=3 vs 请求数差分 1+0+2=3，first-1 同时计入两侧）。
- 验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **794 通过 / 22 跳过**
  （新增 1 项 P1 回归测试）；COPILOT_HOME 钉 fixture 门控 Real_copilot Passed。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/CopilotSession/`（CopilotLogLocator、CopilotSessionParser、
  **CopilotOtelParser（新）**、**CopilotUsageReconciler（新）**、**CopilotSourceRecords（新，中间记录）**、
  CopilotSessionAdapter）、`src/Tokenmetry.Core/Abstractions/ICopilotSessionAdapter.cs`（文档注释）、
  `tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotOtelMergeTests.cs`（新，35 项）、
  `tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs`（无总额 checkpoint
  断言随语义更新）、`tests/Tokenmetry.Reconciliation.Tests/CopilotFixtureReconciliationTests.cs`
  （3 项，含场景级断言）、`tests/Tokenmetry.Reconciliation.Tests/Support/UsageChainAggregator.cs`
  （ChainAggregation 增 Records）、`tests/Tokenmetry.Reconciliation.Tests/Support/RealReconciliationEnvironment.cs`
  （HasCopilotData 扩 otel 数据源）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/copilot/`
  （otel/merge.jsonl + 7 个积分场景会话）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/
  copilot-fixture-daily-v20.0.29.json`（重录）+ `golden/README.md`（覆盖说明）
- How to run/verify: 全量 `dotnet build Tokenmetry.sln && dotnet test Tokenmetry.sln`；Copilot 单测
  `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj --filter
  "FullyQualifiedName~Copilot"`；fixture 对账 `dotnet test tests/Tokenmetry.Reconciliation.Tests
  --filter "FullyQualifiedName~CopilotFixture"`；golden 重录命令见
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`；门控真实对账（有数据机器）
  `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter
  "FullyQualifiedName~Real_copilot"`（无数据自动 Skip）
- Results: 2026-10-11 — 构建 0 警告 0 错误；全仓 **793 通过 / 22 跳过**（基线 757/22 保留，
  新增 36 项：35 单测 + 1 场景对账测试）；fixture 对账 tokens 五分量 + cost 两侧逐位一致
  （全量 input=31,614 / output=3,750 / cacheCreation=2,500 / cacheRead=7,500 / total=45,364，
  cost=$0.1723295，per-model 含 unknown 0.033，见 TC-002）；门控真实对账就绪，本机无
  session-state/otel 数据按门控 Skip，COPILOT_HOME 钉 fixture 全链路验证 Passed。
  2026-10-12（独立审查修复复验）— 构建 0 警告 0 错误；全仓 **794 通过 / 22 跳过**（新增 P1
  回归测试 Unreadable_otel_exporter_file_is_skipped_without_breaking_the_scan）；fixture 对账
  数字不变；COPILOT_HOME 钉 fixture 门控 Real_copilot 复验 Passed
