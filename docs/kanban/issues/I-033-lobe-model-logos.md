---
id: I-033
title: 模型厂商 logo 接入 lobe-icons（官方彩标行内直放 + 单色标明暗翻转）
type: feature
priority: P2
size: M
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

I-030 的「内置模型图标」实为品牌色圆角芯片 + 字母字形占位（Claude=✳、GLM=Z…），用户观感即「没有内置图标」。接入开源项目 lobe-icons（MIT），把模型统计页 / 总览模型分布 / 设置页别名行的图标换成**官方品牌 logo**。经用户选定形态：彩色官方 logo 直接放行内（无色块）。

## Background

- 图标来源：npm 包 `@lobehub/icons-static-svg@1.95.1`（MIT License, Copyright (c) 2023 LobeHub），950 个静态 SVG（`{brand}.svg` 单色 currentColor / `{brand}-color.svg` 官方配色），覆盖本仓 16 家厂商里的 15 家；phi 无对应（微软模型族非品牌），与 generic 一并回退字形。
- 技术路线为**零依赖的开发期转换**：`Avalonia.Svg.Skia` 已被官方弃用（且仅适配 Avalonia 11，本仓 12.1.4），而 SVG 的彩色结构（多 path、逐 path fill/fill-rule/fill-opacity、linearGradient、circle）可完整转成 XAML `DrawingImage`（DrawingGroup + GeometryDrawing），生成物提交进仓库，运行时零解析成本。
- 主题策略（实测各 SVG 后定）：官方标无彩色形态（openai/grok）→ ThemeDictionaries Light=黑 / Dark=白（lobe 静态 PNG 同惯例）；kimi 彩标主体为白色 K（暗底设计，浅色主题不可读）→ Light=单色标 / Dark=彩标；其余明暗同用彩色版。
- 渲染复用 I-030 的伪类机制：`ModelChip.Vendor` 附加属性 → model-* 伪类 → SharedStyles 规则给 `Image.Source`（DynamicResource，单色版随主题换黑白）。ModelVisualMap 判定逻辑与 ModelChip 附加属性零改动。
- `ModelLogos.axaml` 键纳入 `DesignTokenTests.AllTokenKeys` Semi 撞名白名单，延续「App* 键有 headless 测试固化」纪律。

## Acceptance Criteria

- [x] 15 家厂商行内官方 logo（claude/gemini/zhipu→glm/meta→llama/wenxin→ernie 等映射见生成器 VENDORS 表），渐变（gemini 三层 userSpaceOnUse、meta 13 个 objectBoundingBox、minimax/qwen/wenxin）、fill-opacity（折算 alpha）、circle（hunyuan）、命名色（mistral gold）全部忠实转换
- [x] openai/grok 单色标 Light=黑 / Dark=白；kimi Light=单色 / Dark=彩标；明暗翻转经 ThemeDictionaries + DynamicResource 自动生效
- [x] phi/generic 保留 Φ/◇ 字形兜底（中性前景随明暗翻转），有 logo 厂商字形由样式隐藏
- [x] 生成器 `scripts/generate-model-logos.py` 入库可复现：pin 版本、SVG→XAML 规则成文、遇 transform/未知结构整体失败不写文件（宁可回退字形不失真）
- [x] 旧「品牌色芯片」令牌清理：AppModelChip*BackgroundBrush ×16 与 ForegroundBrush 删除（grep 确认无引用），AppIconChipNeutral* 保留（Agent 芯片在用）
- [x] headless 守卫测试 `ModelLogoTests`（6 用例）+ 全量 5 工程测试通过（1196 用例零回归）

## Subtasks

- [x] 下载并核对 15 个 SVG 结构（逐 path fill/fill-rule/渐变/透明度盘点）
- [x] 生成器脚本 + 生成 ModelLogos.axaml（mono 主题分典 + kimi 双版本）
- [x] App.axaml ResourceInclude / SharedStyles 样式改造 / 三视图 Panel{TextBlock,Image}
- [x] Tokens.axaml 清理 + ModelVisualMap/ModelChip 文档注释更新
- [x] ModelLogoTests + DesignTokenTests 白名单扩充
- [x] headless 明暗截图目检（15 logo 两主题 30 帧全部正确后删临时用例）

## Dependencies

- I-030（模型聚合 + 伪类图标机制）、I-027（令牌与 ThemeDictionaries 体系）

## Test Cases

### TC-001: 资源存在性与名单同步

Steps: 遍历 ModelVisualMap.Vendors，断言每家要么在 LogoKeys（两变体解析出 DrawingImage）要么在字形兜底名单；兜底厂商断言无 logo 资源。

Automated: ModelLogoTests.Every_vendor_has_logo_or_glyph_fallback_listed / Logo_resources_resolve_as_drawing_image_in_light_and_dark / Glyph_fallback_vendors_have_no_logo_resource。

### TC-002: 单色标明暗策略

Steps: TryGetResource 取 OpenAI/Grok/Glm/Kimi 的 Light/Dark DrawingImage，下钻 DrawingGroup→GeometryDrawing 断言画刷色。

Expected: openai/grok 黑↔白翻转；glm 三块 "Z" 形几何同色黑↔白（官方标无彩色形态）；kimi Light 双 path 全黑、Dark 双 path 首位 #1783FF（彩标）。

Automated: ModelLogoTests.Mono_logos_flip_between_light_and_dark / Glm_uses_inlined_official_mark_with_mono_flip / Kimi_uses_mono_on_light_and_color_on_dark。

### TC-003: 芯片样式真实应用

Steps: headless 窗口挂 Border.model-chip + Panel{TextBlock,Image.model-logo}，SetVendor 切换 claude/generic/phi。

Expected: claude → Image 可见且 Source 与资源同实例、字形隐藏；generic/phi → Image 隐藏、字形可见。

Automated: ModelLogoTests.Chip_styles_show_logo_for_logo_vendors_and_glyph_for_fallback。

## Dev Log

- 2026-10-15：首版用 `MappingMode="Absolute"` 表达 userSpaceOnUse 渐变，Avalonia 12.1.4 编译报 AVLN2000——12 起 GradientBrush 无 MappingMode 属性，映射由 RelativePoint 坐标写法决定（纯数字=绝对、带%=相对包围盒），生成器改为直接译坐标。
- 2026-10-15：样式首版用 `Border.model-chip > Image` 直接子级选择器，headless 断言 Source=null——视图把内容包进 Panel 后 Image 是「孙级」，`>` 匹配逻辑树直接子级失败；改后代组合器（空格）。守卫测试当场抓住，证明其价值。
- 2026-10-15：headless 明暗截图目检发现 kimi 彩标（白色 K 主体 + 角落蓝片）在浅色主题只剩蓝片——官方彩标为暗底设计；改为 Light=单色标/Dark=彩标（品牌自身在浅底也是用深色标），生成器引入双版本厂商结构。
- 2026-10-15：gemini-color 的三层渐变叠加（蓝底 + 绿/红/黄渐变覆盖、stop-opacity 0 收尾）在 DrawingGroup 逐层绘制下还原正确；meta-color 13 个渐变逐 path 内联（各只被一条 path 引用，无共享）。
- 2026-10-15（用户发现后返工）：lobe 1.95.1 的 `zhipu`/`zhipu-color` 仍是智谱**旧标**（蓝色圆形图形标），与智谱现行官方标（三块 "Z" 形，bigmodel.cn favicon 形状一致）不是一个东西。生成器加 `INLINE_MONO_LOGOS` 内联官方标路径数据（形状走同一 shape_drawing，仅数据不取自 npm 包），glm 改走该分支：VENDORS 表两个 lobe id 置空，主题策略按「官方标无彩色形态」取 Light=黑 / Dark=白。官方呈现色为中性 #2D2D2D，与既有单色标取纯黑的惯例一致，未单开品牌色。**lobe 升级后需回看该表**：若上游换成新标，应删掉内联项改回 `zhipu`。
