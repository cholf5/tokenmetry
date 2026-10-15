---
id: I-035
title: 模型目录（ModelId 单一身份）+ 计价接线
type: feature
priority: P1
size: L
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

模型的价格、厂商、图标、聚合归一此前由三套各自独立、都以「模型名字符串」为键的机制决定（别名表只作用于聚合、定价走三级回退且设在采集期、图标是 App 层硬编码子串表），且**别名与价格互不相通**——用户给一个名字指定了规范名，价格依然按原始名匹配，缺价模型永远无价。

本卡落定「一个模型一个稳定 ID、所有参数挂在 ID 上」的身份层：`ModelCatalog` 把内置快照 / models.dev 在线 / 用户条目合成为一份模型配置表，提供 原始名 → 模型 ID 的解析；`PricingEngine` 的用户层改按模型 ID 命中，使**关联同时决定聚合与价格**。

## Background

- 判定口径的取舍（踩过的坑，见 Test Cases）：条目上的 `MatchPattern` 语义是"**只**走家族包含匹配（精确档会跳过它）"，而快照里几乎每个条目都带 `match` 字段——所以把归一化后的 ID 喂给计价匹配会同时打坏"点号条目命中点号名"与"子型号精确条目压过家族模式"两条已验证口径。结论：**身份解析只用于用户层与聚合**，导入两层（远程 / 快照）继续按原始拼写（别名解析后）走既有匹配，对账锚零漂移。
- 身份解析收敛只认"已知条目"：ID / 来源键（`provider/model-id`）精确 → 归一化 ID（点号 / 斜杠折连字符、大小写折叠）→ 候选名（去 provider 前缀 / 去 `[pi] ` 装饰 / 去 `-fast` 采集标记）。全部是确定性判定，不做拼写近似；未命中原样返回（保持"宁可分开不猜"）。
- 用户条目按**字段级覆盖**：价格被覆盖的条目在 models.dev 刷新时价格不被冲掉，但元数据照常更新；半截价格（只填 input）整组忽略，不留"部分覆盖"的中间状态。
- 单一实例纪律：目录必须与计价引擎、查询服务共享同一个对象（`AppServices` 单例注入），两份目录会让"设置页改的价"与"统计用的身份"分叉。

## Acceptance Criteria

- [x] `ModelId`：小写字母 + 数字、段间单连字符的约定 + 归一化（`.`/`/`/`@`/`_`/空格 → 连字符、大小写折叠、连续与首尾连字符削平）
- [x] `ModelDefinition`（目录条目：Id / Vendor / DisplayName / LogoVendor / SourceKey / Origin / 覆盖标记 / 四桶价 + 长上下文档）与 `ModelDefinitionOverride`（用户层：逐字段覆盖 + 沿用旧 `ToPriceEntry` 校验口径的兼容桥）
- [x] `ModelCatalog`：三层合成（快照 → 在线按 ID 覆盖但保留家族规则 → 用户逐字段覆盖）、身份解析（别名 → 精确 → 归一化 → 候选 → 家族）、远程换对象自动重建、`IModelNameResolver` 实现（查询服务直接消费）
- [x] `PricingEngine` 用户层按模型 ID 命中（含 I-024 三条候选链走向）；导入两层匹配口径逐位不变
- [x] 覆盖测试：目录合成 / 身份收敛 / 别名生效 / 自建模型计价 / 热更新即时生效 / 刷新不冲覆盖价；既有 110 个 Core 测试全绿

## Subtasks

- [x] Core：`Models/ModelId.cs`、`Models/ModelDefinition.cs`、`Models/ModelDefinitionOverride.cs`、`Services/ModelCatalog.cs`
- [x] Core：`PricingEngine` 接目录（用户层按 ID；导入两层保持原名口径）+ 删掉已死的 `TryMatchOverride`
- [x] Core：`AppSettings.ModelDefinitions` 新字段 + 旧 `PricingOverrides` 转注释义
- [x] App：`AppServices` 装配共享目录（含旧字段折算）；`UsageQueryService` / 设置页改用目录
- [x] 死代码清理：`BuiltinPricingSnapshot.TryMatch` 删除（生产路径已走目录），两处测试改用目录断言

## Dependencies

- I-003（定价引擎）、I-024（候选计价接线）、I-030（别名聚合）、I-025（快照锚点纪律）

## Test Cases

### TC-001: 关联对价格生效（本卡的核心验收）

Steps: 造一个目录里关联不上的原始名 `glm46-preview-build`；先直接计价（应缺价）→ 再配别名指向 `glm-4.6` → 重查价格。

Expected: 未关联时 `Source=Missing`、`CostUsd=null`、进 `UnpricedModels` 清单；关联后按 glm-4.6 的快照价计价（`Source=Snapshot`），1M input 恰等于该条目 input 单价。覆盖于 `ModelCatalogPricingTests`。

### TC-002: 家族模式与子型号精确条目不被身份层打坏

Steps: 用归一化 ID 与点号原名分别计价 `gpt-5.6-terra` / `gpt-5.6-codex` / `gpt-4.1-mini` / `o3-mini`。

Expected: terra 走自己的条目价（1.42），`gpt-5.6-codex` 走 sol 家族（0.4），mini / nano 走各自精确条目价——即 `PricingEngineTests` 的既有对账用例逐位不变。首轮实现曾把 ID 喂进匹配器导致这四条全错，是设计结论（"身份只用于用户层"）的来源。

### TC-003: 刷新不冲覆盖价

Steps: 目录同时接远程源与用户条目（同一 ID），远程价 7/8、用户价 1/2 查生效价；再把远程快照换成新对象。

Expected: 生效价始终 1/2 且 `IsPriceOverridden=true`；新对象带来的新条目立即可见（不重启）。

### TC-004: 身份收敛

Steps: 对 `glm-4.6` / `GLM-4.6` / `glm-4-6` / `zai/glm-4.6` / `GLM-4.6-fast` 逐个解析身份。

Expected: 全部 `Id=glm-4-6` 且已关联；完全未知的名字保持原拼写且 `Kind=None`；带日期后缀的历史名走家族档。

## Dev Log

- 2026-10-15：实施完成。首轮把归一化 ID 喂给全部三层匹配 → 7 个 Core 测试红（terra 家族、子型号、用户覆盖、provider 前缀远程优先）；修正为"身份只作用于用户层 + 聚合"，导入两层保持既有原名口径 → 全绿。教训写进 Background 第 1 条：**家族模式是按拼写做包含判定，归一化会静默改变命中集合**。
- 遗留（不属于本卡）：模型维度的 provider 拆分（需 `session_facts` 加 provider 列）、按 provider 的差异价（同一 ID 多 provider 目前取 rank 最优）、图标变体与厂商 id 的映射表（现由 `LogoVendor` 显式指定，未覆盖时仍走名字推断）。
