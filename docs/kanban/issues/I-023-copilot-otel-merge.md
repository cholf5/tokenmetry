---
id: I-023
title: Copilot CLI OpenTelemetry 合并与积分对账
type: feature
priority: P3
size: M
status: todo
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

- [ ] otel 四种记录形态解析 + 互斥去重（含 chat span 双导出计一次）单测（fixtures 全合成）
- [ ] otel token 拆分 / reasoning / total_tokens 回退与蓝本一致
- [ ] otel 行与 shutdown 区间取优合并（含"进程无 shutdown 结束的调用由 otel 补计"）单测 + fixture 对账
- [ ] credit gap `unknown` cost-only 条目（有 checkpoint 会话、区间无关性、豁免条件）单测
- [ ] `copilot daily` fixture golden（含 otel 与 checkpoint 数据）对账逐位一致
- [ ] 完成后**回勾 I-008 卡的进阶 AC**（"otel 行与 shutdown 区间取优合并"项）并更新其 Subtasks 末条

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

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 自 I-008 拆出（多源覆盖批次；I-008 实现时判定 otel 合并 + credit gap 超出单卡合理范围，
  且本机无 otel/checkpoint 真实数据可验——见 I-008 卡 Development Log"实现边界"节）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
