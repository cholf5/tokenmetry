---
id: S-004
title: 数据与计价页拆分（数据源/口径/目录/别名 ↔ 应用偏好分离）
type: refactor
priority: P2
size: M
status: done
created: 2026-10-16
updated: 2026-10-16
blocked: none
---

## Goal

用户决策：把 S-002 的合并页「数据源与设置」沿真实关注点拆成两个页签——「数据与计价」（数据源表 / 统计口径 / 模型目录 / 别名映射）与「设置」（主题 / 语言 / 刷新间隔 / ccusage 命令 / 通知 / 本地路径）。侧栏由 8 项变 9 项，行为语义零变化。

## Background

- 动机：S-002 合并页里"影响数字的口径"（数据源、去重说明、计价、聚合）与"不影响数字的偏好"（主题、语言、刷新）混排，I-035～I-038 模型目录批次落地后页面长度与异质性进一步放大；I-035 的跨页指引文案（成本分析页 → 设置页模型目录）也让"目录在设置页"的归位显得别扭。
- 缝合线定案：按"数字怎么算" vs "应用长什么样"切，而不是字面 Data | Settings——数据源/口径/目录/别名互相咬合（别名目标候选来自目录、目录决定计价、口径解释数据源），拆散会断引用链；偏好侧与之无任何数据依赖。
- 产品方向依据：pivot 文档 §3.4 固定 IA 立场允许页面结构随真实关注点演进（不做的是面板平台/布局自定义）；§5 Settings 原规格本就把数据源路径与定价覆盖列进 Settings，本次拆分把它们归位到数据与计价页，已在 pivot 文档 §5 追记（2026-10-16）。
- 明确不做：不新增/删除任何设置项与行为；不做页内锚点/折叠等替代方案；不改原型 HTML（历史设计基准，拆分偏离记录于本卡）。

## Acceptance Criteria

- [x] 侧栏 9 项：总览 / 会话明细 / Agent 统计 / 模型统计 / 项目统计 / 时间分析 / 成本分析 / 数据与计价 / 设置（明暗文案与顺序对齐 ShellPages 注册表）
- [x] 数据与计价页承接数据源表 + 重扫 + 口径区 + 模型目录 + 别名映射，全部行为语义与拆分前逐位等价（重扫走真实采集线、目录/别名即存即热更新 + 重算）
- [x] 设置页收缩为应用偏好（主题三态 / 语言 / 刷新间隔 / ccusage 命令 / 通知开关 / 本地路径），全自动保存语义不变
- [x] 跨页指引修正：成本分析页「待确认价格」卡指向「数据与计价 → 模型目录」
- [x] 测试全绿无回归（App.Tests 与 Headless 按页拆分，P0 回归锁随迁到数据与计价页）

## Subtasks

- [x] `DataPricingViewModel` 新建（自 SettingsViewModel 迁入数据源/口径/目录/别名逻辑 + 行记录类型），`SettingsViewModel` 收缩为偏好（构造依赖同步瘦身：不再需要 scanReader/refreshService/toast/query/catalog）
- [x] `DataPricingView` 新建 + `SettingsView` 收缩（面板原样搬迁，x:DataType 对应替换）
- [x] 接线：PageId.DataPricing + ShellPages 注册表 + MainWindowViewModel（CreatePage/DataPricing 属性/注释）+ AppServices 单例注册 + MainWindow.axaml DataTemplate
- [x] Translations：NavDataPricing/DataPricingTitle/DataPricingSubtitle 三键（zh/en），NavSettings/SettingsTitle 收为「设置」，SettingsSubtitle 换偏好文案，KpiFootUnpricedWhere 指引改指数据与计价页
- [x] 测试拆分：SettingsViewModelTests 收缩为偏好侧 + 新 DataPricingViewModelTests（数据侧全部用例迁移）；Headless SettingsPageTests 收缩 + 新 DataPricingPageTests（P0 回归锁 `Di_data_pricing_rescan_drives_real_dashboard_refresh_line` 随迁）；外壳测试构造点与导航数断言（8→9）适配

## Dependencies

- S-002（合并页）、I-035～I-038（模型目录批次）

## Test Cases

### TC-001: 导航与路由

Steps: 断言 NavigationItems 为 9 项且标签唯一（zh/en）；选第 7 项路由 DataPricingView（数据源表 19 行重建）、第 8 项路由 SettingsView。

Automated: AppShellTests.Shell_has_three_columns_*（9 项 + 标签数组）、Navigation_selection_*（两页路由断言）、SemiThemeMigrationTests（9 项 + DataPricingView 实例化）、PageDescriptorTests（9 页标签唯一 + 设置页 "Settings"）。

### TC-002: 数据与计价页行为等价（P0 回归锁随迁）

Steps: 真实 DI 解析 DataPricingViewModel → RescanCommand → 断言走通真实采集线（scan finished + 外壳时间戳回写 + 19 源快照）；数据源表/口径区/目录表单/别名 AutoCompleteBox 绑定各一例 headless 端到端。

Automated: DataPricingPageTests 全套（含 P0 锁 `Di_data_pricing_rescan_drives_real_dashboard_refresh_line`）+ DataPricingViewModelTests（重扫成败/合并语义/未接线诚实失败/目录与别名增删改热更新/旧 PricingOverrides 兼容钉）。

### TC-003: 设置页偏好语义

Steps: 主题/语言/刷新间隔/ccusage 命令/通知开关改完即存；语言切换实时重译选项 Label 与页面。

Automated: SettingsViewModelTests（收缩后偏好侧用例）+ SettingsPageTests（主题/语言组合框 + DI 冒烟 + 偏好页不含数据源表断言）。

## Development Log

### 2026-10-16

- 用户在评审 I-035 目录批次后提出拆分（"Data & Settings 有什么关系要放在一起？"），定案缝合线为"影响数字的口径 vs 应用偏好"，两页分别命名「数据与计价」与「设置」。实现同日完成。
- VM/View 原样搬迁为主：数据侧行记录类型（CaliberItem/DataSourceRowViewModel/AliasRuleRowViewModel/ModelDefinitionRowViewModel）与全部行为逻辑迁入 DataPricingViewModel；SettingsViewModel 仅保留偏好 + 自动保存 gate。两 VM 共享同一 AppSettings 实例，各自持有保存 gate（共享实例全量写盘，跨页并发保存语义安全）。
- DI：两页均为单例 + INavigationAware 重进刷新（仅数据页实现）；MainWindowViewModel 构造新增 DataPricingViewModel 参数（7 处测试构造点同步）。
- i18n：zh 字典为源，新增 NavDataPricing/DataPricingTitle/DataPricingSubtitle；设置页副标题换偏好文案；KpiFootUnpricedWhere（成本分析页指引）改指「数据与计价 → 模型目录」。
- 截图留档：settings-{light,dark}.png 更新为偏好页（内容变短），新增 data-pricing-{light,dark}.png（7 源 stub 快照，同 S-002 口径；19 源真实长表由 DI 冒烟断言覆盖）。
- 明确不做（同 Background）：不借机调整任何面板内容/顺序；目录编辑表单收对话框等页面内部优化另行立卡。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/ViewModels/DataPricingViewModel.cs`（新）、`src/Tokenmetry.App/ViewModels/SettingsViewModel.cs`（收缩）、`src/Tokenmetry.App/Views/DataPricingView.axaml(.cs)`（新）、`src/Tokenmetry.App/Views/SettingsView.axaml`（收缩）、`src/Tokenmetry.App/ViewModels/NavigationItemViewModel.cs`、`src/Tokenmetry.App/ViewModels/MainWindowViewModel.cs`、`src/Tokenmetry.App/DependencyInjection/AppServices.cs`、`src/Tokenmetry.App/Views/MainWindow.axaml`、`src/Tokenmetry.App/Localization/Translations.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`；截图 `TOKENMETRY_SETTINGS_PREVIEW_DIR=<docs/design 绝对路径> dotnet test tests/Tokenmetry.App.Headless.Tests --filter "FullyQualifiedName~Data_pricing_page_renders|FullyQualifiedName~Settings_page_renders"`（相对路径会落进 testhost CWD，须用绝对路径）
- Results:
  - 全量 1257 通过 / 22 跳过 / 0 失败 / 0 警告（Core 159；Infrastructure 652/3 跳；App.Tests 127；Headless 270；Reconciliation 49/19 跳）。
  - P0 回归锁随迁通过：`Di_data_pricing_rescan_drives_real_dashboard_refresh_line`（真实 DI → 重扫 → 真实采集线 + 外壳时间戳回写 + 19 源快照）。
  - 明暗截图：`docs/design/data-pricing-{light,dark}.png`（新增）、`docs/design/settings-{light,dark}.png`（更新为偏好页），人工核对通过。
