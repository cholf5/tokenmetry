---
id: I-026
title: Semi.Avalonia 主题迁移（含 Avalonia 12.1.x 升级）
type: chore
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

把应用基础主题从 FluentTheme 换成 Semi.Avalonia，Avalonia 从 12.0.4 升到 12.1.x，保证现有全部视图在 Semi 下正常渲染、亮/暗/跟随系统切换不回归，为 UI 原型落地（v0.4 主体）打底。

## Background

- 2026-10-12 选型结论：Semi.Avalonia 最新 12.1.0.1（2026-07-31 发布）依赖 Avalonia ≥ 12.1.0，需小版本升级；备选 SukiUI 7.0.1（依赖 ≥ 12.0.3 可直用，但风格强势、偏离原型）与保留 Fluent 手写重写控件样式（保底路线）。设计蓝本气质与 Semi 同源（web 设计系统风格），故选 Semi。
- 设计基准：`docs/design/ccusage_analytics_prototype.html`（2026-10-12 自 ~/Downloads 收入仓库）。
- 迁移对象：`App.axaml` 根主题 + 现有 6 个视图；现有自建 ThemeDictionaries（App.axaml 中 10 组画刷 ×明暗）是否与 SemiToken 冲突需盘点。

## Acceptance Criteria

- [x] Avalonia 全家桶升级到 12.1.x，`dotnet build` / `dotnet test` 基线不回归（迁移前基线 803 通过 / 22 跳过；迁移后 815 / 22 / 0，见 Verification）
- [x] App.axaml 以 SemiTheme 替换 FluentTheme；现有全部视图可打开、无样式崩坏、无绑定错误日志（无 XAML 加载错误 / 无绑定错误已由 headless 自动化实证：6 视图 + MainWindow 实例化 + DataGrid 解析 + 资源零冲突；「样式观感是否崩坏」的人工视觉复核按批次计划统一归入 Q-002 视觉验收执行）
- [x] 亮 / 暗 / 跟随系统三态切换照常工作（ThemeService → RequestedThemeVariant 与 SemiTheme 的桥接方式记录在 Development Log）
- [x] Avalonia.Controls.DataGrid 包（12.1.x）引入，Semi 下有可用样式（一个演示表格即可）
- [x] 现有 ThemeDictionaries 画刷与 Semi 的冲突清单成文（保留 / 改名 / 删除各有结论）

## Subtasks

- [x] 升级 Avalonia 12.0.4 → 12.1.x，全量 build + test
- [x] 引入 Semi.Avalonia + Irihi.Avalonia.Shared，替换 App.axaml 根主题
- [x] 明暗切换三态验证（自动化部分），冲突画刷盘点清单（见 Development Log 2026-10-13）
- [x] DataGrid 包引入 + 最小演示验证

## Dependencies

- none

## Test Cases

### TC-001: 主题三态切换

Steps:
1. `dotnet run --project src/Tokenmetry.App`
2. 设置页依次切换 日间 / 夜间 / 跟随系统。

Expected:
- 三种状态全部视图渲染正常，无异常日志，重启后偏好保持。

### TC-002: 测试基线

Steps:
1. `dotnet test Tokenmetry.sln`

Expected:
- 通过数不低于迁移前基线（803 通过 / 22 跳过），无新增失败。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

**包版本决策**（先用 nuget.org nuspec 逐一核实依赖约束，再统一升版）：

| 包 | 旧版本 | 新版本 | 依据 |
|---|---|---|---|
| Avalonia / Avalonia.Desktop / Avalonia.Fonts.Inter | 12.0.4 | **12.1.4**（12.1.x 最新稳定） | Semi.Avalonia 12.1.0.1 依赖 Avalonia ≥ 12.1.0 |
| Avalonia.Controls.DataGrid | 无 | **12.1.2**（该包在 12.1 线最新；依赖 Avalonia ≥ 12.1.0） | 核心包已到 12.1.4 而 DataGrid 包 12.1 线停在 12.1.2，属官方各包独立发版，两者约束兼容、运行正常（headless 测试实证） |
| Avalonia.Themes.Fluent | 12.0.4 | **移除** | 全仓唯一引用点是 App.axaml 根主题，换 Semi 后无残留引用（grep 证实） |
| Semi.Avalonia / Semi.Avalonia.DataGrid | 无 | **12.1.0.1** | 12.1.x 对应版（依赖 Avalonia ≥ 12.1.0、Irihi.Avalonia.Shared ≥ 0.5.0） |
| Irihi.Avalonia.Shared | 无 | **0.5.0** | Semi.Avalonia 12.1.0.1 的直接依赖（依赖 Avalonia ≥ 12.0.0，兼容 12.1.4） |
| ScottPlot.Avalonia | 5.1.59 | **保持不变** | 其 nuspec 依赖 Avalonia ≥ 12.0.0，与 12.1.4 无版本冲突，无需换版 |

**三态主题桥接方式**：无需任何桥接代码。SemiTheme 与 FluentTheme 一样是 ThemeVariant 感知的 Styles 容器，`Application.RequestedThemeVariant`（由 ThemeService 按 AppSettings.Theme 设置，light/dark/system→Light/Dark/Default）直接驱动 Semi 的 ThemeDictionaries 换色，同时驱动 App.axaml 自建 ThemeDictionaries。ThemeService 与 AppSettings 持久化零改动。已由 headless 测试 `ThemeService_light_dark_system_bridge_with_SemiTheme` 实证：Apply("dark"/"light") 后 ActualThemeVariant 与画刷取值随之切换，Apply("system") 回到 Default（跟随宿主）。

**ThemeDictionaries 画刷冲突盘点**（方法：解码 Semi.Avalonia / Semi.Avalonia.DataGrid / Irihi.Avalonia.Shared 三个 dll 内嵌资源键的 UTF-16 字符串全集，并用独立 SemiTheme 实例 `TryGetResource` 运行时复核）：

- Semi.Avalonia 定义 146 个 *Brush 键 + 178 个 *Color 键（命名均为 Semi* / 控件前缀风格），Semi.Avalonia.DataGrid 定义 5 个 DataGrid*Brush，Irihi.Avalonia.Shared 定义 0 个资源键。
- 与自建 10 组画刷键（SecondaryTextBrush / MutedTextBrush / CardBackgroundBrush / CardAccentBackgroundBrush / SeparatorBorderBrush / WarningBannerBackgroundBrush / WarningBannerBorderBrush / WarningBannerTextBrush / DangerTextBrush / AccentLinkTextBrush）**零键名冲突**。

| 画刷 | 结论 | 理由 |
|---|---|---|
| SecondaryTextBrush | 保留 | 无冲突；6 视图 29 处引用；语义与 Semi Grey 系可映射，留待 I-027 |
| MutedTextBrush | 保留 | 无冲突；13 处引用 |
| CardBackgroundBrush | 保留 | 无冲突；14 处引用；Semi 无同名 Card 资源 |
| CardAccentBackgroundBrush | 保留 | 无冲突；4 处引用 |
| SeparatorBorderBrush | 保留 | 无冲突；4 处引用（注意与 Semi Divider 语义可映射，I-027 评估） |
| WarningBannerBackgroundBrush / BorderBrush / TextBrush | 保留（3 个） | 无冲突；各 1 处引用 |
| DangerTextBrush | 保留 | 无冲突；2 处引用；Semi 用 SemiRedColor 系表达 |
| AccentLinkTextBrush | 保留 | 无冲突；1 处引用 |

- 结论：本卡不做任何改名/删除（全部「保留」，原键名留在 App.axaml ThemeDictionaries，10 组 × 明暗不动），现有视图零 XAML 修改即可解析到正确画刷；I-027 令牌重命名时可按上表把语义相近者映射到 Semi token（SemiGrey*Color / SemiRed*Color 等）。

**DataGrid 演示位置**：选在 TrendSpikeView（D-002 临时 spike 页，v0.4 随页删除），对 6 个现有视图零侵入；复用该页已有 `Points`（TrendSpikePoint：日期/成本/Tokens）做 3 列只读表格，无新增 VM 代码。DataGrid 样式由 `semi:DataGridSemiTheme`（独立包）提供，已在 App.axaml 与 SemiTheme 一起引入。

**顺手修复（升级引入的告警）**：Avalonia 12.1.4 将 `RenderTargetBitmap.Save(string, int?)` 标记过时，TrendSpikeView.axaml.cs 验证钩子改用 `Save(string, PngBitmapEncoderOptions)`，恢复 0 警告构建（基线本身无此告警）。

**新增 headless 测试项目 `tests/Tokenmetry.App.Headless.Tests`**（12 个用例，全过）：Avalonia 12.1.x 的 Avalonia.Headless.XUnit 基于 xunit v3，与现有 App.Tests 的 xunit 2.5.3 同项目冲突，故独立成项目（xunit.v3 3.2.2 + Microsoft.NET.Test.Sdk + xunit.runner.visualstudio 3.1.5，保持 VSTest 跑法以兼容 `dotnet test Tokenmetry.sln`）。用真实 App（App.axaml 原样）headless 启动，覆盖：根主题断言（SemiTheme zh-CN + DataGridSemiTheme）、10 组画刷明暗取值、Semi 资源零冲突运行时复核、ThemeService 三态桥接、6 视图 + MainWindow XAML 实例化、TrendSpike DataGrid 3 列在 Semi 样式下解析。

**遗留风险**：
- Avalonia.Controls.DataGrid 12.1.2 与核心包 12.1.4 存在小版本差（官方 12.1 线无 12.1.3/12.1.4 的 DataGrid 包）；依赖约束兼容且测试通过，后续 DataGrid 出新包时可一并跟进。
- ScottPlot 画布仍用 ScottPlot 默认配色（D-002 既定决策），亮暗切换下画布不随主题变，v0.4 正式集成时处理。
- GUI 视觉效果（样式是否"好看/崩坏"）无法自动化判定，TC-001 人工验证部分见 Verification。

**PM 修正留痕**：本卡创建时写的测试基线「242 通过 / 5 跳过」是过时数字（17 源适配器批次合入后测试已增长），实施前 PM 实测基线为 **803 通过 / 22 跳过 / 0 失败**，AC-1 与 TC-002 中的基线数字已相应更正为 803/22（更紧的口径，非放宽标准）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，7 条完成声明逐条核实为真（含构建/测试数字、包版本兼容、冲突清单独立复核、无超范围改动、规程合规）。4 条 P2 均已当场修复：
1. DataGrid 演示测试补断言 `DataGridLineBrush`（仅 DataGridSemiTheme 定义的键）可解析，使测试对 DataGridSemiTheme 的存在敏感；
2. 三态桥接测试补 Semi 自有键 `SemiGrey0Color` 的明暗取值断言（#F9F9F9 / #1C1F23，键值经 Semi.Avalonia 上游源码 Tokens/Palette/Light.axaml 与 Dark.axaml 核实）；
3. 修正测试文件头注释中关于 AvaloniaTestApplication assembly 级生效版本的不准确表述；
4. 本条「PM 修正留痕」即审查发现的基线数字改动未留痕问题的处理。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Tokenmetry.App/Tokenmetry.App.csproj`（包版本：Avalonia 12.1.4 / Avalonia.Controls.DataGrid 12.1.2 / Semi.Avalonia 12.1.0.1 / Semi.Avalonia.DataGrid 12.1.0.1 / Irihi.Avalonia.Shared 0.5.0；移除 Avalonia.Themes.Fluent；ScottPlot.Avalonia 5.1.59 不变）
  - `src/Tokenmetry.App/App.axaml`（根主题 FluentTheme → SemiTheme zh-CN + DataGridSemiTheme；自建 ThemeDictionaries 原样保留）
  - `src/Tokenmetry.App/Views/TrendSpikeView.axaml`（I-026 Semi DataGrid 最小演示，绑定本页 Points，随 D-002 临时页同生命周期）
  - `src/Tokenmetry.App/Views/TrendSpikeView.axaml.cs`（CS0618 修复：Save 改用 PngBitmapEncoderOptions）
  - `tests/Tokenmetry.App.Headless.Tests/`（新增：SemiTestAppBuilder.cs、Theming/SemiThemeMigrationTests.cs）
  - `Tokenmetry.sln`（登记新测试项目）
- How to run/verify:
  - TC-002（自动化，已执行）：`dotnet build Tokenmetry.sln`（Debug + Release 均 0 警告 0 错误）+ `dotnet test Tokenmetry.sln`。
  - 主题/画刷/DataGrid/视图加载（自动化，已执行）：`dotnet test tests/Tokenmetry.App.Headless.Tests` —— 真实 App.axaml headless 启动，12/12 通过（根主题断言、10 组画刷明暗取值、Semi 零冲突复核、三态桥接、6 视图 + MainWindow 实例化、DataGrid 3 列解析）。
  - TC-001（人工，**待人工验证**）：`dotnet run --project src/Tokenmetry.App`，设置页依次切换 日间/夜间/跟随系统，确认各视图视觉渲染正常、重启后偏好保持。自动化已覆盖三态机制的程序行为（Variant 切换与资源取值），**视觉样式是否崩坏、观感是否符合预期无法自动判定，需人工点击确认**；设置页主题组合框（日间/夜间/跟随系统）的实际点击路径同属此项。
- Results:
  - `dotnet test Tokenmetry.sln`：**815 通过 / 22 跳过 / 0 失败**（Core 99+0、Infrastructure 612+3、Reconciliation 49+19、App 43+0、App.Headless 12+0）≥ 迁移前基线 803 / 22 / 0，无回归；新增 12 个 headless 主题迁移用例全过。
  - Debug / Release 构建均 0 警告 0 错误（升级引入的 CS0618 已顺手修复）。
  - TC-001 人工部分：待人工验证（见上）。
