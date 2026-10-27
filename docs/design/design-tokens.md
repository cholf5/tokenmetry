# 设计令牌与样式命名规范（I-027）

来源：UI 原型 `docs/design/ccusage_analytics_prototype.html`（v0.4 设计基准）。
实现：`src/Tokenmetry.App/Styles/Tokens.axaml`（令牌）+ `src/Tokenmetry.App/Styles/SharedStyles.axaml`（共享样式）；
品牌 logo（模型厂商 + Agent）是生成资源（`Styles/ModelLogos.axaml`、`Styles/AgentLogos.axaml`，见 §7/§8），不在令牌层。
本文档是 8 个页面卡（D-006 ~ D-013）与 S-002/Q-002 引用的约定。

> D-003 补充：外壳专用令牌 `AppSidebarBackgroundBrush` / `AppTopbarBackgroundBrush` /
> `AppNavForegroundBrush` / `AppNavActiveBackgroundBrush` / `AppNavActiveForegroundBrush` /
> `AppNavActiveIndicatorBrush` / `AppNavHoverBackgroundBrush`（Dark 侧对应原型 `.sidebar/.topbar/.nav`
> 选择器原值，Light 为协调补值）。已进 ThemeDictionaries；外壳导航样式（`ListBox.nav-list` 等）留在
> `MainWindow.axaml` 的 Window.Styles 内（外壳私有，页面卡勿复用），页面级样式仍从 SharedStyles.axaml 取。
>
> Q-021 追加 `AppNavForegroundBrush`（非选中导航项前景）：导航是可点击入口，语义上强于 muted 说明文字。
> Light `#3d4c63`（侧栏底 7.68:1，对齐原型 .nav 在暗底的 9.09 强度档），Dark `#8293ad`（沿用目检通过的现值）。
>
> 首次初始化遮罩补充：`AppOverlayScrimBrush`（MainWindow.axaml 的 `InitializingOverlay` 专用，
> 页面底色 90% 不透明，Light/Dark 各一份随明暗翻转；外壳私有样式 `overlay-card` /
> `overlay-spinner` / `overlay-title` / `overlay-subtitle` 同留 Window.Styles，页面卡勿复用）。
>
> **Q-019 暗色去蓝（2026-10-27）**：暗色侧原样移植原型后整体偏蓝——原型是大面积高饱和深蓝的看板风，
> 表面饱和度 46–49%，约为 GitHub Dark（28%）/ Slack（12%）/ Linear（11%）的 2–9 倍，全屏铺开造成
> 色适应与「一团蓝」的层级塌陷，强调蓝又与底色同色相（217–223°）而无法跳出。修法：**大面积表层保色相、
> 饱和度砍半，亮度逐项对齐原值**——文字/强调色/系列色的对比度因此一位不变（正文 16.2:1、次要文字 6.0:1、
> 强调蓝 5.9:1 前后相同）。语义色不动：六强调色、图表系列色与 Token 类型色、KPI 图标软底、告警横幅。
> 各行注释保留原型原值供溯源，箭头右侧为现值。Light 侧未改（其蓝偏移仅 2.5%，本就温和）。

## 1. 键名前缀约定

| 前缀 | 归属 | 说明 |
|---|---|---|
| `App*` | 本项目设计令牌（I-027 起） | 唯一允许新增的资源键前缀 |
| （无前缀 `*Brush`） | 遗留层（I-025 批次，App.axaml） | SecondaryTextBrush 等 10 组，冻结不新增；Q-002 收敛合并 |
| `Semi*` / 控件前缀 | Semi.Avalonia | 禁止占用/覆盖；Semi 共 146 个 `*Brush` + 178 个 `*Color` 键 |
| `DataGrid*Brush` | Semi.Avalonia.DataGrid | 同上 |

新增任何键之前，用独立 `new Semi.Avalonia.SemiTheme()` 实例 `TryGetResource(key, Light/Dark)` 复核无撞名（headless 测试 `DesignTokenTests.New_tokens_have_no_key_collision_with_semi_theme` 已固化此检查）。

## 2. ThemeDictionaries vs 普通资源

| 类别 | 放哪 | 命名 | 例 |
|---|---|---|---|
| 随明暗翻转的表面/文字/描边/强调色 | `ThemeDictionaries`（Light/Dark 各一份，画刷） | `App{语义}{角色}Brush` | `AppPanelBrush`、`AppMutedTextBrush`、`AppCardLineBrush`、`AppIconChipBlueBackgroundBrush` |
| 图表语义色（系列色、涨跌指示、固定渐变） | 普通资源（不随明暗翻转） | 色值优先 `App{名}Color`，派生画布 `App{名}Brush`（StaticResource 引用同一 Color） | `AppSeries0Color`/`AppSeries0Brush`、`AppMiniFillBrush` |

判定口诀：**跟界面表层走的进 ThemeDictionaries，跟数据语义走的不翻转**。图表无论明暗用同一组颜色（与原型一致，原型只有暗色主题）。

## 3. 图表系列色（7 个，索引自 0）

对应原型 JS `colors[]`（Agent/模型维度着色，环形图、表格圆点、图例共用）：

| 键 | 值 | 语义色名 |
|---|---|---|
| AppSeries0Color | `#4c8dff` | blue |
| AppSeries1Color | `#12b886` | teal green |
| AppSeries2Color | `#ffad4d` | amber |
| AppSeries3Color | `#a779ff` | purple |
| AppSeries4Color | `#48bde3` | sky |
| AppSeries5Color | `#8394ae` | slate grey |
| AppSeries6Color | `#f06bc4` | pink |

超出 7 个系列时回到 `AppAccent*Brush`（蓝/青/紫/绿/橙/红，随明暗取协调值）或与设计确认后再扩 `AppSeries7+`。

## 4. 共享样式类（SharedStyles.axaml）

类名对齐原型 CSS 类，视图用 `Classes="..."` 引用，一律 DynamicResource 取令牌，**视图内禁止再写硬编码色值**：

| 类 | 目标类型 | 对应原型 | 说明 |
|---|---|---|---|
| `card` | Border | `.kpi,.panel` | 仅表面：11px 圆角、1px AppCardLineBrush、145deg 渐变底 |
| `panel` | Border | `.panel` | card + Padding 15（自足，无需再连用 card） |
| `kpi` | Border | `.kpi` | card + Padding "15,15,13,13" + MinHeight 124（自足） |
| `page-title` | TextBlock | `.pagehead h1` | 21px / SemiBold（650 的近似）/ LetterSpacing -0.4 |
| `page-subtitle` | TextBlock | `.subtitle` | 12px + AppMutedTextBrush |
| `panel-title` | TextBlock | `.panelhead h2` | 13px / SemiBold |
| `link-btn` | Button | `.linkbtn` | 11px 无边框透明底 + AppLinkBrush（悬停底色保留 Semi 行为） |
| `chip` | Border | `.chip` | 5px 圆角、Padding 7,3、AppChip 背景/文字（文字色经附加属性继承） |
| `icon-chip` | Border | `.kpi-icon` | 30×30 圆角 9；颜色按变体给 `AppIconChip{Hue}BackgroundBrush`/`ForegroundBrush`（blue/amber/purple/green/neutral，使用处显式给底色——总览/成本页 KPI 卡与样式预览页在用） |
| `model-chip` | Border | （无原型对应；I-030 起图标槽，I-033 起 logo 化） | 16×16 透明槽：行内官方品牌 logo（`Image.model-logo` ← `AppModelLogo*`），phi/generic 回退字形；厂商经 `ModelChip.Vendor` 附加属性翻译成 model-* 伪类（同 AgentChip 机制），样式规则集中在 SharedStyles |
| `agent-chip` / `agent-chip-lg` | Border | （原 D-008 的 `.agenticon` 软色底芯片，I-034 起 logo 化） | `agent-chip` = 16×16 透明行内槽（会话明细 Agent 列，对齐 model-chip）；`agent-chip-lg` = 30×30 圆角 9 中性软底（Agent 统计页 KPI 卡 / 右栏最近会话行）。内容 `Panel{TextBlock 字形, Image.agent-logo}`，品牌经 `AgentChip.Logo` 附加属性翻译成 agent-* 伪类 → `AppAgentLogo*`（见 §8） |
| `series-dot` | Border | `.modeldot` | 7×7 圆角 3；颜色给 `AppSeries{N}Brush` |
| `mini` | ProgressBar | `.mini-track`/`.mini-fill` | 4px 高；Value 0-100 直接驱动填充宽度（内置 PART_Track/PART_Indicator） |
| `chart-tip` | ToolTip | （原型无 tooltip，B-006 补） | 图表悬浮提示统一装饰：chip 底 + 卡片描边 + AppTextBrush 文字、圆角 6 / Padding 10,7 / 字号 12；控件经 `ChartTooltips.Set` 挂类（Donut/Heatmap）。ScottPlot 图内气泡（TrendChart）不适用本类，但走同源令牌（`ApplyTooltipChrome`）与同一文案口径 |

## 5. 渐变角度换算（CSS → Avalonia）

CSS `linear-gradient(θ, ...)`：0deg 指向上、顺时针；方向向量（屏幕坐标 y 向下）`(sinθ, −cosθ)`；渐变线长 `L = |W·sinθ| + |H·cosθ|`；起止点 = 中心 ∓ 方向·L/2，再归一化到包围盒（Avalonia `MappingMode=RelativeToBoundingBox`，默认 SpreadMethod=Pad 与 CSS 超界取端色一致）。

- 145deg（卡片）：`(0.574, 0.819)`，方形近似下 `StartPoint="10.058%,-7.043%" EndPoint="89.942%,107.043%"`（精确解）；非方形卡片存在角度畸变——固定相对坐标下渐变俯角随宽高比变化（如 340×124 卡约 27.5°，CSS 原值 55°；225×124 KPI 卡约 38°），但两 stop 原型 `#121e31→#101a2b`／Q-019 现值 `#181e27→#151a22`（约 2% 亮度差），方向偏差视觉不可感知，接受近似；页面卡若需逐像素还原可按宽高比换算该页专属起止点。
- 90deg（mini 填充）：`StartPoint="0%,50%" EndPoint="100%,50%"`，精确。

## 6. 令牌 ↔ 原型色值对照

Dark 列 = 原型原值经 Q-019 降饱和后的现值（`:root` 变量 + `.kpi/.chip/.linkbtn/.mini-track/.kpi-icon/.agenticon` 选择器色值；色相与亮度不变，仅压饱和度，语义色与文字色仍为原值）；Light 列为 I-027 设计的协调值（同色相、明度反转、浅底上加深保证对比度）。完整对照见 `Styles/Tokens.axaml` 内逐行注释与 `tests/Tokenmetry.App.Headless.Tests/Styles/DesignTokenTests.cs` 的期望值表（测试与令牌文件同步维护）。

## 7. 模型厂商 logo（I-033，lobe-icons 生成资源）

模型统计页 / 总览模型分布 / 设置页别名行的厂商图标**不是令牌**，而是生成资源：`Styles/ModelLogos.axaml` 由 `scripts/generate-brand-logos.py` 从 npm 包 `@lobehub/icons-static-svg@1.95.1`（MIT License, Copyright (c) 2023 LobeHub）的静态 SVG 一次性转换（SVG → `DrawingImage`/`DrawingGroup`/`GeometryDrawing`，path 数据原样保留仅补填充规则前缀）。**勿手改**；再生：`python3 scripts/generate-brand-logos.py`（版本 pin 在脚本 LOBE_VERSION，升级即改它重跑）。

- 键名 `AppModelLogo{VendorPascal}`，15 家：Claude / OpenAI / Gemini / Glm(智谱标) / DeepSeek / Qwen / Kimi / Llama(Meta 标) / Grok / Mistral / Doubao / MiniMax / Cohere / Ernie(文心标) / Hunyuan；phi / generic 无官方标，视图层回退 Φ/◇ 字形。
- 例外（内联几何）：lobe 1.95.1 的 `zhipu` 仍是智谱旧标（蓝色圆形图形标），glm 改用脚本 `INLINE_MONO_LOGOS` 内联的官方新标——三块 "Z" 形几何（坐标空间非 24×24，由 Image 的 Stretch=Uniform 归一，无需换算）。形状语汇与 SVG 转换同路（同一 shape_drawing），只是数据不取自 npm 包。
- 主题策略：官方标无彩色形态（openai/grok/glm）→ ThemeDictionaries Light=黑 / Dark=白；kimi 彩标以白为主体（暗底设计，浅底不可读）→ Light=单色标 / Dark=彩标；其余明暗同用彩色版。单色/彩色的取舍由生成器 VENDORS 表逐厂商显式声明。
- 渲染：`Border.model-chip` 16×16 槽 + `Panel{TextBlock 字形, Image.model-logo}`；Source 由 model-* 伪类样式经 DynamicResource 给出（必须 DynamicResource——单色版要随明暗切换）。注意 chip 内容包在 Panel 里，样式选择器用**后代组合器**（`Border.model-x Image.model-logo`）而非直接子级 `>`。
- 键名单入 `DesignTokenTests.AllTokenKeys` Semi 撞名白名单；资源存在性 / 明暗策略 / 样式应用由 `ModelLogoTests` 固化。

## 8. Agent 品牌 logo（I-034，lobe-icons 生成资源）

Agent 统计页 KPI 卡 / 会话明细 Agent 列 / 右栏最近会话行的 Agent 图标，与 §7 完全同源同机制，只是换了一张表：`Styles/AgentLogos.axaml`（键 `AppAgentLogo{VendorPascal}`），由同一个 `scripts/generate-brand-logos.py` 的 `AGENT_VENDORS` 表生成（脚本一次跑出两个文件）。

- 覆盖 16 个 Agent（`AGENT_VENDORS` / `AgentLogos.axaml` / `AgentVisualMap.Vendors` / `AgentLogoTests.LogoKeys` 四处名单必须同步）：claudecode / codex / geminicli / amp / openclaw / qwen / kimi / goose / opencode / kilo / hermes / grok / antigravity / copilot / zai / dsh。其中 `zai` = Z.ai 的编程 CLI **ZCode** 用 Z.ai 三块 "Z" 标（与模型表 glm 内联标同一形状）；`dsh` 的标与 DeepSeek 同形状、官方呈现为黑色 → 复用 `deepseek` 单色几何。
- lobe-icons 1.95.1 里**没有**标的 Agent：Codebuff / Droid(Factory) / pi-agent（lobe 的 `pi` 是 Inflection 的 π，不是本仓的 pi-agent）以及 pi 命名存储（作为独立 Agent 出账），一律落 `generic` → 视图层回退**散列字形**（`AgentVisualMap.Glyph`，同一 Agent 全应用一致；比单一 ◇ 更有信息量，列表里多个未识别 Agent 仍可区分），中性前景随明暗翻转。
- 主题策略同 §7：只有彩色版 → 明暗同用 color；只有单色版（goose/opencode/kilo/hermes/grok/antigravity/copilot/zai/dsh）→ Light=黑 / Dark=白；kimi 双版本（彩标主体为白，暗底设计）。彩色版里 `antigravity-color`（含 `<g>`/mask/filter）与 `copilot-color`（含 radialGradient/transform）生成器不支持 → 分别退到 `antigravity` / `githubcopilot` 单色标。
- 渲染：`Border.agent-chip`（16×16 透明行内槽）/ `Border.agent-chip-lg`（30×30 圆角 9 中性软底）+ `Panel{TextBlock 字形, Image.agent-logo}`；品牌经 `AgentChip.Logo` 附加属性 → agent-* 伪类，样式集中在 SharedStyles（选择器一律后代组合器，同 §7 的坑）。五个渲染点：Agent 统计页 Top4 KPI 卡（`agent-chip-lg`）、Agent 统计页排行表行首（`agent-chip`——原 `series-dot` 系列色圆点已退场，同模型明细行口径）、会话明细 Agent 列（`agent-chip`）、右栏最近会话行（`agent-chip-lg`）、模型明细关联 Agent 列（`agent-chip`；无会话数据 "—" 时 `IsVisible=false` 整槽隐藏）、设置页「已识别的数据源」表行首（`agent-chip`）。
- 历史（D-008 → I-034）：Agent 图标原为「软色底芯片（chip-blue/green/purple/neutral）+ 散列字形」，颜色对一个 Agent 是随机的、无语义；I-034 改为行内官方品牌 logo 并删掉 `chip-*` 四个类和 `AgentChip.Variant`（散列色退场，`AppIconChip{Hue}*` 令牌保留——总览/成本页 KPI 卡与样式预览页仍直接用）。
- 键名单入 `DesignTokenTests.AllTokenKeys`（`AgentLogoTests.LogoKeys.Values`）；资源存在性 / 明暗策略 / 样式应用由 `AgentLogoTests` 固化。

## 9. 滚动条（overlay 重主题）

全局滚动条为现代 overlay 语义（原型无滚动条样式，为本节设计）：**闲置完全隐藏**（Opacity 0 且不参与命中，悬浮条不挡贴边内容），滚动值变化或指针移入滚动条邻近区（命中条外扩 24px，Q-005 由"指针在滚动区内活动即显"收窄而来）时淡入、闲置约 1s 淡出，悬停/拖拽滑块期间钉住；**永远无上下箭头**（Semi 的 Line 按钮不复存在；Track 页按钮透明保留，点击滑块上/下方仍可翻页）；6px 细条**贴滚动视口右缘/底缘**，无轨道底色。

- 模板与显隐样式：`Styles/ScrollBarTheme.axaml`（覆盖 Semi 的 `{x:Type ScrollBar}` ControlTheme，App.axaml 以 ResourceInclude 挂进 Application.Resources——资源查找先于 Styles 里的 SemiTheme）。
- 显隐状态机：`Controls/ScrollBarOverlay`（附加行为，经 ControlTheme setter 全局启用；显隐翻译成 ScrollBar 的 `overlay-shown` 普通类——Avalonia 12 禁止控件外部添加 `:` 前缀伪类；邻近区判定为纯几何（命中条矩形外扩 `ScrollBarOverlay.NearDistance`），隐藏态不可命中也照样可"接近"；headless 测试经 `ScrollBarOverlay.SetIdleHideDelay` 注入闲置窗）。
- 令牌：`AppScrollBarThumbBrush`（闲置）/ `AppScrollBarThumbHoverBrush`（悬停），随明暗翻转进 ThemeDictionaries（§2，Light 为协调补值）；键名入 `DesignTokenTests.AllTokenKeys` 撞名白名单。
- 契约由 `ScrollBarOverlayTests` 固化：闲置隐藏 / 无箭头 / 滑块贴边（垂直+水平）/ 滚动即显闲置即隐 / 远离滚动条的指针活动不显示 / 移近滚动条即显、闲置收回 / 悬停钉住。

## 10. 界面图标（I-040，lucide 生成资源）

顶栏/侧栏/KPI chip/托盘弹窗的 UI chrome 图标**不是字形也不是令牌**，而是生成资源：
`Styles/Icons.axaml` 由 `scripts/generate-ui-icons.py` 从 npm 包 `lucide-static@1.61.0`
（ISC License, Copyright (c) lucide authors，https://github.com/lucide-icons/lucide）的
静态 SVG 一次性转换（24×24 视框 stroke 语义 → 单个 `StreamGeometry`，path 数据原样保留，
circle/ellipse/rect/line/polyline 转 arc/H/V/A 路径）。**勿手改**；再生：
`python3 scripts/generate-ui-icons.py`（名单与版本 pin 在脚本 ICONS / LUCIDE_VERSION）。

- 起因：原 Unicode 字形图标（⟳ ⚙ ◌ ⌂ ▤ ◉ ⬡ ▣ ◷ ＄ ⌗ ◈ ▣ ↻）在应用未激活任何
  FontFamily 的现实下经系统回退字体（macOS = Apple Symbols）渲染，墨迹只占 em 四到五成，
  34px 按钮里 14pt 字形实际图形仅 6~8px，且 Windows/Linux 回退字体墨迹率不同、跨平台不可控。
- 键名 `AppIcon{Pascal}` 13 个：House/Table/Bot/Cpu/Folder/Clock/DollarCircle/Database/
  Settings（侧栏九页；Settings 兼顶栏入口）、RotateCw（顶栏手动刷新）、LoaderCircle
  （刷新进行中菊花 + 侧栏品牌内圈标）、Coins/DollarCircle/Table/Zap（KPI 四卡；成本卡用
  带圈版本与导航成本图标一致——裸 dollar-sign 竖杆长伸、横端子大敞，15px 下读作「歪」，
  2026-10-24 真机目检退场）。
- 渲染：`PathIcon`（隐式 ControlTheme 在 Icons.axaml，内层 Path 自适应墨迹尺寸 + Center
  对齐、StrokeThickness=1.5 圆角端点定值）。**必须用 PathIcon 而非裸 Path + Stretch=Uniform**：
  Avalonia 的 `Shape.CalculateSizeAndTransform` 只缩放适配并钉在元素左上角（无 WPF 的居中项），
  墨迹在槽内偏移 (槽−墨)/2（$ 左偏 3px、zap 左偏 1.2px，2026-10-24 真机目检「歪/不居中」实锤）；
  PathIcon 内层自定尺寸由布局居中，墨迹恒居中（IconResourceTests 居中回归锁固化）。颜色走
  Foreground 继承（模板 TemplateBinding Foreground）：KPI chip 变体色在用点给
  `AppIconChip{Hue}ForegroundBrush`，导航未选/选中、按钮禁用态随祖先 Foreground 自动生效，
  顶栏按钮给 `AppTextBrush` 显式基线，禁止硬编码色值。
- 数据驱动（导航项）：`ShellPages.Icon` 存图标键名（= AppIcon* 后缀），`Controls/PageIcon.Icon`
  附加属性翻译成 `pi-*` 伪类 → Icons.axaml 的 `PathIcon.app-icon.pi-*` 给 Data（DynamicResource
  不支持动态键名，机制同 §8 AgentChip）。
- 名单同步点（四处）：generate-ui-icons.py ICONS / ShellPages（NavigationItemViewModel.cs）/
  IconResourceTests.GeometryKeys / DesignTokenTests.AllTokenKeys（Semi 撞名白名单）。
- **不替换**：agent/model 厂商兜底散列字形（§8 既有决策）、ModelLogos/AgentLogos 品牌 logo
  （§7/§8）、KPI 涨跌箭头 ↑↓ 与文本连接符 →（文本语义，非 chrome 图标）。
- 品牌标示（侧栏头部 / 弹窗头部）不用本节图标系统：直接 `Image` 引用 D-018 定稿的烘焙资产
  `Assets/app-icon.png`（居中 Apple 网格变体；窗口标题栏用其出血变体 app-icon-window.png）。
  2026-10-24 起替换此前的「圆环+弧形」（侧栏）与「T 字芯片」（弹窗，AppPopoverLogoBackgroundBrush
  渐变令牌随之退场）两个占位。
