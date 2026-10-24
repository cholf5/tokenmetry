---
id: Q-009
title: 未识别模型兜底图标——◇ 文本字形 → circle-help 矢量问号（不冒充品牌标）
type: feature
priority: P3
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

未识别厂商的模型（如 `ark-code-latest`）在模型芯片里的兜底显示从 `◇` 文本字形换成 lucide `circle-help` 矢量问号图标——直白表达「未知厂商」，避免菱形被误读成某个自造品牌标，且墨迹尺寸跨平台稳定。

## Background

- 用户截图反馈（2026-10-24）：总览/模型页里已识别模型都有漂亮 logo，未识别的 `ark-code-latest` 只有一个小菱形，「看起来很山寨」。
- 取证：`◇` 是 `ModelVisualMap.GlyphOf` 对 `generic`（未识别厂商）的兜底字形（U+25C7 文本字形，经 `model-chip` 内 TextBlock 显示；generic 无 model-* logo 伪类规则，Image 恒隐藏）。它与 `AgentVisualMap` 的散列字形家族（✳◈⬡◉⬟）视觉同类，且 `Φ`（phi）同样是「字形即品牌」的表达——菱形因此容易被当成一个真实存在的品牌标。
- 传播面：所有模型图标消费点都走 `ModelVisualMap.Resolve` 单点（模型统计页 / 总览模型区 / 托盘弹窗 / 计价页目录行），伪类样式改一处全生效；「其他」聚合行同为 generic 语义，一并享受新兜底。

## 修正项 1（同日）：? 字形观感太小 → circle-help 矢量

- 首轮修法是把字形换成 `?`（数据层 + 样式注释 + 断言同步），用户看后反馈「问号太小了，能不能大点，或者用 svg 画个 Icon」。
- 取证：文本字形墨迹只占 em 四成——11px 字号下 `?` 实际墨迹 ~7px（I-040 同源问题：系统字体渲染不可控、墨迹占比低）；加大字号受 16px 槽位与字形 line-height 限制，文本路线到顶也大不到哪去。
- 修法：走 I-040 矢量管线——`scripts/generate-ui-icons.py` 白名单加 `circle-help`（lucide-static@1.61.0）再生 Icons.axaml（AppIconCircleHelp）；SharedStyles 新增 `PathIcon.model-fallback` 槽样式（model-chip 13×13 / model-chip-lg 22×22，中性芯片前景），`model-generic` 伪类显示图标、文本字形隐藏；`?` 字形退到数据层兜底语义（`ModelVisual.Glyph`，UI 不再渲染）。phi 的 `Φ` 文本字形不受影响。
- 接入点 5 处 model-chip：模型统计页明细行、总览模型区、计价页目录行 + 别名映射行、托盘弹窗主要模型区（lg）。
- 测试：ModelLogoTests generic 断言翻转（字形隐藏 + 图标显示）；新增墨迹几何量测回归锁（16px 芯片内 ≥10px，防「太小」悄悄回归）；IconResourceTests.GeometryKeys 补 `circle-help`，顺手补 Q-007 漏登记的 `hard-drive-upload/download` 两键（出框/主题不变护栏此前未覆盖它们）。

## Acceptance Criteria

- [x] 未识别模型显示 circle-help 矢量问号、文本字形隐藏（ModelLogoTests 窗口内样式应用用例）
- [x] 兜底图标墨迹 ≥10px @16px 芯片（几何量测锁，实际 ~13px）
- [x] phi 的 `Φ` 与各厂商 logo 映射零变化
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿（183 + 49 + 209 + 661 + 346 headless = 1448 Passed / 0 Failed / 22 Skipped）

## 明确不做

- `AgentVisualMap` 的散列兜底字形不跟改——Agent 间保持可区分是有意设计（见其类型注释），且本卡语义是「模型名→厂商」而非「Agent 实例」。
- 不给 generic 添加品牌 logo 资源（诚实回退原则：没有品牌依据就不画品牌标）。
- 图标本体语言无关（? 符号），文案本地化不涉及。

## Dependencies

- I-040（lucide 生成管线与 PathIcon 消费约定；本卡在其「厂商兜底散列字形不替换」边界上，按用户指示为 generic 单点突破——phi 与 Agent 散列字形仍在边界内）。
- I-033（model-chip / ModelLogos 机制本体）。
