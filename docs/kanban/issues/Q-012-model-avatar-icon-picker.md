---
id: Q-012
title: 模型头像（图标选择器四模式）——新增模型也有头像，全应用显示统一
type: feature
priority: P2
size: M
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

Data & pricing「新增模型」支持给模型设置头像，与内置模型的官方 logo 观感统一。编辑弹层加「头像」区（预览芯片即入口，点击打开图标选择器 modal），提供五种方式：

- **自动**（缺省）：按模型 ID 走既有 `ModelVisualMap` 名推断；
- **内置图标**：`ModelVisualMap.Vendors` 全变体格子任选（含 phi 字形 / generic 兜底），**不做匹配校验**——用户拍板"选错了也是用户的事"；
- **字母**：单字符（字母或汉字），显示面积约束用户拍板只能 1 个；
- **颜色**：20 色常量色盘（前 7 色与 AppSeries0–6 同源）；
- **图片**：上传后自动**居中裁方 + 压缩为 64×64 圆角 PNG**（data URI 存 settings.json）。

头像随目录条目在全应用所有模型图标槽展示（目录行 / 别名目标 / 模型统计页 / 总览页 / 托盘弹层）。

## Background

- 用户反馈（2026-10-24 会话）：所有内置模型都有头像，用户新加的模型也应该有，显示才统一；并提出四种头像方式与 icon picker modal 的产品形态。
- 既有机制：图标槽 = `ModelChip.Vendor` 附加属性 → model-* 伪类 → SharedStyles 选行内官方 logo / Φ 字形 / circle-help 兜底。`ModelDefinitionOverride.LogoVendor` 字段已存在但 UI 从未暴露（仅手改 settings.json 可达）。
- 头像数据落 settings.json（配置，非会话内容），不进 SQLite——隐私红线不受影响；`AvatarKind` + 载荷与 `HasMetadataOverride` 联动，models.dev 刷新不冲掉。
- 旧手写 `LogoVendor`（Auto + 非空）兼容语义：装订层保留、解析层按 Vendor 呈现；编辑表单显式化为 Vendor 模式（所见即所存）。用户改选其它模式时旧值显式清空，防止切回自动时"复活"。

## Acceptance Criteria

- [x] 图标选择器 modal（AvatarPickerDialog，页内 overlay 叠于编辑弹层之上）：模式段选 + 各模式载荷 + 顶部实时预览；确定写回表单字段（单模式载荷整组写入），取消 / 遮罩 / Esc 丢草稿；确定门禁拦空载荷（字母非空 / 已选色 / 图片已传）。
- [x] 五处模型芯片视图改绑 `ModelChip.Avatar`（`ModelAvatarVisual` 收敛四模式 + 自动推断 + 旧 LogoVendor 兼容），letter/color/image 新增伪类层（SharedStyles，色值全走令牌或用户内容色）。
- [x] 统计页 / 总览 / 托盘弹层经 `ModelAvatarResolver`（DI 单例，共享 ModelCatalog）联动目录头像；未关联名字回落名推断。
- [x] 上传标准化确定性：Avalonia 12 RTB 绘制走 FramebufferRenderTarget（合成线程延迟）实锤——处理改为全程 CPU 像素运算（解码 → CopyPixels → 面积均值 + 圆角蒙版 → WriteableBitmap 帧缓冲直写 → Save 同步编码）。
- [x] 测试：Core 装订语义 6 例、App 解析器 9 例、VM 表单/选择器回路 6 例、headless 芯片渲染 4 例 + data URI 转换器 1 例 + 图片处理 3 例（圆角烙进像素的角落透明断言）；En↔Zh 键奇偶测试自动覆盖 18 个新键。
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿（1481 Passed / 0 Failed / 22 Skipped）。

## Subtasks

- [x] Core：`ModelAvatarKind` 枚举 + `ModelDefinition`/`ModelDefinitionOverride` 头像四字段 + Bind 覆盖语义 + `CloneWithMatchPattern` 透传
- [x] App：`ModelAvatarVisual`（定义/名字/变体名三入口 + 颜色规范化）、`ModelAvatarResolver`、`ModelChip.Avatar` 附加属性（伪类 + 色块就地着色）、三个转换器
- [x] `AvatarImageProcessor`（居中裁方 + 面积均值缩放 + 圆角蒙版 + data URI）
- [x] `AvatarPickerViewModel`（草稿态 + 格子/色盘选中态 + CanConfirm 门禁）+ `AvatarPickerDialog` + DataPricingView overlay 宿主 + 编辑弹层头像区
- [x] `DataPricingViewModel`：表单五字段 + 预览 + 打开/确认/取消命令 + 保存/装载/重置
- [x] i18n 18 键（zh 源 + En 全量覆盖）

## Dependencies

- I-035（模型目录）、S-003/S-004（目录 UI）、Q-009（generic 兜底图标）
