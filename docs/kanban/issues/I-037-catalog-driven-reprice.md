---
id: I-037
title: 目录驱动的重算（改价格即生效，不做查询期计价）
type: feature
priority: P1
size: M
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

用户在设置页改了价格 / 关联之后，界面上的成本必须跟着变——否则"有地方配价格"只是半个功能。

提案文档 `docs/plans/2026-10-15-model-registry-design.md` 原定方案是"把计价从采集期挪到查询期 + 拆 cost 来源"，因为担心"改价要全量重扫磁盘"。实施前核实发现该前提不成立，改用更便宜的方案。

## Background

**核实结论（推翻原方案）**：`IncrementalJsonlScanner` 每轮扫描返回**全量累积记录**（`_recordsByFile` 全量回放，只重解析变更文件，IncrementalJsonlScanner.cs:100），而补价侧表每轮都对所有 `CostUsd is null` 的记录重跑 `PriceRecord`（LocalIngestionDataSource.cs:217-224）。因此**触发一轮刷新即可全量重算**，不需要重扫磁盘、不需要查询期计价、也就不需要拆 cost 来源（那本是本卡最大的风险点与 schema 改动）。

于是本卡只做三件事：目录热更新（I-035 已具备）、保存后触发一轮刷新、成本页给缺价模型指路（缺价清单此前只能看 tooltip，无处可去）。

## Acceptance Criteria

- [x] 目录以单例共享给计价引擎与查询服务（`AppServices`），设置页持有的就是同一个实例
- [x] 目录 / 关联变更后触发一轮重算（复用 `BackgroundRefreshService.TriggerRefreshAsync`：与定时刷新同一单飞槽，正在跑则并入，不追加轮次）
- [x] 保存即时反馈（toast「已保存，正在按新价格重算…」）；刷新失败静默（价已落盘，下轮自然校正）
- [x] 成本分析页「待确认价格」卡补指路文案（设置 → 模型目录；不做跨页跳转）
- [x] 覆盖测试：保存触发刷新一次、非法输入不触发、目录热更新使计价来源切到 `UserOverride`

## Subtasks

- [x] App：`AppServices` 装配共享 `ModelCatalog`（用户层 = 新字段 + 旧字段折算）并注入计价引擎与查询服务
- [x] App：`SettingsViewModel.ApplyModelSettings` / `ApplyAliasSettings` → 落盘 + 目录热更新 + 重建表 + 触发重算
- [x] App：成本页 KPI 卡指路文案（Translations 中英）
- [x] 文档：提案文档 §3.3 回写为实际方案（含核实依据）

## Dependencies

- I-035（目录）、I-005（采集管道刷新入口）、S-002（设置页）

## Test Cases

### TC-001: 保存条目触发一轮重算

Steps: 构造带计数刷新动作的 VM；填写 ID / 价格并保存。

Expected: `settings.ModelDefinitions` 落一条（ID 归一为 `glm-4-6`）、落盘一次、刷新动作被调用一次、目录里该条目标记为已覆盖价格；表单回新建态（`SaveModelDefinition_CreatesEntryPersistsAndTriggersReprice`）。

### TC-002: 非法输入不落盘也不重算

Steps: ID 填 `bad#id`（或价格只填一半）后保存。

Expected: 行内错误文案、`ModelDefinitions` 仍空、落盘 0 次、刷新 0 次。

### TC-003: 改价后计价来源切换

Steps: 目录先按快照计价 `glm-4-6` → `UpdateUserDefinitions` 给该 ID 新价 → 重新计价。

Expected: `Source` 从 `Snapshot` 变为 `UserOverride`，成本等于新价（`UserDefinitionsUpdateTakesEffectWithoutRestart`）。

## Dev Log

- 2026-10-15：方案变更记录——原设想"查询期计价 + cost 来源拆分"因增量扫描器每轮全量回放而不必要；改为"采集期计价 + 变更触发一轮"。省掉的不只是工作量：facts 表无需加"未定价 token"列，与 ccusage 的对账口径（日志自带 costUSD 优先、facts 里的 cost_usd 语义）也完全不动。
- 已知边界：Pi / DSH 两条源的成本在**解析期**落定（Pi 的 identity cost 走注入引擎、DSH 走 `DeepSeekPricingCatalog` 历史规则），其未变更文件不重解析，故改了价对这两源的历史行要到相应文件变化后才更新——属既有增量语义，已在卡片遗留登记，不藏。
