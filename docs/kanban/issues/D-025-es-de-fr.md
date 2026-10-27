---
id: D-025
title: 第二梯队 es/de/fr 落地——语种决策由「社区认领」改为「即刻全量」
type: feature
priority: P2
size: M
status: done
created: 2026-10-27
updated: 2026-10-27
blocked: none
---

## Goal

西班牙语（es）、德语（de）、法语（fr）三语种全量翻译上线，与 D-024 五语种同构（全量覆盖 + 回退链 + 测试固化）。**决策变更**：D-024 时定的是「第二梯队待社区认领」，用户会话拍板（2026-10-27）改为直接做完——翻译成本已由第一梯队流程摊薄（同构照抄 + 占位符/覆盖测试兜底），社区认领的等待价值不再成立。

## Background

- D-024 的机制扩展（回退链 / FallbackChain / partial 分文件 / 全量覆盖测试）是通用形状，新增语种纯增字典文件，核心逻辑零结构改动。
- es 不分 es-ES / es-MX（统一拉美倾向用词，costo）；de / fr 不分地区变体；与 pt 系统一落 pt-BR 的纪律一致。

## Acceptance Criteria

- [x] Es/De/Fr 字典与 Zh 全量对齐（`NewLanguageDictionaries_CoverEveryChineseSourceKey` × 8）+ 占位符不变式（× 9 语种字典）
- [x] `ApplyPreference` 认 es/de/fr 小写码；`Resolve` 系统语言 es*/de*/fr* 直达（不分地区变体）；回退链 Es/De/Fr → en → zh 固化
- [x] 设置页下拉 8 → 11 选项；既有五字典与 En/Zh 补 `LanguageEs/De/Fr` 自名 key
- [x] 全量测试：Core 189 / Reconciliation 49 / Infrastructure 678 / Headless 397 全绿；App.Tests 除 4 个既有环境性 SingleInstanceGuardTests（B-020 同族）外全绿

## Subtasks

- [x] `AppLanguage` +3；`ApplyPreference`/`Resolve`/`FallbackChain` 补分支；主文件补 `Es`/`De`/`Fr` 字段声明 + `LanguageEs/De/Fr` 属性
- [x] `Translations.Es.cs` / `.De.cs` / `.Fr.cs` 全量翻译（法语冒号前空格排版用半角无空格惯例，见 ThCaveats 等词条）
- [x] `SettingsViewModel.LanguageOptions` +3 + `LanguageOption.Label` 新分支；`AppSettings.Language` 注释更新
- [x] 测试 theory 全部 +3（ApplyPreference / Resolve / 覆盖 / 占位符 / 回退链 / TrendChart 千进制 / 设置页解析）；AGENTS.md 同步

## Dependencies

- D-024（机制与测试基建的直接复用）
