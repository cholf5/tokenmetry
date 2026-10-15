---
id: I-036
title: provider 三元组（agent × model × provider）落库与查询
type: feature
priority: P1
size: L
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

用量事实此前只有 agent × model × project 三维：**"这次请求由谁服务"这一路信息在适配器里读完就丢**（ZCode `provider_id`、Goose `provider_name`、Hermes `provider_raw`、Kilo `provider_id`、OpenClaw `provider`、Droid `providerLock` 都读出来只用于计价候选链）。同一模型经不同 provider（z.ai / openrouter / 自定义源）使用因此在统计里混成一行——而不同 provider 的价目可能不同，混行则成本不可解释。

本卡把 provider 落成 facts 的第一等维度，并推理出"适配器没给"时的来源。

## Background

- **不设硬门槛**：约 6 类源有真 provider 字段，其余源只能用模型名前缀推导（`openrouter/x`、`ark/glm-...`——`PricingModelMatcher` 本来就在剥这个前缀做 provider 限定匹配）。两种都拿不到就是空串 = **未检测**，界面显示"—"；绝不按模型名猜厂商（那会与"模型身份上的 Vendor"混淆，见 I-035）。强制用户补全才能统计会把"打开就知道花了多少钱"变成"打开先填表"。
- **两个概念不共用一个词**：行级 Provider = 这次请求由谁服务（本卡）；条目级 Vendor = 这个模型属于哪个厂商（I-035 的 `ModelDefinition.Vendor`）。
- 主键变更：SQLite 不支持 ALTER 主键，migration 2 建新表 + 搬数据 + 换名（数据可由采集重建，但搬一次比丢一次便宜）；旧行 provider 回填空串，语义正确。
- 模型名索引同时被身份解析复用：`ExtractRequestedProvider` 从私有改为公开（I-035 的候选名推导与 I-036 的 provider 推导同源，两处口径必须是一个实现）。

## Acceptance Criteria

- [x] `UsageRecord.Provider` + `UsageFact.Provider`（空串 = 未检测；CLI 分解行恒空）
- [x] `UsageFactsMapper` 把 provider 并入 full 粒度分组键（同模型不同 provider 分行），并在记录没有 provider 时由模型名前缀推导
- [x] migration 2：`usage_facts` 加 provider 列 + 主键改为 (day, grain, agent, model, provider, project)，旧库搬数据不丢
- [x] 六个有 provider 观测的源补录：ZCode / Droid / Goose / Hermes / Kilo / OpenClaw
- [x] 查询侧：模型维度行汇总 provider（唯一值给名字，多值给计数，无观测为 0）；Agent / 项目维度恒不报
- [x] 模型统计页模型列加服务商副行（无观测时整行不渲染，列高不变）

## Subtasks

- [x] Core：`UsageRecord.Provider`、`UsageFact.Provider`、`UsageFactsMapper.ResolveProvider`、`PricingModelMatcher.ExtractRequestedProvider` 公开
- [x] Infrastructure：migration 2（建表 / 搬数据 / 换名 / 重建索引）、upsert 与 select SQL、读行映射
- [x] Infrastructure：ZCode / Droid / Goose / Hermes / Kilo / OpenClaw 六源补 provider
- [x] Core：`DimensionUsageRow.Provider` / `ProviderCount` + 维度聚合的 provider 汇总
- [x] App：模型页副行（`ProviderText` / `HasProvider`）+ 中英文案

## Dependencies

- I-002（facts 表与迁移框架）、I-024（候选计价：provider 的既有用途）、I-035（身份层）

## Test Cases

### TC-001: 同模型不同 provider 分行（存储层往返）

Steps: 同一 day / agent / model / project 写两行，provider 分别空串与 `openrouter`。

Expected: 两行都在，各自成本正确（`Upsert_usage_facts_splits_rows_by_provider`）。

### TC-002: 旧库升级不丢数据

Steps: 手工建只应用过 migration 1 的库（无 provider 列）+ 一行 full 事实 → `InitializeAsync`。

Expected: 行保留、`CostUsd` 逐位一致、`Provider` 为空串（未检测）；升级后该库仍可按新主键写入新行（`ProviderMigrationUpgradesV1DatabaseAndBackfillsEmptyProvider`）。

### TC-003: provider 推导优先级

Steps: 三条记录——适配器给了 provider / 没给但模型名带前缀 / 都没有。

Expected: 依次落 `zai` / `openrouter`（前缀推导）/ 空串（`ToUsageFacts_SplitsRowsByProviderAndInfersFromModelPrefix`）。

### TC-004: 模型维度 provider 汇总

Steps: 种入 zai + openrouter 两个 provider 的同一模型、单 provider 模型、无 provider 模型。

Expected: 多 provider 行 `Provider=""`、`ProviderCount=2` 且 token 合计不受影响；单 provider 行给名字；无观测为 0；Agent / 项目维度恒 0（`ModelProviderBreakdownTests`）。

## Dev Log

- 2026-10-15：实施完成。迁移测试断言从"只有 migration 1"改为 `[1, 2]` 并新增 v1 升级用例。
- 遗留：**模型维度行未按 provider 拆分**（当前是"汇总展示 + facts 已分行"）。拆分需 `session_facts` 同步加 provider 列（会话数 / 关联 Agent 才能按新粒度归属），属查询层后续改动；facts 已具备三元组，届时无需再动 schema。
