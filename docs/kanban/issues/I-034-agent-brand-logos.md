---
id: I-034
title: Agent 品牌 logo 接入（ZCode/DSH 等 16 个 Agent 行内官方标 + 未覆盖者散列字形兜底）
type: feature
priority: P2
size: M
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

Agent 图标从 D-008 起的「软色底芯片 + 按名散列字形/色变体」改为**行内官方品牌 logo**（lobe-icons），复用 I-033 的模型图标机制。用户观感问题即「ZCode / DSH 的图标不对」——那些字形与颜色对一个 Agent 是随机的、无语义。

## Background

- **现状（用户发现）**：Agent 品牌图标只渲染在 3 处——Agent 统计页 Top4 KPI 卡、会话明细表 Agent 列、右栏「最近会话」行（设置页数据源表只有 Agent 名，无图标；Agent 统计页的排行表行首是「系列色圆点」而非图标，用户随后指出，见 AC/Dev Log）。三处都是 `AgentVisualMap` 按 Agent 名散列出的 5 字形之一 + 4 色变体之一，与品牌无关。模型侧 I-033 已 logo 化，Agent 侧没跟上。
- **图标来源**：与模型图标同一个 npm 包 `@lobehub/icons-static-svg@1.95.1`（MIT License, Copyright (c) 2023 LobeHub），lobe 自带**专门的编程工具标**（`claudecode` / `codex` / `geminicli` / `amp` / `goose` / `opencode` / `kilocode` / `hermesagent` / `openclaw` / `antigravity` / `githubcopilot` 等），无需拿模型厂商标替代。
- **覆盖与例外**：16 个 Agent 有标；`zai` = Z.ai 的编程 CLI **ZCode** → Z.ai 三块 "Z" 标（与模型表 glm 内联标同一形状）；**DSH 的标与 DeepSeek 同形状、官方呈现为黑色**（用户告知）→ 复用 `deepseek` 单色几何走 Light=黑 / Dark=白；`antigravity-color`（`<g>`/mask/filter）与 `copilot-color`（radialGradient/transform）生成器不支持 → 退到 `antigravity` / `githubcopilot` 单色标。lobe 里**没有**标的：Codebuff / Droid(Factory) / pi-agent（lobe 的 `pi` 是 Inflection 的 π）/ pi 命名存储 → 落 `generic` 回退散列字形。
- **设计决策（用户选定「官方彩标 · 去色块」）**：行内官方彩色 logo，去掉散列色块（与原相位 D-008 的四色芯片退场），与模型图标一致。KPI 卡保留 30×30 槽位但底色改**中性软底**（不塌陷、保住原型 `.kpi-icon` 的视觉锚点）；会话表用 16×16 透明行内槽（对齐 model-chip）；右栏沿用 30×30 中性软底。
- **技术路线**：零新依赖，复用 I-033 的 SVG→`DrawingImage` 转换。生成器泛化为**双表双输出**（`MODEL_VENDORS` + `AGENT_VENDORS`，共用 `shape_drawing`/`convert_svg`），脚本更名 `scripts/generate-brand-logos.py`。渲染复用伪类机制：`AgentChip.Logo` 附加属性 → `agent-*` 伪类 → SharedStyles 给 `Image.Source`（DynamicResource，单色版随明暗换黑白）。

## Acceptance Criteria

- [x] 16 个 Agent 行内官方 logo（claudecode/codex/geminicli/amp/openclaw/qwen/kimi/goose/opencode/kilo/hermes/grok/antigravity/copilot/zai/dsh），`AgentLogos.axaml` 由生成器产出
- [x] 单色官方标（goose/opencode/kilo/hermes/grok/antigravity/copilot/zai/dsh）Light=黑 / Dark=白；kimi 双版本（彩标主体为白，暗底设计）；其余明暗同用 color 版（同一实例）
- [x] DSH 复用 DeepSeek 几何（官方黑色）走单色策略；ZCode 用 Z.ai "Z" 标
- [x] 未覆盖 Agent（Codebuff/Droid/pi-agent 及命名存储）`generic` → 中性前景**散列字形**兜底（诚实回退，不伪造品牌标）
- [x] 三处视图全部换为行内 logo（AgentsView KPI ×4 / SessionsView 明细 / MainWindow 右栏），行 VM 改 `LogoVendor`
- [x] **补一处漏网（用户指出）**：Agent 统计页的 Agent **排行表**行首原为「行序映射系列色圆点」（`series-dot` + `AppSeries{N}Brush`），改为 `agent-chip` 品牌 logo——对齐既定口径（模型明细行用 logo；总览模型分布行「有图标用图标、无图标才退回圆点」）。`AgentRankRowViewModel` 的 `DotBrush`/`ResolveSeriesBrush`/`rank` 参数随之删除（该页无图例与之对应，圆点色只是装饰）
- [x] **再补一处（用户指出）**：模型统计页「模型明细」表的**关联 Agent 列**加 Agent 品牌图标。图标与 `AssociatedAgentText` 同源——唯一 Agent → 该 Agent 的标；多 Agent 共用 → 主用 Agent 的标（文案「主用 等 N 个」的主语即它）；无会话数据（"—"）→ `HasAssociatedAgentIcon=false`，整槽隐藏（不猜，与文案同纪律）
- [x] **第三处补充（用户指出）**：设置页「已识别的数据源」表行首加 Agent 品牌图标。该表 19 个源（采集管道清单）里 15 个有标、4 个（Droid / Codebuff / pi-agent）回退散列字形；`DataSourceRowViewModel` 增只读派生属性 `LogoVendor`/`Glyph`（不参与 record 相等性）
- [x] 散列色退场：删 `chip-blue/green/purple/neutral` 四类与 `AgentChip.Variant`（`AppIconChip{Hue}*` 令牌保留——总览/成本页 KPI 卡与样式预览页仍直接用）
- [x] 生成器 `scripts/generate-brand-logos.py` 一次产出 ModelLogos + AgentLogos 两文件，可复现（pin 版本、遇不支持结构整体失败不写文件）
- [x] `AgentLogoTests`（8 用例）+ `DesignTokenTests` 白名单扩 `AgentLogoTests.LogoKeys.Values`
- [x] headless 明暗截图目检 16 标两主题全部可读（codex 白底方块在浅色下消失但字形仍在；geminicli/openclaw 的近黑元素在深色下隐去但主体仍清晰）
- [x] 全量 5 工程测试通过（1183 用例 0 失败）

## Subtasks

- [x] 生成器泛化：`LogoSet` 参数化（vendors/pascal/key_prefix/out/header）、`AGENT_VENDORS` 表、双文件输出、改名 `generate-brand-logos.py`
- [x] 生成 `AgentLogos.axaml`（16 键，单色/彩色/双版本三分支）
- [x] `AgentVisualMap`：`Resolve`/`ResolveVendor`/`Vendors`（含 generic），字形降级为兜底；`AgentChip` 换 `Logo` 附加属性 + `agent-*` 伪类
- [x] SharedStyles：`agent-chip`/`agent-chip-lg` 两槽 + 16 条品牌 Source 规则 + generic 兜底；删 `chip-*` 四类
- [x] App.axaml ResourceInclude `AgentLogos.axaml`
- [x] 三视图模板 + 三行 VM（AgentsViewModel / SessionsViewModel / SessionsPanelViewModel）
- [x] Agent 排行表行首圆点改 logo（`AgentRankRowViewModel` 去 `DotBrush`）+ 整页 stub 数据明暗目检
- [x] `AgentLogoTests` + `DesignTokenTests` + `SessionsTests` 断言（ChipVariant → LogoVendor）
- [x] 文档：`design-tokens.md` 新增 §8（§4 类表、§7 措辞同步）
- [x] 临时明暗截图用例（用例后删，同 I-033 做法）

## Dependencies

- I-033（lobe 生成器与伪类渲染机制）、D-008/D-009（三处视图与行 VM）

## Test Cases

### TC-001: 名单同步与资源存在性

Steps: 遍历 `AgentVisualMap.Vendors`，断言每个变体要么在 `AgentLogoTests.LogoKeys` 要么在字形兜底名单；兜底变体断言无 logo 资源；Light/Dark 两变体都能解析成 `DrawingImage`。

Automated: `AgentLogoTests.Every_vendor_has_logo_or_glyph_fallback_listed` / `Logo_resources_resolve_as_drawing_image_in_light_and_dark` / `Glyph_fallback_vendors_have_no_logo_resource`。

### TC-002: 明暗策略

Steps: `TryGetResource` 取单色标 Light/Dark `DrawingImage`，下钻 `DrawingGroup→GeometryDrawing` 断言画刷色；kimi 断言 Light 双 path 全黑、Dark 首位 `#1783FF`；claudecode 断言明暗同一实例且首位 `#D97757`。

Automated: `AgentLogoTests.Mono_logos_flip_between_light_and_dark` / `Kimi_uses_mono_on_light_and_color_on_dark` / `Color_logos_share_one_instance_across_light_and_dark`。

### TC-003: 槽位样式真实应用

Steps: headless 窗口挂 `Border.agent-chip`（及 `agent-chip-lg`）+ `Panel{TextBlock,Image.agent-logo}`，`SetLogo` 切 claudecode/dsh/generic。

Expected: claudecode/dsh → Image 可见且 Source 与资源同实例、字形隐藏；generic → Image 隐藏、字形可见。

Automated: `AgentLogoTests.Chip_styles_show_logo_for_logo_vendors_and_glyph_for_fallback`（`agent-chip` 与 `agent-chip-lg` 两个槽位各跑一遍）。

### TC-004: Agent 名 → 变体映射

Steps: 对采集层真实 Agent 名（"Claude Code" / "Codex" / "Gemini CLI" / "DSH" / "ZCode" / "Copilot CLI" / …）调 `ResolveVendor`。

Expected: 命中对应变体；未识别名（"Codebuff" / "Droid" / "pi" / 命名存储）落 generic；"OpenCode" 不误命中 codex、"Copilot CLI" 不误命中 pi 语义。

Automated: 由 TC-001 的名单双向断言覆盖（`Vendors` 与 `LogoKeys` 等势）；映射表由 `AgentLogoTests` 固化，行 VM 侧由 `SessionsTests` 断言 `LogoVendor ∈ Vendors`。

## Dev Log

- 2026-10-15：生成器从「模型专用」泛化为双表双输出——`LogoSet` 数据类参数化（vendors / pascal / key_prefix / out / header），`build_xaml` 接收它；`INLINE_MONO_LOGOS` 仍只服务模型表（glm）。脚本改名 `scripts/generate-brand-logos.py`（`--model-out`/`--agent-out`），ModelLogos.axaml 仅头部注释文案变化（重跑产出与旧文件逐字节同形）。
- 2026-10-15：`antigravity-color` 含 `<g>`/mask/filter、`copilot-color` 含 radialGradient，生成器均整体拒绝 → 该两 Agent 改单色标（`antigravity` / `githubcopilot`）。这是生成器「宁可回退不失真」纪律的正常结果，不是缺陷。
- 2026-10-15：`AgentVisualMap.ResolveVendor` 用包含规则匹配 Agent 展示名；顺序上把最短、最易误伤的 `amp` 与 `dsh`/`zcode` 放最后，`openclaw` 在 `opencode` 前。已知残留风险：`amp` 三字母包含判定可能误命中含 "amp" 的 pi 命名存储（与 ModelVisualMap 的宽松包含判定同类，接受并在此记录）。
- 2026-10-15：去掉色块后 KPI 卡用 `agent-chip-lg`（30×30 中性软底）保住视觉锚点，会话表用 `agent-chip`（16×16 透明，对齐 model-chip 行高），右栏沿用 30×30。`chip-blue/green/purple/neutral` 与 `AgentChip.Variant` 确认无其他引用后删除（grep 全仓），`AppIconChip{Hue}*` 令牌保留（总览/成本页 KPI 卡与样式预览页在用）。
- 2026-10-15：headless 明暗截图目检 16 标 ×2 主题（临时用例，验证后删除）：全部可读；codex 官方彩标的白色圆底在浅色主题下与页面底融合但内部渐变字形清晰，geminicli/openclaw 的近黑元素在深色主题下隐去但主体（渐变星/红爪）仍在——与 I-033 的 kimi「整块不可读」不同，无需为它们切单色策略。
- 2026-10-15：全量测试通过 1183 用例 0 失败（Core 110 / Infra 647 / App 112 / Reconciliation 49 / Headless 265，跳过 22 为既有平台/真实数据用例）。
- 2026-10-15（用户指出后补）：Agent 排行的系列色圆点漏了。排查四周 Agent 图标渲染点只找到 3 处（KPI 卡 / 会话明细 / 右栏），漏了排行表——它的行首是 `series-dot`（不引用 AgentVisualMap，所以不在最初的检索范围内）。改法对齐模型侧：模型明细行（ModelsView）列 0 用 `model-chip` logo；总览模型分布行是「`HasModelIcon` → logo，否则退回 `series-dot`」。Agent 排行同理改用 `agent-chip`（未覆盖的 Agent 自动落散列字形，与会话页/右栏一致，尺寸统一为 16×16），`DotBrush` 与 `rank` 参数删除。用 stub 数据渲染整页明暗两套目检（Claude Code / Codex / ZCode / DSH / Gemini CLI / Amp / OpenCode 有标，Cursor 走兜底字形）：KPI 卡与排行行两主题全部正确。
- 2026-10-15（用户指出后补 2）：模型明细「关联 Agent」列加图标。该列不是裸 Agent 名而是派生文案（唯一 Agent → 名；多 Agent → 「主用 等 N 个」；无会话数据 → "—"），故图标与文案同源取值：`AgentCount >= 1` 时取 `DominantAgent` 解析图标，否则 `HasAssociatedAgentIcon=false` 整槽隐藏（"—" 行不出现无主图标）。`ModelDetailRowViewModel` 增 `HasAssociatedAgentIcon`/`AssociatedAgentGlyph`/`AssociatedAgentVendor`；`DimensionPagesTests.Model_rows_bind_columns_and_associated_agent_variants` 补四项断言（单 Agent→claudecode、多 Agent→codex、两处 "—" 无图标）。stub 数据明暗两套整页目检：单/多 Agents 文案与图标对齐、"—" 行无图标、文案无截断。
- 2026-10-15（用户指出后补 3）：设置页「已识别的数据源」表行首加图标。该表行来自 `IngestionScanRegistry` 快照（19 个源 = `LocalIngestionDataSource.BuildScanPipeline` 清单），Agent 名与统计页同源，故直接用同一 `AgentVisualMap.Resolve`；`DataSourceRowViewModel` 用只读计算属性 `LogoVendor`/`Glyph`（不动 9 个位置参数的构造签名与 record 相等性语义——同名源恒等）。`SettingsPageTests` 补四项断言（Claude Code→claudecode / Copilot CLI→copilot / Gemini CLI→geminicli / ZCode→zai）。19 源明暗两套整页目检：15 个官方标 + Droid/Codebuff/pi-agent 灰色兜底字形，行高与状态列对齐无偏移。

## Related files

`scripts/generate-brand-logos.py`（改名自 generate-model-logos.py）、`src/Tokenmetry.App/Styles/AgentLogos.axaml`、`Styles/SharedStyles.axaml`、`Styles/ModelLogos.axaml`（头部注释）、`App.axaml`、`Controls/AgentChip.cs`、`ViewModels/AgentVisualMap.cs`、`ViewModels/AgentsViewModel.cs`、`ViewModels/SessionsViewModel.cs`、`ViewModels/SessionsPanelViewModel.cs`、`Views/AgentsView.axaml`、`Views/SessionsView.axaml`、`Views/MainWindow.axaml`、`tests/Tokenmetry.App.Headless.Tests/Styles/AgentLogoTests.cs`、`Styles/DesignTokenTests.cs`、`Shell/SessionsTests.cs`、`docs/design/design-tokens.md`
