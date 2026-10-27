---
id: D-024
title: 界面多语种扩展——zh-Hant/ja/ko/pt-BR/ru 五语种全量翻译 + 回退链固化
type: feature
priority: P2
size: L
status: done
created: 2026-10-27
updated: 2026-10-27
blocked: none
---

## Goal

界面语言从 zh/en 扩到 7 语种：新增繁体中文（zh-Hant）、日语（ja）、韩语（ko）、巴西葡语（pt-BR）、俄语（ru）全量翻译；语种选型按「开发者群体本地化偏好」而非母语人口（会话决策 2026-10-27）——第二梯队 es/de/fr 等社区认领，明确不做 hi/bn/ar/id（开发者英文渗透率高，ar 另有 RTL 成本）。

## Background

- 原机制：Zh 源字典 + En 覆盖字典（缺项回落中文），`Resolve` 只认 zh/else 两分支，设置页三选项。
- 扩展后每个新语种若缺 key 会经回退链露出英文/中文——比英文界面漏中文更糟的跨语混排，故回退链只作安全网，全量覆盖用测试固化（Q-010 纪律推广到全部语种）。
- zh-Hant 单列语种而非 OpenCC 运行时转换：术语需台湾化（資料/快取/設定/工作階段/本機/系統列），运行时转换做不到且引依赖。

## Acceptance Criteria

- [x] 五个语种字典与 Zh 全量对齐（`NewLanguageDictionaries_CoverEveryChineseSourceKey` × 5 固化；En 既有测试保留）
- [x] 全部语种 {0} 占位符槽位与中文源一致（`LanguageDictionaries_KeepInvariantFormatPlaceholders` × 6 固化——多语言最大机械性风险是译文丢占位符）
- [x] 回退链固化：ja/ko/pt-BR/ru → en → zh；zh-Hant → zh（同语言变体露简体比露英文可读）；en → zh；`FallbackChain` 反射身份断言
- [x] `ApplyPreference` 新码（小写 "zh-hant"/"ja"/"ko"/"pt-br"/"ru"，大小写/空白容忍）+ `Resolve` 系统语言：zh 系按书写系统分简繁（zh-TW/zh-HK/zh-Hant* → 繁体），ja/ko/ru 直达，pt 系统一落 pt-BR，未支持语种 → English
- [x] 设置页语言下拉八选项（自名不翻译，任何界面语言下显示各语种原名）；旧 "zh"/"en" 值原样兼容
- [x] zh-Hant 走万进制且单位繁体化（萬/億，`FormatCompactTokens_UsesTraditionalUnitsForTraditionalChinese`）——图表数值混入简体字形即漏译；ja/ko/ru/pt-BR 走千进制
- [x] 全量测试：Core 189 / Reconciliation 49 / Infrastructure 678 / Headless 397 全绿；App.Tests 290 通过 + 4 失败均为既有环境性 SingleInstanceGuardTests（B-020 同族，stash 干净工作区复现同款，与本卡无关）

## Subtasks

- [x] `AppLanguage` 枚举 +5；`Translations.ApplyPreference`/`Resolve`/`FallbackChain`/`StringsFor`（链式合并）重写；`Translations` 改 partial
- [x] 五个 partial 分文件全量翻译：`Translations.ZhHant.cs` / `.Ja.cs` / `.Ko.cs` / `.PtBr.cs` / `.Ru.cs`（字典字段声明集中主文件、置于 Instance 之前，规避跨文件静态初始化顺序陷阱；俄语数量模板用「名词：数字」句式规避数词变格）
- [x] `SettingsViewModel.LanguageOptions` 八选项 + `LanguageOption.Label` 新分支；`AppSettings.Language`（Core）注释更新
- [x] `TrendChart` 单位制改为 `UnitsFor(language)` 三分支 + `TraditionalChineseUnits`
- [x] AGENTS.md i18n 纪律段落同步（语种清单 / 回退链 / 全量覆盖纪律）

## Dependencies

- none（Q-010 的键奇偶测试是直接前置纪律）
