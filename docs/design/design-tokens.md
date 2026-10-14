# 设计令牌与样式命名规范（I-027）

来源：UI 原型 `docs/design/ccusage_analytics_prototype.html`（v0.4 设计基准）。
实现：`src/TokenPulse.App/Styles/Tokens.axaml`（令牌）+ `src/TokenPulse.App/Styles/SharedStyles.axaml`（共享样式）。
本文档是 8 个页面卡（D-006 ~ D-013）与 S-002/Q-002 引用的约定。

> D-003 补充：外壳专用令牌 `AppSidebarBackgroundBrush` / `AppTopbarBackgroundBrush` /
> `AppNavActiveBackgroundBrush` / `AppNavActiveForegroundBrush` / `AppNavActiveIndicatorBrush` /
> `AppNavHoverBackgroundBrush`（Dark 侧对应原型 `.sidebar/.topbar/.nav` 选择器原值，Light 为协调补值）
> 已进 ThemeDictionaries；外壳导航样式（`ListBox.nav-list` 等）留在 `MainWindow.axaml` 的 Window.Styles
> 内（外壳私有，页面卡勿复用），页面级样式仍从 SharedStyles.axaml 取。

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
| `icon-chip` | Border | `.kpi-icon` | 30×30 圆角 9；颜色按变体给 `AppIconChip{Hue}BackgroundBrush`/`ForegroundBrush`（blue/amber/purple/green/neutral） |
| `series-dot` | Border | `.modeldot` | 7×7 圆角 3；颜色给 `AppSeries{N}Brush` |
| `mini` | ProgressBar | `.mini-track`/`.mini-fill` | 4px 高；Value 0-100 直接驱动填充宽度（内置 PART_Track/PART_Indicator） |

## 5. 渐变角度换算（CSS → Avalonia）

CSS `linear-gradient(θ, ...)`：0deg 指向上、顺时针；方向向量（屏幕坐标 y 向下）`(sinθ, −cosθ)`；渐变线长 `L = |W·sinθ| + |H·cosθ|`；起止点 = 中心 ∓ 方向·L/2，再归一化到包围盒（Avalonia `MappingMode=RelativeToBoundingBox`，默认 SpreadMethod=Pad 与 CSS 超界取端色一致）。

- 145deg（卡片）：`(0.574, 0.819)`，方形近似下 `StartPoint="10.058%,-7.043%" EndPoint="89.942%,107.043%"`（精确解）；非方形卡片存在角度畸变——固定相对坐标下渐变俯角随宽高比变化（如 340×124 卡约 27.5°，CSS 原值 55°；225×124 KPI 卡约 38°），但原型两 stop 仅 `#121e31→#101a2b`（约 2% 亮度差），方向偏差视觉不可感知，接受近似；页面卡若需逐像素还原可按宽高比换算该页专属起止点。
- 90deg（mini 填充）：`StartPoint="0%,50%" EndPoint="100%,50%"`，精确。

## 6. 令牌 ↔ 原型色值对照

Dark 列全部为原型原值（`:root` 变量 + `.kpi/.chip/.linkbtn/.mini-track/.kpi-icon/.agenticon` 选择器色值）；Light 列为 I-027 设计的协调值（同色相、明度反转、浅底上加深保证对比度）。完整对照见 `Styles/Tokens.axaml` 内逐行注释与 `tests/TokenPulse.App.Headless.Tests/Styles/DesignTokenTests.cs` 的期望值表（测试与令牌文件同步维护）。
