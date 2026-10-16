---
id: I-038
title: 目录迁移与对账护栏（旧字段兼容 + 信任锚边界）
type: refactor
priority: P2
size: S
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

让这套改动在既有用户与既有对账纪律下都站得住：老 `settings.json` 里的价格覆盖不能丢；模型目录（用户可编辑的价目）不能污染与 `ccusage` 的对账锚。

## Background

- **旧字段兼容桥**：`AppSettings.PricingOverrides` 保留但只读不写——装载时经 `ModelDefinitionOverride.FromLegacy` 折算成目录条目，沿用它原有的整条剔除口径（模型名空白 / 任一单价为负即丢），新旧同 ID 时以新字段为准（用户已在设置页编辑过）。字段留到下一版再删。
- **对账锚边界**：内置快照仍是离线兜底与对账基准，且**目录的用户层不参与对账测试**——`Reconciliation.Tests` 走的是各源适配器 + 默认引擎（无用户条目），用户改价只影响他自己的本地数字，这是"用户覆盖"的本意，也是信任锚不被稀释的前提。
- 复核：migration 1 未改动（顺序迁移纪律）；migration 2 是新增（provider 维度），已发布迁移一律只追加。

## Acceptance Criteria

- [x] 旧 `PricingOverrides` 折算进目录（同 ID 以新字段优先），字段注释标明只读桥与删除时机
- [x] `ModelPricingOverride` 类型保留（仅作为旧字段的类型 + 兼容桥输入），`PricingEngine.UpdateOverrides` 保留为桥接入口
- [x] 删除生产路径已不使用的 `BuiltinPricingSnapshot.TryMatch`（两处测试改走目录断言）
- [x] 全量测试绿：Core 157 / Infrastructure 654 / Reconciliation 68 / App 118 / Headless 266
- [x] 看板卡片与提案文档与实现一致（含方案变更说明）

## Subtasks

- [x] Core：`ModelDefinitionOverride.FromLegacy` + `PricingEngine` 构造/UpdateOverrides 的桥接
- [x] App：`AppServices` 折算旧字段（含新旧同 ID 去重）
- [x] 清理：`BuiltinPricingSnapshot.TryMatch` 删除；`PricingEngineTests` / `DshSessionAdapterTests` 改断言
- [x] 文档：`docs/plans/2026-10-15-model-registry-design.md` 状态与 §3.3 回写

## Dependencies

- I-035 / I-036 / I-037 / S-003（本批其余四卡）

## Test Cases

### TC-001: 旧覆盖规则仍生效

Steps: `new PricingEngine(overrides: [ { Model = Sonnet46, Input = 9, Output = 90 } ])` → 计价。

Expected: `Source=UserOverride`、成本 99（1M in + 1M out）——与改动前逐位一致（`UserOverrideBeatsSnapshot`）；非法规则（空白名 / 负价）整条剔除（`InvalidOverridesAreIgnored`）。

### TC-002: 对账不受用户层影响

Steps: 跑 `Reconciliation.Tests` 全部 fixture 对账。

Expected: 与 ccusage 锚点的既有对账结果不变（49 通过 / 19 个需真实本地日志的用例跳过）。

## Dev Log

- 2026-10-15：实施完成。全量测试绿。
- 2026-10-16（审查修正，本卡复核项的落实）：migration 2 改为整表重建不搬数据（原实现升级后双算，详见 I-036 Dev Log），"已发布迁移一律只追加"复核结论随之细化为迁移纪律两条，落在本库 `Migrations` 列表旁注释块与 AGENTS.md。本卡 TC-001 只覆盖引擎直连路径的缺口由 `SettingsViewModel` 装配层测试补上（旧字段经 `ModelCatalog.MergeUserDefinitions` 合并后构造设置页 VM，旧价仍生效）。
- 待办（下一版）：删除 `AppSettings.PricingOverrides` / `ModelPricingOverride` / `PricingEngine.UpdateOverrides` 三处旧桥（届时确认无用户 settings.json 仍依赖）。
