---
id: Q-002
title: 旧视图清理与视觉验收打磨
type: refactor
priority: P2
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

v0.4 UI 批次收尾质量卡：移除旧视图与死代码，统一滚动条 / 行 hover / 焦点样式，空态复用 EmptyStateView，八个页面亮暗两套逐页目检并对照原型出差异清单。

## Background

- 批次内各卡各自目检，本卡是跨页一致性 pass：导航态、全局筛选态、空数据态、大数据量态统一定性。
- 差异清单原则：与原型一致的记还原完成；有意偏离的（如不做响应式断点、系统窗口边框）记录理由，不算缺陷。

## Acceptance Criteria

- [x] 旧 DashboardView / ReportsView / ModelBreakdownView / TrendSpikeView 及死代码清除，导航无残留（grep + 反射结构锁双验证；另清理 EmptyStateView、旧 CSV 导出线、10 组旧画刷、DataGrid 包）
- [x] 8 页 × 亮暗两套全量目检，差异清单成文（docs/design/q-002-visual-diff.md，接受的差异附理由；自动化截图 + 逐张人工核对由执行人完成，真机交互复核留 PM/用户）
- [x] 键盘可达性：Tab 焦点顺序、focus ring 在 Semi 下正常，无不可达控件（headless 锁 Tab 遍历可达性：顶栏筛选/设置入口/侧栏导航；focus ring 观感与读屏属真机复核项，如实标注）
- [x] 空态 / 加载态统一（盘点结论：8 个真实页已统一经 SharedStyles 的 table-empty 类 + 各页诚实错误态；EmptyStateView 为旧 Dashboard 专属大空态（绑定 DashboardViewModel），随旧视图删除）
- [x] `dotnet test Tokenmetry.sln` 基线不回归（清理前 1030 过/22 跳/0 败 → 清理后 1010 过/22 跳/0 败，差额 = 随死代码删除 27 个旧用例（3 个整删测试文件的 24 例 + SemiThemeMigrationTests 内 3 个方法）− 新增 7 个（3 个新测试 + Existing_views 理论 6→10 的 +4；审查后再加 4 例 ScrollViewer 结构锁，清理后总数为 1014 过），0 失败）

## Subtasks

- [x] 旧视图与死代码清除
- [x] 全量目检 + 差异清单
- [x] 键盘可达性与焦点样式
- [x] 空态/加载态统一

## Dependencies

- D-006, D-007, D-008, D-009, D-010, D-011, D-012, D-013, S-002（全部完成后执行）

## Test Cases

### TC-001: 全量回归

Steps:
1. `dotnet test Tokenmetry.sln`。
2. 按 D-002 离屏截图钩子思路对关键页面留证（可选）。

Expected:
- 测试基线不回归；目检无 P0/P1 差异。

Results（2026-10-13）:
- `dotnet build Tokenmetry.sln`：0 警告 / 0 错误。
- `dotnet test Tokenmetry.sln`：App 33 过、Headless 185 过、Core 103 过、Infrastructure 640 过/3 跳、Reconciliation 49 过/19 跳 —— 合计 **1010 通过 / 22 跳过 / 0 失败**。
- 离屏截图留证：8 页 × 亮暗 + 外壳 + 图表控件全部重生成至 docs/design/（18 张更新；另全部 32 张 PNG 因重渲染 mtime 刷新，其中 14 张字节级不变——渲染确定性所致）。
- 新增/演进用例见 Development Log；P0/P1 差异无遗留（1 个 P1、2 个 P2 当场修复）。

## Development Log

### 2026-10-13

**DashboardViewModel 处置定案：保留为纯 headless 刷新锚点（未迁锚点）。**
后台采集刷新线（定时器 / 启动首刷 / 设置页重扫三触发共用一轮）的接线是
`BackgroundRefreshWiring.Attach(dashboard, shell) → dashboard.RefreshCommand`，成败口径取其
Status != "error"，且 RefreshAsync 内含非 facts 持久化源落库与成本异常提醒（AnomalyDetector +
ISystemNotification）。迁轻量 RefreshCoordinator 等于重写一条有 P0 回归锁（SettingsPageTests.
Di_settings_rescan_drives_real_dashboard_refresh_line）与 15 个 VM 用例的采集线，收益（删一个
无 UI 挂载的类）不抵风险；故 VM 原样保留并在类型注释写明其 headless 定位与"拆锚点前须迁移的
三点"（接线 / 落库 + 异常提醒 / 成败口径）。DashboardView 只是其旧宿主，删视图不动 VM。

**删除清单（11 个文件 + 2 组 DI 注册 + 2 个包 + 10 组旧画刷）：**
- Views：DashboardView / ReportsView / ModelBreakdownView / TrendSpikeView / EmptyStateView（.axaml + .cs）
- ViewModels：ReportsViewModel / ModelBreakdownViewModel / TrendSpikeViewModel
- 死代码连带：ReportCsvExportService.cs（Formatter/Exporter/ReportsDailyRow，仅旧 Reports 页使用，
  DI 注册删除；ReportCsvFormatterTests 随删）——如未来需要 CSV 导出，git 历史可恢复
- App.axaml：DataGridSemiTheme 撤下 + I-025 批次的 10 组旧画刷（SecondaryText/MutedText/
  CardBackground/CardAccentBackground/SeparatorBorder/WarningBanner×3/DangerText/AccentLinkText，
  唯一使用者即被删旧视图）——App.axaml 注释中"收敛合并留待 Q-002"的事项就此办结；
  Semi 键名冲突检测职责由 DesignTokenTests 对 App* 令牌的断言接管
- Tokenmetry.App.csproj：Avalonia.Controls.DataGrid + Semi.Avalonia.DataGrid 包引用移除
（I-026 的 DataGrid 演示使命已完成，生产视图无 DataGrid 使用者，演示随 TrendSpikeView 删除、
未迁入 StylePreview；SemiThemeMigrationTests 以类型全名负断言防复活）

**StylePreviewView 定案：dev-only 保留。** 无导航入口、不在外壳路由中，唯一打开方式
TOKENMETRY_PREVIEW_WINDOW=1；定位（截图/测试基建 + 开发预览）已写入类型与 XAML 注释。
ChartPreviewViewModel 同步改注释。

**跨页一致性打磨：**
- 数据表行样式收编：Agents/Models/Projects 的 rank-table 与 Sessions 的 session-table 四份局部
  副本统一为 SharedStyles 的 `data-table` 共享配方（SessionsTests 类名断言随改）。
- 行 hover（Q2-B2）：原局部样式把 ListBoxItem 背景恒置透明，原型 `tr:hover` 底色不可见；收编后
  统一 `:pointerover → AppNavHoverBackgroundBrush`（Dark #17253b，与原型 tr:hover #17253a 同源
  一阶差，归并记为有意偏离），DesignTokenTests 新增 headless 用例锁定（MouseMove 命中行容器
  断言背景为同一画刷实例）。
- 滚动（Q2-B1）：总览/Agent/模型/项目四页根补 ScrollViewer（对齐时间/成本/设置页既有模式），
  修复最小窗口高度下底部面板被裁剪不可达；会话页维持视口式布局保万行虚拟化（有意差异，已记
  入差异清单）。
- 滚动条样式：原型无自定义滚动条（浏览器默认），Semi 默认滚动条即对应物，全页一致，不定制。
- 空态统一盘点（审查 P2-1 后如实分层表述）：5 个表级页（总览/会话/Agent/模型/项目）统一经 SharedStyles 的 `table-empty`（表级诚实空态 + 错误态
  互斥）；时间/成本页零数据以补零图表呈现（原型语义）+ 诚实错误态，无独立空态元素；设置页经
  status-nodata chip 降级；外壳右栏为"暂无最近会话"诚实文案；EmptyStateView 是旧 Dashboard 专属整页大空态
  （绑定 DashboardViewModel.RefreshCommand），无真实页使用 → 随旧视图删除，不改造成通用控件
  （避免为统一而大改真实页布局）。
- 键盘可达性：AppShellTests 新增 Tab 遍历用例（顶栏时间/Agent 下拉、设置入口、侧栏导航全部
  可达且焦点不逃逸窗口）；focus ring 观感与读屏复核留真机（如实标注，headless 无法定性）。

**全量目检与差异清单：** 8 页 × 亮暗重生成（复用各页既有截图测试，docs/design/ 18 张更新），
逐页对照原型核对，成文于 **docs/design/q-002-visual-diff.md**——每条标【还原完成】/
【有意偏离+理由】/【缺陷】；缺陷 3 条（1 P1 + 2 P2）当场修复：
- Q2-B1（P1）四页内容小窗裁剪 → 补 ScrollViewer；
- Q2-B2（P2）行 hover 缺口 + 样式四份重复 → data-table 收编 + hover 统一 + 用例锁定；
- Q2-B3（P2）热力图说明文案两处不一致（时间分析页"越深" vs 预览页"越亮"，各在一主题下误导
  方向）→ 统一为主题无关的"越浓"。
真机交互复核（hover 手感 / focus ring 观感 / 读屏）留 PM/用户。

**测试演进：** SemiThemeMigrationTests 重构（Existing_views 理论从 4 个旧视图演进为 8 真实页 +
占位 + dev-only 预览页；根主题断言改"无 DataGrid 主题"负锁；旧画刷取值/冲突两组用例删除，职责
由 DesignTokenTests 接管；ThemeService 桥接用例改用 App 令牌）；新增 Legacy_view_and_dead_service_
types_are_removed_from_app_assembly 反射结构锁（12 个旧类型名不得复活）+ data-table hover 锁 +
Tab 遍历可达性锁。Reports/ModelBreakdown 两个 VM 测试文件与 CSV 导出用例随死代码删除。
数字：清理前 1030 过/22 跳/0 败 → 清理后 1010 过/22 跳/0 败（审查补 4 例 ScrollViewer 结构锁后 1014 过），build 0 警告。

**遗留风险：**
- 会话页外的 7 页现为页面级滚动，行容器在 ScrollViewer 内无高度约束（行数量级为排行 Top N /
  源数，当前量级无感知）；若未来出现万行级排行，需按会话页视口式布局改造（已记差异清单）。
- 本机真实数据价格全为 0（glm 系列），成本四态中「未知 chip」路径在截图留档中未出现（headless
  用例已固化该路径）；真机数据出现缺价模型时建议人工复核一次显示。
- 真机交互复核项（留 PM/用户）：focus ring 观感、hover 手感、读屏。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| Q2-B1 | P1 | 总览/Agent/模型/项目四页页面根无滚动容器，最小窗口高度（960×640）下底部面板被裁剪、内容不可达（时间/成本/设置页有 ScrollViewer，跨页不一致） | Fixed | 四页页面根补 ScrollViewer（同时间/成本/设置页模式）；差异清单同步记录会话页视口式布局为有意差异 |
| Q2-B2 | P2 | 排行/明细表行 hover 不可见（各页局部样式把 ListBoxItem 背景恒置透明，覆盖 Semi :pointerover），与原型 tr:hover 不符，且同一配方在 4 页重复 | Fixed | 收编为 SharedStyles 的 data-table 共享配方 + 统一 :pointerover → AppNavHoverBackgroundBrush；DesignTokenTests 新增 hover 断言锁定 |
| Q2-B3 | P2 | 热力图说明文案两处不一致：时间分析页「颜色越深表示…」与 dev-only 预览页「颜色越亮表示…」，各自在另一主题下误导强度方向 | Fixed | 统一为主题无关的「颜色越浓表示当日 Token 越多」（两处各一行，无测试锁定文案） |

## Verification

- Related files: src/Tokenmetry.App（Views 12 处、ViewModels、Services、App.axaml、App.axaml.cs、
  DependencyInjection/AppServices.cs、Styles/SharedStyles.axaml、Tokenmetry.App.csproj）、
  tests/Tokenmetry.App.Headless.Tests（Theming/SemiThemeMigrationTests、Shell/AppShellTests、
  Shell/SessionsTests、Styles/DesignTokenTests）、tests/Tokenmetry.App.Tests（删除 3 个旧测试文件）、
  docs/design/q-002-visual-diff.md（新增）+ docs/design/*.png（重生成）
- How to run/verify: `dotnet build Tokenmetry.sln`（0 警告）；`dotnet test Tokenmetry.sln`（1010 过/22 跳/0 败）；
  grep 验证导航与 DI 无旧视图残留（仅注释性文字）；`SemiThemeMigrationTests.Legacy_view_and_dead_
  service_types_are_removed_from_app_assembly` 反射锁旧类型不复活；截图重生成见各截图测试的
  TOKENMETRY_*_PREVIEW_DIR 约定；差异清单与逐页核对记录见 docs/design/q-002-visual-diff.md
- Results: AC 全部满足（2026-10-13）；目检 3 缺陷（1 P1 + 2 P2）当场修复，无 P0/P1 遗留；
  真机交互复核（focus ring 观感 / hover 手感 / 读屏）留 PM/用户

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，代码与采集线无缺陷——采集线完好（BackgroundRefreshService/Wiring 零改动、dashboard.Status 成败口径消费链闭合、P0 回归锁完整）、删除零残留（5 视图 + 3 VM + CSV 整线 + 10 组旧画刷 + DataGrid 两包全仓 grep 0 引用）、data-table 收编逐 setter 相等（唯一新增即声明的 hover 修复）、差异清单 8 条有意偏离逐条溯源属实、-24 旧用例抽查无误删、+4 断言真实、截图无丢色异常。文档级发现由 PM 修复：① P2-1 空态盘点结论失实（8 页统一 table-empty → 实为 5 表级页 + 时间/成本补零图表 + 设置页 chip 降级，改为如实分层表述）；② P2-2 B1 滚动修复无自动化锁 → 补 4 例 ScrollViewer 结构锁（Long_scroll_pages_root_is_scrollviewer…，1014 过）；③ P3 三处：截图 17→18 张更正（含 32 张 mtime 刷新/14 张字节不变说明）、测试计数分解更正（27 删 − 7 增 + 审查补 4）、SemiTestAppBuilder 过时注释更正。PM 验收：build 0 警告 + 全量 1014 通过 / 22 跳过 / 0 失败。
