---
id: D-005
title: 自绘环形图与热力图控件
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

实现两个可复用自绘控件：环形图（多段弧 + 中心总量 + 图例列表，对应原型 donut）与活跃日历热力图（日强度着色格子 + 悬停提示），供总览页与时间分析页使用。

## Background

- 原型 donut 用 SVG stroke-dasharray 圆弧实现（约 6 行核心逻辑），热力图是 35 个着色格子；逻辑简单，自绘可控性最高，不值得为它们上图表库。
- Avalonia 无内置 Arc shape，用 PathGeometry 圆弧段或 StreamGeometry；环形中心文字用 Grid 叠层。
- 颜色映射 I-027 的系列色资源；明暗两套（中心文字、格子边框、空数据色）。

## Acceptance Criteria

- [x] DonutChart 控件：数据源驱动多段弧、中心标题/副标题、图例列表（名称 + 百分比），段序映射系列色
- [x] Heatmap 控件：按日强度着色、hover 显示当日数值、空数据样式、列数/行数可配
- [x] 明暗两套目检通过
- [x] 数据量边界（>2 段、单段 100%、全空）渲染正常

## Subtasks

- [x] DonutChart 控件（弧段几何 + 图例）
- [x] Heatmap 控件（格子 + 悬停）
- [x] 边界情况与明暗目检

## Dependencies

- I-027（系列色与配色）

## Test Cases

### TC-001: 环形图边界

Steps:
1. 预览页分别传入 1 段 / 2 段 / 7 段 / 空数据。

Expected:
- 单段满圆、多段比例正确、空数据不崩且显示占位。

### TC-002: 热力图悬停

Steps:
1. 悬停不同强度的格子。

Expected:
- 提示显示当日数值，颜色深浅与强度单调一致。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- **DonutChart 落地**（`src/CCUsage.App/Controls/DonutChart.axaml(.cs)`）。
- **几何实现方式**：每段一个 `Path`，`StreamGeometry` 构造填充环段——外弧（顺时针 ArcSegment）→ 内边连线 → 内弧（逆时针）→ 闭合；0° = 顶部、顺时针为正（等价原型 svg `rotate(-90deg)` 的 dasharray 弧序）。单段 100% 时 ArcSegment 起终点重合退化为零长，拆成两个 180° 半弧（几何纯函数 `BuildRingSegment` 单测覆盖：整环包围盒 = 外径、四分之一环象限正确）。角度分配 `BuildSweeps`（纯函数）：比例扫角 + 末段吃掉累计舍入，角度和恒为 360°，全零/空集返回空。
- **数据源 API 选型**：选**标准样式属性 + VM 集合**（`Segments` 为 `IEnumerable`，元素 `DonutSegment(Name, Value, Color?)`，init-only 小对象可直接进 `ObservableCollection`；控件订阅 `INotifyCollectionChanged` 增量重建、detach 退订），弃 AttachedProperty——与 ItemsControl 生态绑定习惯一致、XAML 直接可写、可测试性相同，且不引入附加属性样板。段色显式 `Color` 优先，缺省按段序映射 `AppSeries{0..6}Color`（超 7 段循环，对齐原型 `colors[i % colors.length]`，普通资源不随明暗翻转）。
- **图例**：ItemsControl 行模板 = swatch（7×7 圆角 3）+ 名称（超宽 `CharacterEllipsis`）+ 百分比（`F1`，对齐原型 `toFixed(1)`），行悬停 ToolTip 显示绝对值；环段本体也有 ToolTip。布局坑一次修掉：根布局最初用横向 StackPanel（横向测量给无限宽 → 图例行按名字全宽展开溢出面板），改 `Grid ColumnDefinitions="Auto,*,Auto"` 用 `*` 列把图例约束在环右侧剩余宽度内。
- **Heatmap 落地**（`src/CCUsage.App/Controls/Heatmap.axaml(.cs)`）：`UniformGrid` 排格子，`Columns` 可配（默认 7，行数 = ceil(n/Columns)）、`CellHeight` 可配（默认 27，原型值）、格距 6px（原型 gap）。悬停用 `ToolTip.SetTip`（有数据日 `yyyy-MM-dd · N tok` 千分位；补零日 `· 无数据`）。
- **强度色阶映射**：对齐原型 `rgba(76,141,255, .12+v*.78)`——即 `AppSeries0Color`（#4c8dff）的 alpha 线性渐变，纯函数 `IntensityColor(baseColor, ratio)`（ratio ∈ [0,1] → alpha ∈ [0.12, 0.90]，色相保持、深浅单调，headless 单测覆盖端点与单调性）；系列色本就是"跟数据语义不翻转"的令牌，**无需新增令牌**。无数据格（`HasData=false`，I-028 补零日）用 `AppPanelAltBrush` 令牌底 + `AppCardLineBrush` 描边，与最低强度色明显区分；空集合整体呈"暂无数据"文案。样式全部 DynamicResource，明暗自动翻转，无需手工重建。
- **测试**：新增 headless 12 例（`tests/CCUsage.App.Headless.Tests/Controls/DonutChartTests.cs`、`HeatmapTests.cs`）：弧段数量/角度和=360°/满圆拆弧/四分之一环象限、1/2/7 段渲染与图例百分比、空数据与全零占位、ObservableCollection 增量更新、明暗截图留档；强度色单调/端点、格子数与列数/行高、tooltip 文案（千分位与"无数据"）、空数据占位、补零格令牌底。注意点：断言空态用"有效可见性"（自身+祖先 IsVisible），子节点 IsVisible 不随父级隐藏翻转。
- **预览接线**：与 D-004 共用 `ChartPreviewViewModel`——donut 用真实 `GetAgentBreakdownAsync`（Top6 + "其他"聚合，≤7 段）或 fixture 五段；heatmap 取日序列尾部 35 格。
- **遗留风险**：①真机悬停手感与"占位环/中心文字"的明暗目检以离屏截图留档为准（本机自动化环境无法拉起桌面会话），真机可经 `CCUSAGE_PREVIEW_WINDOW=1 dotnet run --project src/CCUsage.App` 复核；②中心文字当前不自动截断，超长 `CenterTitle` 可能撑出环（页面卡使用时控制字数或后续加 MaxWidth）；③热力图 tooltip 走 Avalonia ToolTip（系统延迟样式），若页面卡要求原型式即时 overlay 再换自绘。

### 2026-10-13（独立审查修复轮）

- **[P2] 图例百分比修复**：`Rebuild` 的占比计算去掉 `Math.Max(1, total)` 整数下限——单段小值（total<1）此前会被
  拉成 50%，现按真实占比显示 100%（`total<=0` 时 BuildSweeps 已判空走占位，非空分支 total 恒 >0；防御分支显示 0%）。
  headless 测试 `Single_segment_below_one_still_shows_100_percent` 锁定。
- **[P2] INCC 订阅对称化**：Donut/Heatmap 的集合订阅由"属性变更时挂、detach 时退"改为 Attach/Detach 对称重挂
  （`OnAttachedToVisualTree` 先退订再订阅防重复 + Rebuild；`OnDetachedFromVisualTree` 退订）——detach→reattach 后
  集合变更仍生效，两控件各补 `Collection_updates_still_apply_after_detach_and_reattach` 测试。
- 配套（与 D-004 共用文件）：`ChartPreviewViewModel` fixture 环心总量与 Agent 段同源（环心与图例合计一致）、
  LoadAsync 序号防护；Verification 补截图再生成命令。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Controls/DonutChart.axaml(.cs)`、`src/CCUsage.App/Controls/Heatmap.axaml(.cs)`、
  `src/CCUsage.App/ViewModels/ChartPreviewViewModel.cs`、`src/CCUsage.App/Views/StylePreviewView.axaml(.cs)`（演示区）、
  `tests/CCUsage.App.Headless.Tests/Controls/DonutChartTests.cs`、`tests/CCUsage.App.Headless.Tests/Controls/HeatmapTests.cs`、
  `tests/CCUsage.App.Headless.Tests/Controls/ChartTestHelpers.cs`
- How to run/verify:
  - TC-001（自动化）：`dotnet test tests/CCUsage.App.Headless.Tests --filter FullyQualifiedName~DonutChartTests`
    ——`BuildSweeps` 角度和=360°、单段满圆拆弧（包围盒=外径）、1/2/7 段弧数与图例百分比、空/全零占位、
    ObservableCollection 增量重建、明暗两套渲染（系列色像素命中）。
  - TC-002（自动化）：`dotnet test tests/CCUsage.App.Headless.Tests --filter FullyQualifiedName~HeatmapTests`
    ——强度色 alpha 单调 + 端点 0.12/0.90、格子数/列数/行高、tooltip 文案（千分位 / "无数据"）、补零格令牌底、
    空数据占位、明暗两套渲染。
  - 明暗目检：截图留档（下）替代真机目检；真机复核入口 `CCUSAGE_PREVIEW_WINDOW=1 dotnet run --project src/CCUsage.App`
    （StylePreviewView"图表控件"区，悬停热力图格子看 tooltip、切明暗按钮看两套配色）。
  - 截图再生成命令（P2-3）：`CCUSAGE_CHART_PREVIEW_DIR=docs/design CCUSAGE_STYLE_PREVIEW_DIR=docs/design
    dotnet test tests/CCUsage.App.Headless.Tests`（环境变量未设置时仅断言可渲染，不写文件）。
- Results: 全仓 `dotnet test CCUsage.sln` → **908 通过 / 22 跳过 / 0 失败，0 警告**（基线 860 + 48 新增，无回归；
  审查修复轮净增 11 例）；截图留档 `docs/design/donut-{light,dark}.png`、`docs/design/heatmap-{light,dark}.png` 与
  `docs/design/style-preview-{light,dark}.png`（整页含两控件）。
