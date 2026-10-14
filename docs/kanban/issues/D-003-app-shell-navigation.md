---
id: D-003
title: 三栏应用外壳 + 导航 + 全局筛选状态
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

按原型搭应用骨架：左侧导航（8 个固定视图）、顶部栏（全局时间范围 + Agent 筛选 + 数据状态 + 设置入口）、右侧最近会话栏、主内容区页面路由；全局筛选状态写入共享 VM 并跨页生效。替换现有 MainWindow 的 TabControl 结构。

## Background

- 设计基准：`docs/design/ccusage_analytics_prototype.html` 的 `.app/.topbar/.shell/.sidebar/.sessions-panel`。
- 2026-10-12 决定：窗口 chrome 沿用系统边框，不复刻原型左上角的 mac 红绿灯（那是网页模拟窗口的装饰；Avalonia 有 ExtendClientAreaToDecorationsHint 可选，不做首期）。
- 右侧栏可见性：原型为全页常驻（<1200px 隐藏，桌面版可忽略响应式）；实现时目检后定，倾向保持常驻。
- 原型响应式断点（@media 三档）桌面端不做，定一个合理最小窗口尺寸即可。

## Acceptance Criteria

- [x] 三栏布局 + 8 项导航 + 页面路由（各页可为空占位）——AppShellTests 结构断言 + 明暗离屏截图
- [x] 顶部栏全局筛选（时间范围 7/30/90/全部 + Agent）写入共享筛选状态，切换时通知各页 VM——FilterStateService 单例 + 占位页显示"当前筛选"并随变更更新（headless 断言含跨线程 marshal）
- [x] 右栏最近会话 + 快速筛选骨架就位（真实数据接线在 D-008）——列表结构 + 诚实空态 + Agent 勾选/模型/项目/应用筛选入口
- [x] 最小窗口尺寸下布局不破，导航键盘可达——960×640 三栏 ActualWidth 断言 + 方向键移动选中用例
- [x] 明暗两套目检通过——headless Skia 整窗离屏截图 `docs/design/app-shell-light.png` / `app-shell-dark.png` 逐项目检（导航选中态/顶栏/右栏/占位页均正常）；真机交互复核留待 PM 验收

## Subtasks

- [x] ShellWindow/Grid 三栏 + 导航 VM + 路由
- [x] 全局筛选状态服务（时间范围 + Agent）与通知机制
- [x] 顶部栏与右栏骨架
- [x] 旧 TabControl 移除、页面迁移占位

## Dependencies

- I-026, I-027

## Test Cases

### TC-001: 导航与筛选联动

Steps:
1. `dotnet run --project src/TokenPulse.App`。
2. 点击 8 个导航项；切换时间范围与 Agent。

Expected:
- 页面切换正常；筛选状态变更各占位页可观测（临时日志或绑定显示）。

Automated coverage（2026-10-13）:
- 上述路径已由 `tests/TokenPulse.App.Headless.Tests/Shell/AppShellTests.cs` 全覆盖（导航切换路由、筛选通知到达占位页 VM、右栏/顶栏骨架、最小尺寸、方向键、明暗截图）；真机点击路径待 PM 执行。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

**路由实现方式（决策）**：主内容区用 `ContentControl.Content={Binding CurrentPage}` + `Window.DataTemplates`（VM 运行时类型 → 视图），不引第三方导航库、不做 NavigationView。页面 VM 按 `PageId` 创建：7 个未落地页共用 `PagePlaceholderViewModel`（持有 `PageDescriptor`：Label/Icon/Title/Subtitle/落地卡号/规划内容清单），设置页复用既有 `SettingsViewModel` + `SettingsView`（S-002 才重构）。页面注册表 `ShellPages.All`（顺序即导航顺序，文案对齐原型 .nav/.pagehead）。VM 在切换后仍被各页后续持有（当前实现为每次切换重建页面 VM——占位页无状态无影响；页面卡落地时若需保留跨切换状态，VM 端持有数据即可，视图重建成本低）。

**导航控件（决策）**：Semi `ListBox`（而非 RadioButton 组）——SelectedItem TwoWay 绑定、方向键移动选中为控件原生行为（headless 用例固化）、选中态经 Window.Styles 定制（`ListBoxItem:pointerover/:selected` 直接给 Background/Foreground，Semi 模板根 ContentPresenter TemplateBinding 生效，截图佐证）。原型 `.nav button.active` 的 inset 2px 指示条：条目模板内常驻 2px `Border.nav-indicator`（避免选中时图标位移），用 `ListBoxItem:selected Border.nav-indicator` 后代选择器着色。8 项导航保证键盘可达（Tab 聚焦 + 上下方向键）。

**筛选状态服务形态（决策）**：`FilterStateService`（CommunityToolkit ObservableObject，DI 单例）而非挂共享 VM——顶部栏 ComboBox 直接绑定 `Filters.TimeRange` / `Filters.Agent`（记录类型值相等性供 SelectedItem 比较），各页 VM 经 PropertyChanged 订阅；`Display` 汇总串（「最近 30 天 · 所有 Agent」）任一维度变化即通知，占位页 chip 直接展示（TC-001 的"可观测"落地于此）。线程模型：属性 setter 在非 UI 线程调用时把"赋值 + 通知"整体 marshal 到 UI 线程（Dispatcher.UIThread.Post），保证所有 PropertyChanged 都在 UI 线程发出，订阅方与绑定无需自管调度（headless 断言 Task.Run 写入后通知线程 CheckAccess()==true）。时间范围枚举 Last7/30/90/All，默认 30 天；Agent 选项为占位清单（all + 原型 5 个 Agent），D-008 接真数据。

**数据状态（轻量实现）**：grep 结论——`IUsageDataSource`/`IUsageStore`/`LocalIngestionDataSource` 均未暴露数据源状态/最后扫描时间类接口，不为此卡发明新管线。顶部栏就绪圆点 + "本地数据源就绪"为静态文案，"最近更新 HH:mm"来自后台刷新动作完成时间戳（`NoteDataRefreshed`，定时器线程回调内部 marshal 到 UI 线程）；两者均带 TODO(I-028/D-006) 注释，待查询/采集服务提供真实状态后替换。

**旧视图处置**：Dashboard/Reports/ModelBreakdown/TrendSpike/StylePreview/EmptyState 六个视图文件与 VM 的 DI 注册全部保留（Q-002 清理），仅从导航移除。连带两个结构调整：① `MainWindowViewModel` 不再持有 Dashboard/Reports/ModelBreakdown（Settings 保留）；② DashboardViewModel 改由 App.axaml.cs 直接解析——后台刷新仍由它驱动（本地采集把会话日志解析进 facts 存储，是后续页面卡的数据来源），刷新完成回写顶部栏"最近更新"。StylePreviewView 的去留：不在导航里塞第 9 个"开发入口"（破坏"8 项导航"结构与测试），改 MainWindow.axaml DataTemplates 处 Dev 注释说明其用途（I-027 审查截图与 headless 用例仍直接实例化该视图，重生成命令见 Verification），Q-002 随旧视图一并清理。

**外壳令牌（I-027 体系延伸）**：新增 6 组 ThemeDictionaries 画刷——AppSidebarBackground（.sidebar #0d1626）/ AppTopbarBackground（.topbar #0e1728）/ AppNavActiveBackground（.active #1b2d49）/ AppNavActiveForeground（#9dc2ff）/ AppNavActiveIndicator（inset bar #5794ff）/ AppNavHoverBackground（.nav hover #17253b），Dark 全为原型选择器原值，Light 补协调值；期望值同步进 DesignTokenTests，键名对独立 SemiTheme 实例查重通过（既有用例自动覆盖）。会话列表空态/快速筛选盒等复用既有 AppLineBrush/AppPanelBrush/AppMutedTextBrush，不为近似色增设令牌。导航样式类（ListBox.nav-list/icon-btn/topbar-label）留 MainWindow.axaml 的 Window.Styles——外壳私有、页面卡不复用，已在 docs/design/design-tokens.md 头部注明，避免误从 SharedStyles 引用。

**其余结构决策**：最小窗口 960×640（184 + ≥420 主列 + 276，与原型 .shell 最小列宽同源），headless 断言最小尺寸下三栏 ActualWidth 不破；窗口 Title 更新为 "ccusage — Token Analytics"（品牌与侧栏一致）；右栏快速筛选为骨架本地状态（Agent 勾选/模型/项目下拉 + 清除 + 应用筛选→toast 提示 D-008 接入），不驱动查询——诚实占位；右栏常驻（Background 决策倾向照办），会话列表空态用简单文案而非 EmptyStateView（后者绑定 DashboardViewModel，语义不符）。

**测试与验证**：I-026 的 `MainWindow_instantiates_under_Semi` 断言随结构演进改为"无 TabControl + 8 项导航 ListBox"（注释留痕）；新增 AppShellTests 10 个用例（三栏结构/路由切换/筛选通知/线程 marshal/顶栏/右栏骨架/最小尺寸/方向键/明暗截图）；headless 21→31。全套 `dotnet test TokenPulse.sln`：834 通过 / 22 跳过 / 0 失败（基线 824/22/0 无回归），Debug/Release 构建 0 警告 0 错误。明暗截图目检：导航选中态（底色 + 蓝字 + 指示条）、顶栏筛选/数据状态/设置按钮、右栏骨架、占位页 chip 明暗均正常（app-shell-light/dark.png）。

**遗留风险**：
- 真机 `dotnet run` 的完整点击路径（TC-001）未人工执行，headless 已覆盖结构/路由/键盘/明暗离屏截图；PM 验收待做。
- Agent 选项为占位清单（all + 原型 5 项）；D-008 接真数据时需同步 `FilterStateService.AgentOptions` 与右栏 quick filters 两处。
- "最近更新"是刷新动作时间戳而非数据最后变更时间（I-028 接入真实状态）。建议 I-028 落地时启动即触发一次刷新，消除首启 15 分钟内顶栏恒显"尚未刷新"（审查观察项）。
- 页面切换为整页重建视图，无转场动画（原型 .page display 切换亦无过渡）；页面 VM 每次切换重建，页面卡落地时注意 VM 内异步初始化的重复触发；**页面 VM 若订阅常驻服务必须实现 IDisposable 并由外壳退订**（本卡已建立该模式，见下）。
- 右栏快速筛选 CheckBox 为骨架本地状态，与全局 Agent 筛选（顶栏单选）语义独立，D-008 统一收口时需明确两者关系。
- 明暗截图为 macOS Skia 字形（headless 走系统字体）；Windows/Linux 渲染未目检。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论"有问题需修复"——1×P1 + 3×P2 + 4×P3 观察，整体质量高（结构/令牌/路由/线程模型/测试质量全部核实为真，外壳零硬编码色实锤，6 个旧视图零改动）。发现全部当场由 PM 修复：
1. **P1 占位页 VM 订阅泄漏**：每次导航 new 的 PagePlaceholderViewModel 把强引用订阅挂到常驻 FilterStateService 单例上且从不退订。修复：VM 实现 IDisposable，MainWindowViewModel 导航替换 CurrentPage 前对旧占位页 Dispose 退订（Settings 为 DI 单例不在此列）；新增回归用例 `Navigating_away_unsubscribes_old_placeholder_from_filter_changes`。
2. **P2 右栏"应用筛选"按钮 `Classes="primary"` 无效**：Semi 的类选择器是 PascalCase（`.Primary`），小写 primary 两边都不命中、按钮实渲染为默认样式。改 `Classes="Primary"`。
3. **P2 默认时间范围 30 天系开发自定**：原型默认"最近 7 天"（#rangeTop 首项 + renderTrend(7)）。对齐原型改默认 7 天（FilterStateService.Default = Options[0]），测试期望同步，决策记入本文。
4. **P2 NavigationItemViewModel.IsActive 死状态**：视图选中态实际由 ListBoxItem:selected 伪类驱动，IsActive 无任何绑定，双真相源。删除该属性，测试改断言 SelectedItem。
5. P3 已顺手/留档：占位页 PropertyChanged 处理补 string.Empty（.NET 全属性变更约定）；"启动即刷新"记入遗留风险供 I-028；后台写 FilterStateService 的旧值覆盖窗口与 DataContext 恒真断言为已知观察项不处理。
修复后 835 通过 / 22 跳过 / 0 失败、0 警告。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/TokenPulse.App/Services/FilterStateService.cs`（新建：TimeRange 枚举 + TimeRangeOption/AgentOption 记录 + FilterStateService 单例，UI 线程 marshal）
  - `src/TokenPulse.App/ViewModels/NavigationItemViewModel.cs`（新建：PageId 枚举 + PageDescriptor + ShellPages 注册表 + NavigationItemViewModel）
  - `src/TokenPulse.App/ViewModels/PagePlaceholderViewModel.cs`（新建：占位页 VM，订阅 FilterStateService）
  - `src/TokenPulse.App/ViewModels/SessionsPanelViewModel.cs`（新建：右栏骨架 VM）
  - `src/TokenPulse.App/Views/PagePlaceholderView.axaml(.cs)`（新建：统一占位视图，仅用 I-027 令牌/样式类）
  - `src/TokenPulse.App/Views/MainWindow.axaml`（重写：三栏外壳 + 8 项导航 + 顶栏筛选/数据状态/设置入口 + 右栏骨架 + Window.DataTemplates 路由）
  - `src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（重写：导航/路由/数据状态；不再持有 Dashboard/Reports/ModelBreakdown）
  - `src/TokenPulse.App/App.axaml.cs`（DashboardViewModel 直接解析维持后台采集刷新，刷新回写"最近更新"）
  - `src/TokenPulse.App/DependencyInjection/AppServices.cs`（注册 FilterStateService / SessionsPanelViewModel）
  - `src/TokenPulse.App/Styles/Tokens.axaml`（追加 6 组外壳令牌，明暗各一份）
  - `docs/design/design-tokens.md`（头部注明 D-003 外壳令牌与导航样式归属）
  - `docs/design/app-shell-light.png`、`docs/design/app-shell-dark.png`（headless 整窗离屏截图留档）
  - `tests/TokenPulse.App.Headless.Tests/Shell/AppShellTests.cs`（新建：10 个用例）
  - `tests/TokenPulse.App.Headless.Tests/Styles/DesignTokenTests.cs`（期望值表同步 6 组新令牌）
  - `tests/TokenPulse.App.Headless.Tests/Theming/SemiThemeMigrationTests.cs`（MainWindow 断言：TabControl→8 项导航 ListBox）
- How to run/verify:
  - 自动化：`dotnet build TokenPulse.sln`（Debug/Release 0 警告 0 错误）+ `dotnet test TokenPulse.sln`。
  - 外壳用例：`dotnet test tests/TokenPulse.App.Headless.Tests --filter FullyQualifiedName~Shell`（AppShellTests 10 个 + SemiThemeMigrationTests 2 个 Shell 相关）。
  - 截图重生成：`TOKENPULSE_SHELL_PREVIEW_DIR=<仓库绝对路径>/docs/design dotnet test tests/TokenPulse.App.Headless.Tests --filter FullyQualifiedName~App_shell_renders`（须绝对路径，测试宿主 cwd 在 bin 目录）。
  - TC-001 人工（**待 PM 执行**）：`dotnet run --project src/TokenPulse.App` → 逐个点击 8 个导航项、切换时间范围/Agent、右栏"查看全部"与设置 ⚙ 跳转、明暗主题切换观察外壳换色。
- Results:
  - Debug / Release 构建 0 警告 0 错误。
  - `dotnet test TokenPulse.sln`：**834 通过 / 22 跳过 / 0 失败**（Core 99、Infrastructure 612+3、Reconciliation 49+19、App 43、App.Headless 31）≥ 基线 824 / 22 / 0，无回归；新增 10 个 headless 用例全过（21→31）。
  - TC-001 截图留档：`docs/design/app-shell-light.png`、`docs/design/app-shell-dark.png`（1280×800 整窗合成帧：三栏 / 8 项导航选中态 / 顶栏筛选与数据状态 / 右栏骨架 / 占位页含"当前筛选"chip）；真机人工目检待 PM。
