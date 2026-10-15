---
id: I-027
title: 设计令牌与共享样式（原型色板 / 卡片 / 系列色 / mini 进度条 / toast）
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

把 UI 原型的视觉规范沉淀为共享资源与样式——色板（明暗两套）、图表系列色、卡片/面板样式、圆角间距字号规范、表格迷你进度条、轻量 toast——供八个页面统一复用，避免各页各写各的样式。

## Background

- 原型观感的核心是 CSS 变量组（`--bg/--panel/--panel2/--line/--muted/--text` + 7 个系列色）与统一的卡片语言（11px 圆角、1px `#24344d` 描边、145deg 渐变、KPI 卡四色图标底）。这些在 Avalonia 里对应画刷与 Style 资源，与主题库无关，是视觉还原度的决定因素之一。
- 设计基准：`docs/design/ccusage_analytics_prototype.html` `<style>` 段 `:root` 与 `.kpi/.panel/.chip/.mini-track` 等选择器。
- 系列色是图表语义色（Agent/模型维度着色），不随明暗翻转，放普通资源不放 ThemeDictionaries。

## Acceptance Criteria

- [x] 原型色板落进 ThemeDictionaries：Dark = 原型原值逐项对应，Light 补一套协调值；画刷命名规范成文（写入本卡或 docs）——Dark 含 `:root` 变量与选择器级色值共 27 组画刷逐键断言；规范成文 docs/design/design-tokens.md
- [x] 7 个系列色定义为共享资源，供图表、表格圆点、环形图共用——`AppSeries0..6Color` + 派生 `AppSeries0..6Brush`（普通资源，明暗不变性有测试断言）
- [x] 共享样式：Card/Panel（圆角描边渐变）、页面标题区、链接按钮、chip、mini 进度条（track + fill）——Styles/SharedStyles.axaml 的 card/panel/kpi/page-title/page-subtitle/panel-title/link-btn/chip/icon-chip/series-dot/mini（ProgressBar.mini）
- [x] 轻量 toast 反馈控件（或采用 Semi 通知组件，结论与理由记录在 Development Log）——选 Semi WindowNotificationManager，封装 IToastService，结论见 Dev Log
- [x] 在临时页面目检明暗两套效果（截图或运行说明记录）——StylePreviewView 临时页 + headless 离屏截图 docs/design/style-preview-light.png / style-preview-dark.png（真机交互目检部分待 PM，见 Verification）

## Subtasks

- [x] 色板逐项映射 + Light 侧补值
- [x] 共享样式资源文件（Styles/*.axaml）
- [x] mini 进度条控件——以 `ProgressBar.mini` 共享样式实现（复用内置 ProgressBar 的 PART_Track/PART_Indicator），未新增 C# 控件，语义同 .mini-track/.mini-fill
- [x] toast 方案选型与实现
- [x] 临时页面目检明暗两套

## Dependencies

- I-026

## Test Cases

### TC-001: 样式目检

Steps:
1. `dotnet run --project src/Tokenmetry.App` 打开样式预览页。
2. 切换明暗主题。

Expected:
- 明暗两套下卡片、文字、描边层次与原型一致，无硬编码色残留于视图。

<!-- 样式类工作以人工目检为准 -->

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

**色板文件归属（决策）**：独立 `src/Tokenmetry.App/Styles/Tokens.axaml`（令牌：28 组 ThemeDictionaries 画刷明暗各一份 + 7 系列色 Color/Brush 对 + mini 填充渐变 = 71 个资源键）+ `src/Tokenmetry.App/Styles/SharedStyles.axaml`（样式类），App.axaml 只挂 `ResourceInclude`/`StyleInclude` 并保留 I-026 的 10 组遗留画刷。理由：App.axaml 已承载根主题 + 遗留画刷，令牌集并入会使其膨胀近一倍；独立文件让 Q-002 清理遗留层时不触碰令牌层；两文件职责分离（"值"与"形"）便于页面卡按需引用。App.axaml 内为 10 组旧画刷补了到新令牌的映射注释（不改名不删除，6 个旧视图零改动继续可用，收敛留 Q-002）。

**色板范围（逐项对应）**：Dark 侧不止 `:root` 六个变量——`.kpi/.panel` 的渐变与描边 `#22324a`、`.linkbtn #80adff`、`.chip` 底/字色、`.mini-track #23334b`、`.kpi-icon` 四色软底 + `.agenticon.gray` 中性软底也一并令牌化（28 组画刷，明暗各一份），这是"统一卡片语言"的完整集合。7 个系列色取自原型 JS `colors=['#4c8dff','#12b886','#ffad4d','#a779ff','#48bde3','#8394ae','#f06bc4']`（`:root` 的 --blue/--cyan/--purple/--green/--orange/--red 是另一组强调色，作为 `AppAccent*Brush` 进 ThemeDictionaries 随明暗翻转——图表内用不翻转的系列色，UI 文字/涨跌指示用会翻转的强调色）。

**Light 补值策略**：同色相、明度反转 + 浅底对比度适配。表面变浅（bg #f4f6fa / panel #ffffff / panel2 #eef1f6 / line #d4dbe6）；文字与强调色加深（muted #66788f / 链接 #2f6bd8 / 六强调色如 blue #2e6bdc、green #0e9e77）保证浅底可读；KPI 图标软底改浅 tint（如 blue #dce9fb）配深前景（#2f66c0）。原型只有暗色主题，Light 侧无原型依据，属设计补值——明暗两套全量色值断言在 DesignTokenTests（期望值表与 Tokens.axaml 同步维护）。

**渐变换算（CSS → Avalonia）**：CSS `linear-gradient(θ)` 方向向量（屏幕坐标 y 向下）为 `(sinθ, −cosθ)`，渐变线长 `L=|W·sinθ|+|H·cosθ|`，起止点=中心 ∓ 方向·L/2，归一化到包围盒即 Avalonia 相对坐标（公式成文 docs/design/design-tokens.md §5）。145deg → 方向 (0.574, 0.819)，方形近似 `StartPoint="10.05%,-7.06%" EndPoint="89.95%,107.06%"`；Avalonia 默认 SpreadMethod=Pad 与 CSS 超界取端色行为一致；非方形卡片存在轻微角度畸变，但原型两 stop 色差极小（#121e31→#101a2b），视觉可忽略。mini 填充 90deg → `(0,50%)→(100%,50%)` 精确。出界负坐标/超 100% 的相对值 XAML 解析与渲染正常（预览页截图佐证）。

**toast 选型（决策）**：采用 Semi 生态的 `WindowNotificationManager` + `NotificationCard`，封装 `IToastService`（按 TopLevel 缓存 manager，BottomCenter 对应原型 .toast 位置，MaxItems 3，2.5s 过期）。理由：① I-026 已全面转 Semi，Semi 12.1.0.1 自带 NotificationCard 的 Light/Dark/Shared 样式，主题自适应零成本；② 通知管理器内置动画、到期自动消失、点击关闭；③ 自绘 overlay 需重复 adorner/计时/动画基建，收益只剩还原原型胶囊底色 #203651 一个色值。实现中发现一个真实时序坑并修复：manager 的卡片加入其模板 `PART_Items` 面板，而模板按需应用——首次 `Show` 早于模板应用时卡片会进 Show 内部的兜底 List 永远不显示，`ToastService` 在 Show 前 `ApplyTemplate()` 规避（headless 测试 `Toast_service_shows_notification_card_in_host_window` 固化）。

**mini 进度条选型（决策）**：不新增 C# 控件，复用内置 `ProgressBar` + `ProgressBar.mini` 类样式整体替换 Template（`PART_Track`/`PART_Indicator` 命名保留，宽度由 ProgressBar 自身代码按 Value 0-100 计算）。类选择器优先级高于 Semi 的 ControlTheme，模板覆盖生效（SharedStyles.axaml 挂在 SemiTheme 之后）。Value 语义、indeterminate 扩展空间免费获得；headless 测试断言 40%/80% 两档填充宽度与轨道宽度成比例。

**命名规范位置（决策）**：成文于 `docs/design/design-tokens.md`（8 张页面卡 D-006~D-013 与 S-002/Q-002 都要引用，独立文档比埋在卡里更可查），要点：新键一律 `App*` 前缀；跟界面表层走的进 ThemeDictionaries（`App{语义}{角色}Brush`），跟数据语义走的不翻转（`App{名}Color` + `App{名}Brush` StaticResource 派生）；系列色索引自 0 对齐原型 `colors[i]`；样式类名对齐原型 CSS 类（card/panel/kpi/chip/page-title/link-btn/mini…）；新增键先对独立 SemiTheme 实例 TryGetResource 查重（headless 测试固化，方法同 I-026）。

**临时预览页**：选独立临时视图 `StylePreviewView` + MainWindow 新增一个 TabItem（仿 I-026 把 DataGrid 演示挂 TrendSpike 的"临时页"模式，但该页已被 ScottPlot+DataGrid 占满，独立页更清晰；侵入 = 1 个 TabItem）。页面自身零硬编码色值，覆盖全部令牌/样式类（KPI 四色图标卡、chips、link-btn、mini 进度条、系列色圆点、明暗切换与 toast 按钮）。同步把 I-026 测试的 MainWindow TabCount 断言 5→6（注释留痕）。明暗截图由 headless 离屏渲染：测试宿主改 `.UseSkia()` + `UseHeadlessDrawing=false`（默认 headless drawing 管线无合成帧可截，CaptureRenderedFrame 返回空），截图前需 `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` 触发一帧；重生成命令见 Verification。

**遗留风险**：
- TC-001 的真机交互目检（`dotnet run` 打开预览页点按主题/toast 按钮）未执行，明暗效果以 headless 整窗截图（docs/design/style-preview-*.png）为证，真机点击路径留待 PM 验收。
- link-btn 悬停底色保留 Semi 默认行为（非纯透明），与原型"无 hover 反馈"略有差异，观感可接受；如需完全还原可在样式内覆盖 pointerover 模板。
- 原型 font-weight 650 以 SemiBold(600) 近似（Avalonia 字重枚举不含 650）；LetterSpacing -0.4 按 DIP 经验值（原型为 px）。
- 截图的中文字形来自本机 macOS 系统字体（headless 走真实 Skia 字体管理器），CI Linux 环境如重跑截图字形可能不同（截图为一次性留档，不影响测试断言）。
- 测试宿主改 Skia 渲染后 I-026 的 12 个既有用例全过（824/22），但后续若有用例依赖 headless drawing 特有行为需注意该前置已变。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，声明逐条核实为真（Dark 色板与原型 28 组逐键比对全部命中、系列色确不翻转、渐变换算方向正确、ApplyTemplate 时序修复经 Avalonia 源码证实真实必要、截图明暗差异明显且可读、无超范围改动、规程合规）。4 条可动手 P2 当场由 PM 修复：
1. 145deg 渐变起止点校正为精确解 `10.058%,-7.043% → 89.942%,107.043%`，并在 design-tokens.md 量化角度畸变（340×124 约 27.5°，因两 stop 色差约 2% 亮度接受）；
2. mini 进度条测试补 `Assert.Same(AppMiniTrackBrush 实例, track.Background)` 直接证明自定义模板生效（注意按控件 ActualThemeVariant 解析，headless 默认为 Light）；
3. ToastService 的 manager 缓存由 Dictionary 改 ConditionalWeakTable，消除 TopLevel 关闭后的强引用泄漏；
4. 审查记录性备注（agenticon 三变体未令牌化为有意取舍、Semi 键计数未复核、link-btn hover 保留 Semi 行为）无需改动，留档备查。修复后 824 通过 / 22 跳过 / 0 失败、0 警告维持。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Tokenmetry.App/Styles/Tokens.axaml`（新建：28 组 ThemeDictionaries 明暗画刷 + 7 系列色 Color/Brush + AppMiniFillBrush = 71 个资源键；Dark 全为原型原值，逐行注释标注来源选择器）
  - `src/Tokenmetry.App/Styles/SharedStyles.axaml`（新建：card/panel/kpi/page-title/page-subtitle/panel-title/link-btn/chip/icon-chip/series-dot/ProgressBar.mini 共享样式类）
  - `src/Tokenmetry.App/App.axaml`（挂载 Tokens.axaml 与 SharedStyles.axaml；10 组遗留画刷补映射注释，不改名不删除）
  - `src/Tokenmetry.App/Services/ToastService.cs`（新建：IToastService + ToastService，Semi WindowNotificationManager 封装，Show 前 ApplyTemplate 规避模板时序坑）
  - `src/Tokenmetry.App/DependencyInjection/AppServices.cs`（注册 IToastService）
  - `src/Tokenmetry.App/Views/StylePreviewView.axaml(.cs)`（新建临时预览页，零硬编码色值，随 v0.4 页面卡落地删除）
  - `src/Tokenmetry.App/Views/MainWindow.axaml`（新增 Style Preview (I-027) Tab）
  - `docs/design/design-tokens.md`（新建：命名规范 / ThemeDictionaries vs 普通资源规则 / 系列色表 / 样式类表 / 渐变换算公式）
  - `docs/design/style-preview-light.png`、`docs/design/style-preview-dark.png`（headless 整窗离屏截图留档）
  - `tests/Tokenmetry.App.Headless.Tests/Styles/DesignTokenTests.cs`（新建：9 个用例）
  - `tests/Tokenmetry.App.Headless.Tests/Theming/SemiTestAppBuilder.cs`（改：UseSkia + UseHeadlessDrawing=false，截图需要真实 Skia 合成帧）
  - `tests/Tokenmetry.App.Headless.Tests/Theming/SemiThemeMigrationTests.cs`（改：MainWindow TabCount 断言 5→6，因新增预览页 Tab）
- How to run/verify:
  - 自动化：`dotnet build Tokenmetry.sln`（Debug/Release 均 0 警告 0 错误）+ `dotnet test Tokenmetry.sln`。
  - 令牌/样式/toast/截图（自动化，已执行）：`dotnet test tests/Tokenmetry.App.Headless.Tests`（21 用例），覆盖：Dark 色板逐键等于原型原值、Light 协调值、卡片/mini 渐变 stop 与几何、7 系列色明暗不变性、全部新键对 SemiTheme 查重无冲突、panel/kpi/chip 样式在窗口内真实应用令牌（Assert.Same App 资源实例）、mini 进度条按 Value 填充、ToastService 弹出 NotificationCard、预览页 Light/Dark 离屏渲染。
  - 截图重生成：`TOKENMETRY_STYLE_PREVIEW_DIR=<仓库绝对路径>/docs/design dotnet test tests/Tokenmetry.App.Headless.Tests --filter FullyQualifiedName~Style_preview_page_renders`（须用绝对路径，测试宿主 cwd 在 bin 目录）。
  - TC-001 人工（**待人工验证**）：`dotnet run --project src/Tokenmetry.App` → "Style Preview (I-027)" 页 → 点 日间/夜间/跟随系统 观察令牌换色、点 弹出 Toast 验证反馈。明暗观感已有截图佐证，真机点击路径留待 PM。
- Results:
  - Debug / Release 构建 0 警告 0 错误。
  - `dotnet test Tokenmetry.sln`：**824 通过 / 22 跳过 / 0 失败**（Core 99+0、Infrastructure 612+3、Reconciliation 49+19、App 43+0、App.Headless 21+0）≥ 基线 815 / 22 / 0，无回归；新增 9 个 headless 用例全过（12→21）。
  - TC-001 截图留档：`docs/design/style-preview-light.png`、`docs/design/style-preview-dark.png`（880×720，整窗合成帧，明暗两套令牌/卡片/进度条/系列色全部可见）；人工目检部分待 PM 执行。
