---
id: Q-013
title: 自建模型误标「已改」与「移除覆盖」——覆盖语义锚定导入层，自建条目行走「删除」
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

模型目录里通过「新增模型」建立的自建条目不再显示「已改/Overridden」徽标与「移除覆盖」动作——它的价与元数据是第一手定义，下面没有导入层，谈不上覆盖谁。其操作列显示「删除/Delete」（整条移除）；只有确实覆盖了导入层（内置快照 / models.dev）的条目才挂「已改」徽标与「移除覆盖」（移除后回落导入值）。

## Background

- 用户反馈（2026-10-25 会话）：Add model 添加的新模型哪怕没有覆盖任何内置模型，也显示 Overridden、Actions 出现 Remove override。
- 取证：`ModelDefinitionOverride.Bind(imported)` 对自建条目（imported == null）同样置 `IsPriceOverridden` / `IsMetadataOverridden`——用户确实填了价与元数据，字段确实来自用户层；行 VM `ModelDefinitionRowViewModel.IsOverridden` 直接取二者或。语义混淆点在 UI 层把「字段来自用户层」当成了「覆盖了导入层」：Core 标志描述的是字段来源（刷新保护文档语义），全仓消费方仅行 VM 与 `CloneWithMatchPattern` 透传，修 UI 层即可、Core 不动。

## Acceptance Criteria

- [x] 自建条目（Origin = User）：不挂「已改」徽标、不进「已覆盖」筛选 chip（去「自定义」chip 找），Actions 显示「删除」
- [x] 覆盖导入层的条目（Origin = Snapshot/Remote 且用户字段接管）：徽标、「已覆盖」筛选、「移除覆盖」行为不变
- [x] 纯导入层条目仍无任何移除按钮（无假按钮回归，headless `ModelCatalog_UnoverriddenRowHasNoRemoveButton` 继续守护）
- [x] 新增 `ModelDeleteButton` 键（中「删除」/英「Delete」，键奇偶不破）
- [x] 全量 `dotnet test Tokenmetry.sln` 绿

## Subtasks

- [x] 行 VM：`IsOverridden` 加 `Origin != User` 前提；新增 `IsUserCreated`；`CanRemove` 改 `IsOverridden || IsUserCreated`（行为等价）
- [x] XAML：操作列拆「删除」（IsUserCreated）/「移除覆盖」（IsOverridden）互斥按钮，同一 RemoveModelDefinition 命令，删除逻辑零改动
- [x] Translations 增键；测试更新（筛选钉住断言改新语义 + CanRemove 测试补徽标 / 自建断言）
- [x] 全量测试 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- 「已覆盖」chip 词义随徽标同步收紧（同一 `IsOverridden` 判定，chip 与徽标必须同词同义）：原先钉住「自建条目也算被用户改过」的 `ModelRows_FilterChipsUnionThenIntersectWithText` 改为只有覆盖导入层的条目进该筛选，自建条目由「自定义」chip 承接。Core 的 `Bind` 标志不动：它们描述字段来源，未来若有新消费方仍可用；自建条目日后被 models.dev 收录时 Origin 自然翻成导入层、徽标与「移除覆盖」如实出现（装订语义自然涌现，无需迁移）。
- 全量 1481 Passed / 0 Failed / 22 Skipped（用例数不变——本卡改断言不增用例）。
