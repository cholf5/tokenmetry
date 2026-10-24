---
id: I-041
title: 关于面板补版权/许可/仓库/无担保行——AGPL Appropriate Legal Notices 对齐
type: feature
priority: P3
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

设置页「关于」面板（I-039 建的版本 + 更新检查区）在版本行下补四项署名/法律信息（参照 stupid-dict 的「关于」Tab，`SettingsWindow.axaml:180-218`）：

1. **版权行**：`Copyright © 2026 cholf5`——运行时读程序集 `AssemblyCopyrightAttribute`（Directory.Build.props `<Copyright>` 单一出处，不二次硬编码）。
2. **许可行**：`GNU AGPL-3.0` → HyperlinkButton 直达仓库 LICENSE 文件。
3. **仓库行**：`https://github.com/cholf5/tokenmetry` → HyperlinkButton；URL 出自 `UpdateAssets.Repository`（与更新检查同源）。
4. **无担保声明行**（AGPL-3.0 §7(d) Appropriate Legal Notices）：「本程序不提供任何担保；依 GNU AGPL-3.0 条款分发。」

## Background

- 仓库 LICENSE 是 **AGPL-3.0**（非 stupid-dict 的 MIT）：§7(d) 要求交互界面显示 Appropriate Legal Notices（版权声明 + 无担保声明），对原本不显示的旧界面可豁免，但既然动「关于」顺手补齐，成本不变。
- 版本行已有 `LabelCurrentVersion` + 值的行模板；新行复用同一版式（本地路径面板的逐行 Grid 惯例）。
- **不做**：作者行的 GitHub/X 图标按钮（stupid-dict 有）——仓库行已是 GitHub 出口，为单一图标扩图标白名单（再生 Icons.axaml + DesignTokenTests 同改）不值；社交出口只保留仓库链接。

## Acceptance

- [x] 关于面板展示版本/版权/许可/仓库四行 + 无担保小字；明暗两主题真机目检移交用户。
- [x] 版权串与 Directory.Build.props `<Copyright>` 同源（程序集属性读取）；仓库/LICENSE URL 与 `UpdateAssets.Repository` 同源。
- [x] 新文案 4 key 进 Translations.cs（zh 源 + en 覆盖）；许可名与 URL 为专有名词/常量不进 i18n。
- [x] 全量测试绿（1453 Passed / 0 Failed：Core 183 / Reconciliation 49 / Infrastructure 661 / App 209 / Headless 351，本机 macOS）。

## Dependencies

无（I-039 的独立后续润色）。

## Development Log

- 2026-10-24：建卡并当日收口。落地：`UpdateAssets` 增 `RepositoryUrl`/`LicenseUrl` 常量（与更新检查同源）；`SettingsViewModel` 增 `AboutCopyright`（运行时读程序集 `AssemblyCopyrightAttribute`——Directory.Build.props 单一出处，不二次硬编码）与两个 URL 属性；`SettingsView` 关于面板版本行下扩三行（版权/许可 HyperlinkButton → LICENSE 文件/仓库 HyperlinkButton）+ 表下无担保小字行（§7(d)「无担保」半句），行版式复用本地路径面板的逐行 Grid 惯例；Translations 增 4 key（zh 源 + en 覆盖）。许可名 "GNU AGPL-3.0" 为专有名词不进 i18n（同 stupid-dict 的 "MIT" 硬编码先例，XAML 内注释说明）。测试：App.Tests 增 1 条单一出处接线用例（语言无关，断言版权串非空含 cholf5、URL 与 UpdateAssets 同源——`<Copyright>` 被删时抓到空行）。**不做**（维持建卡决策）：作者行 GitHub/X 图标按钮——仓库行已是 GitHub 出口，为单一图标扩图标白名单（再生 Icons.axaml + DesignTokenTests 同改）不值。全量 1453 Passed / 0 Failed；明暗 × 1x/2x 目检移交用户。
