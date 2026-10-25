---
id: Q-017
title: 弹层 KPI 三格同字号（22/17 层级撤除）+ 异常徽章迁头部品牌/状态之后
type: design-change
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

KPI 三格（今日 Token / 估算成本 / 缓存命中）**同字号（17 Bold）、同列宽（\*,\*,\*）**——原型 22px 主格 + 1.3fr 主列的主次层级撤除；橙色「↑x.x 均值」异常徽章从 KPI 花费格数字旁迁至**头部 Tokenmetry 品牌/状态之后**（应用级警示位，更醒目且不挤占 KPI 格局）。VM 契约（`IsCostWarn`/`CostWarnBadgeText`/`CostWarnDetail`）零改动。

## Background

- 用户反馈（2026-10-25 会话，二段拍板）：初诊断已定性 22/17 字号差是 D-015 原型有意的主次层级（非 Q-011 改坏；Q-011 只把预警从长条 banner 挪成花费格旁徽章、反而把视线引到中格放大了不协调感）——用户看后拍板：不要层级，三格同字号；徽章留在 KPI 格「太丑、挤占本就不宽裕的布局空间」，迁头部品牌/状态后面更醒目。
- 原型依据（偏离对象）：`tokenmetry_statusbar_prototype.html` 的 `.num{22px}` / `.small .num{17px}` / `.stats{1.3fr 1fr 1fr}`。
- 关联：Q-016（8252c2a）整删 toast 通道后异常唯一出口 = 本徽章；其收口描述「弹层今日花费 KPI 徽标」的**落位**随本卡迁头部，语义（常驻警示 + Tooltip 完整消息）不变。

## Acceptance Criteria

- [x] KPI 三值 FontSize 一致（17）、FontWeight 一致（Bold）；三列星号等份额（1.3\* 撤除）
- [x] 徽章渲染在头部（`popover-head` 后代、状态文字之后），`IsCostWarn` 显隐与 Tooltip 绑定不变；无异常轮次隐藏（既有契约）
- [x] 徽章原 KPI 格占位拆除（中格水平 StackPanel 退场，值 TextBlock 回归直接子级）
- [x] headless 弹层 20/20；全量 `dotnet test Tokenmetry.sln` 1536 Passed / 0 Failed（22 跳过）

## Subtasks

- [x] RED：徽章视图用例改名并加头部位置锁（`popover-head` 后代断言）+ 新增三值同字号/同粗细/三列等份额用例（2 失败实锤旧布局）
- [x] GREEN：`StatusPopoverView.axaml`——列 `1.3*,*,*`→`\*,\*,\*`；左值 22→17、-0.5 字距随主格退场；徽章迁头部状态文字后（Margin 6 补间距）
- [x] 注释同步（文件头原型偏离登记 / 状态灯 / banner / 徽章样式 / 字号五处）+ StylePreview 演示注释；全量 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- 卡号撞车处理：本卡最初按 Q-016 编号，与并行会话的 toast 整删卡（8252c2a，已提交）撞号——其未碰弹层视图/测试文件，本卡三文件的编号注释统一改 Q-017，board 既有 Q-016 条目不动。
- 设计取舍：统一取 17 而非 22——五位数成本串（如 `$12,345.67`）在 ≈126px 等宽列以 22px 渲染有溢出风险，17px 稳妥；`LetterSpacing="-0.5"` 是 22px 主格的微调，随层级一起退场（三值文本样式逐位一致）。
- 徽章在头部与状态文字的间距 = StackPanel Spacing 5 + 自身 Margin 6（合计 11px，与品牌→状态的 12px 呼吸感接近）；类名 `popover-cost-warn-badge`、配色令牌（AppBannerWarn\* + 橙文本）零改动。
- 未做：头部在极端长文案语言下的宽度溢出锁（图标 28 + 品牌 + 状态 + 徽章 + ⚙ 合计余量充足，目检移交用户）。
