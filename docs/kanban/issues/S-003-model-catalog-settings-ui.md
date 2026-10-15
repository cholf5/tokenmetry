---
id: S-003
title: 设置页模型目录（价格 / 厂商 / 图标唯一入口 + 关联）
type: feature
priority: P1
size: L
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

给"模型价格"一个用户能到的地方：设置页新增「模型目录」卡——列出全部模型条目（内置 / 在线 / 自定义），可新增、编辑、移除覆盖；原有的「模型聚合」卡保留，但语义收窄为"目录关联不上的名字手动指定到某个模型 ID"。

## Background

- 改动前 `AppSettings.PricingOverrides` **没有任何 UI 出口**（只有 `JsonAppSettingsStoreTests` 在写），缺价模型唯一的出口是成本页 KPI 的 tooltip——只能看，什么也做不了。这是本卡存在的直接原因。
- ID 输入不做"报错式校验"而是归一：用户敲 `GLM-4.6` 自动落成 `glm-4-6`（约定见 I-035）。只有归一后仍不合约定（如 `bad#id`）才报错——减少无意义的输入摩擦。
- 价格成对可空：都不填 = 只登记身份（先给模型一个 ID 和厂商，价后补）；只填一个 = 录入错误（半截价会伪装成已知价）。
- 关联目标侧候选改为**目录里的模型 ID**（此前是原始名清单）：关联的目标是模型，不是另一个名字。
- 候选源侧排序：目录关联不上的名字排前面——目录能自动关联的不需要人管，待处理的名字才是这一栏的用途（已关联项仍在清单里，用户可能想覆盖自动关联的结果）。

## Acceptance Criteria

- [x] 模型目录卡：条目表（ID + 显示名 / 厂商 / 单价 / 来源徽标 / 已改徽标 + 编辑 / 移除覆盖），面板内滚动不拉长页面
- [x] 新增与编辑共用表单：ID / 厂商 / 显示名 / 输入价 / 输出价 + 行内校验 + 清空
- [x] 编辑载入生效值（用户层有则用户值，否则导入层值，未覆盖字段回落导入层）
- [x] 移除覆盖：自建条目消失，被覆盖的导入条目回落内置 / 在线价
- [x] 关联卡目标侧候选 = 模型 ID；源侧候选未关联优先
- [x] 全部文案进 `Translations.cs`（中英）；无硬编码色值，复用既有 `panel` / `table-th` / `table-td` / `chip` / `link-btn` / `kpi-foot` / `model-chip` 类
- [x] headless 端到端：面板渲染 + 表单输入写回 VM；VM 级：保存 / 校验 / 编辑 / 移除 / 行构造

## Subtasks

- [x] App：`SettingsViewModel` 目录区状态与命令（`ModelRows` / 表单字段 / Save / Edit / Remove / Reset / `ModelIdCandidates`）
- [x] App：`ModelDefinitionRowViewModel`（图标走 `LogoVendor ?? Id`，来源与覆盖徽标）
- [x] App：`SettingsView.axaml` 目录卡 + 关联卡目标侧改绑模型 ID
- [x] App：`Translations` 30 个新 key（中英）+ 成本页指路文案
- [x] Tests：`SettingsViewModelTests` 六个新用例 + `SettingsPageTests` 目录卡渲染用例 + 既有别名用例迁移到目录断言

## Dependencies

- I-035（目录模型）、I-037（保存即重算）、S-002（设置页信息架构）

## Test Cases

### TC-001: 保存条目（含 ID 归一）

Steps: 表单填 `GLM-4.6` / `zai` / 1 / 2 → 保存。

Expected: `settings.ModelDefinitions` 一条且 `Id=glm-4-6`；落盘一次；目录该条 `IsPriceOverridden=true` 且 input=1；触发一轮重算；表单回新建态。

### TC-002: 校验

Steps: 分别用 `bad#id`（非法 ID）与只填输入价（半截价格）保存。

Expected: 各自的错误文案；两种情形都不落盘、不重算、不改目录。

### TC-003: 编辑与移除覆盖

Steps: 先给 `glm-4.6` 配 9/90 → 编辑该行 → 移除覆盖。

Expected: 编辑载入 9 / 90 且厂商回落导入层的 `zai`；移除后 `ModelDefinitions` 空、目录回到快照价且 `IsPriceOverridden=false`。

### TC-004: 面板渲染与绑定（headless）

Steps: 渲染设置页 → 断言目录标题 / 首行 ID / 表头文案在可视树里 → 往厂商输入框键入文本。

Expected: 文案齐全；输入写回 `ModelFormVendor`（按 `x:Name` 定位，不按控件下标——页面上有多个输入框，AutoCompleteBox 也是 TextBox 子类）。

## Dev Log

- 2026-10-15：实施完成。既有 headless 别名用例改为按占位文案定位 AutoCompleteBox（新增面板后按下标取会静默错位）；新用例按 `x:Name` 定位输入框。
- 取舍：不做跨页跳转（成本页缺价清单 → 设置页并预填）。页面定位是"如实说明口径"，动作集中在设置页；若将来要做，应抽一个导航参数契约而不是在成本页复制表单。
