---
id: D-007
title: 总览页（下半）：模型分布 + Token 构成 + 项目排行 + 成本趋势
type: feature
priority: P2
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

总览页下半区四块：模型使用分布表（TopN + mini 进度条 + 查看详情链接）、Token 类型构成环形（输入/输出/缓存）、项目/目录排行表、成本趋势折线（USD）。

## Background

- 设计基准：原型 `#page-overview` 的两个 `.gridEqual` 下行。
- 「查看详情 ↗」跳转模型统计页 / 项目统计页；mini 进度条复用 I-027。
- 成本趋势接 I-028 日序列的 cost 列；未知成本不折零（序列中 null 的日聚合处理在 I-028 契约内）。

## Acceptance Criteria

- [x] 模型 TopN 表：token 数、占比、mini bar 比例正确，「查看详情」跳模型页（stub 绑定 + 外壳端到端导航均有自动化断言，见 OverviewBottomTests）
- [x] Token 类型构成环形三段正确（输入/输出/缓存相关；三段映射定案见 Dev Log 2026-10-13）
- [x] 项目排行表：排名、归一化项目名、token、占比正确，跳项目页（歧义守卫回退原始路径有断言）
- [x] 成本趋势折线随全局筛选刷新（与上半区共用同一订阅/序号防护路径与同一次日序列查询）
- [x] 全部随全局筛选联动，明暗两套目检（目检基于离屏截图 docs/design/overview-bottom-{light,dark}.png，含断言）

## Subtasks

- [x] 模型分布表 + mini bar + 跳转
- [x] 类型构成环形接入
- [x] 项目排行表
- [x] 成本趋势接入

## Dependencies

- D-006（同页，先落上半区）
- I-028, I-029, I-027

## Test Cases

### TC-001: 占比与跳转

Steps:
1. 目检各表占比与 mini bar 宽度比例；点击「查看详情」。

Expected:
- 占比合计合理（TopN 不足 100% 时含「其他」或按 TopN 占比展示，实现时定案记录）；跳转到达对应页。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- **实现收口**：`OverviewViewModel` 扩展下半区四块（模型分布 / Token 类型构成 / 项目排行 / 成本趋势），全部走 D-006 既有的筛选订阅 + 序号防护路径（同一 `LoadAsync` 顺查 KPI → 日序列 → Agent → 模型 → 项目，每步序号比对），未另起一套；空数据诚实空态（表空态文案 / 环形内建空环）。
- **三段映射定案（读契约后落定）**：契约证据 `UsageRecord.TotalTokens = InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens`，即 `InputTokens` 本身**不含缓存**（未缓存输入）。映射：输入(未缓存) = Σ InputTokens；输出 = Σ OutputTokens；缓存相关 = Σ (CacheCreation + CacheRead)。三段之和 = Σ TotalTokens。数据源用已取回的日序列（与上半趋势图同一次 `GetDailySeriesAsync`，不另发请求）；环心 = KPI 总 Token（与上半区 KPI 卡同源，吸取 D-006 环心/段不同源教训）。个别段为 0 保留段位（图例如实 0.0%），全零 → 空集呈内建空态。图例口径文案：「输入(未缓存) / 输出 / 缓存相关」。
- **TopN 呈现定案（实现定案，非任务卡要求）**：TopN = 6（原型表为 Top5，取 6 与同页 Agent 环形 Top6 一致），**不加「其他」聚合行**——对齐原型（榜单只列头部行），占比列用 I-029 的 `TokenShare`（占范围全部行之和，逐行独立成立、不暗示行和为 100%），完整清单经「查看详情 ↗」到模型/项目页（D-010/D-011）。零 token 行不入榜（占比无意义）。
- **mini bar 宽度口径定案（实现定案）**：bar 值 = `TokenShare × 100`（ProgressBar.mini 的 Value 0-100 直驱），与占比列同值同源；原型用相对最大行的宽度（`tokens/maxTokens`），此处改为绝对占比（偏差已记录）。
- **项目排行表带 mini bar（超出原型的自选增强，审查 P2-2 留痕）**：原型 projectPreview 行只有排名/圆点/名称，实现给项目行也加了 mini bar（与模型表一致的占比语义）——视觉审查通过，保留并在此记录偏差。
- **导航机制选型**：构造注入 `Action<PageId>?` 回调（`MainWindowViewModel.CreatePage` 把既有 `GoToPage` 方法组传入）。对比过的方案：INavigationService 接口（仅一个消费方，抽象过度）与页面 VM 直接引用 MainWindowViewModel（双向耦合，否决）。回调为可空参数（默认 null），预览/纯单测环境 no-op；测试直接传 `List<PageId>.Add` 断言，另有外壳端到端用例（执行命令 → `CurrentPage` 变为对应页）。VM 命令：`OpenModelsCommand` / `OpenProjectsCommand`。
- **跳转接线**：`MainWindowViewModel.CreatePage(PageId.Overview)` 增传 `GoToPage`；页面 VM 不引用外壳类型，依赖单向 `Action<PageId>`。
- **令牌补充（I-027）**：新增不随明暗翻转的 `AppTokenType{Input,Output,Cache}Color/Brush` 三色（原型 typeDonut 三元组 #4c8dff / #a779ff / #38d6a4；独立成键不借用 AppSeries{N}，语义固定防系列序漂移）；`DesignTokenTests` 同步（新增性质测试 + 冲突检测清单）。SharedStyles 增补预览表样式 `table-th / table-td / table-num / table-empty`（原型 .table th/td/.num/.empty 对应）。
- **D-006 审查 P3 顺手修**：OverviewView 四张 KPI 卡脚注由横排 StackPanel 改 `Grid ColumnDefinitions="Auto,*"` + 说明列 `TextWrapping="Wrap"`（原结构横向测量宽度无界，窄主区被裁切；第 4 张卡「缓存读取 / 输入相关 Token」最长最先暴露，四卡统一改防同类问题）。
- **测试**：新增 `OverviewBottomTests`（13 用例）：Top6 顺序/占比/bar 值/无「其他」/零行排除、无基数 "—"+bar 0、项目归一化名（含歧义守卫回退原始路径）与排名、环形三段映射与段和自洽、全零空段、成本趋势单查询同源、「查看详情」回调断言（含无回调 no-op + 外壳端到端）、序号防护覆盖模型维度挂起响应、明暗渲染 + 截图留档、真实服务冒烟（同口径直查对账）。`OverviewTests` 的 stub 扩展模型/项目维度与调用计数（D-006 既有断言不回归）；`DesignTokenTests` 新增类型三色用例。
- **截图**：`docs/design/overview-bottom-{light,dark}.png`（整页离屏渲染，覆盖下半区；测试内含「系列色/类型段色出图」「空态不可见」断言后落盘）。
- **遗留风险**：
  - 模型/项目页（D-010/D-011）尚未落地，「查看详情」现落在占位页（端到端断言按占位页断言，两卡落地后无需改动本页）。
  - 行首圆点色在 VM 侧解析 `AppSeries{N}Brush`（Application.Current 不可用时为 null、圆点透明）——VM 内资源解析是受控的例外，与 DonutChart/Heatmap 的取色先例一致，但属于轻微分层妥协。
  - 成本趋势在稀疏数据下以孤立点为主（D-004 已知成本连线规则：连续 ≥2 天才成线），是契约内行为非缺陷；数据密集后自动成线。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/Views/OverviewView.axaml`、`src/TokenPulse.App/ViewModels/OverviewViewModel.cs`（+新增 `DimensionRowViewModel`）、`src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`、`src/TokenPulse.App/Styles/Tokens.axaml`、`src/TokenPulse.App/Styles/SharedStyles.axaml`、`tests/TokenPulse.App.Headless.Tests/Shell/OverviewBottomTests.cs`（新增）、`tests/TokenPulse.App.Headless.Tests/Shell/OverviewTests.cs`、`tests/TokenPulse.App.Headless.Tests/Styles/DesignTokenTests.cs`、截图 `docs/design/overview-bottom-{light,dark}.png`
- How to run/verify: `dotnet test TokenPulse.sln`；截图留档 `TOKENPULSE_OVERVIEW_BOTTOM_PREVIEW_DIR=<docs/design 绝对路径> dotnet test tests/TokenPulse.App.Headless.Tests --filter FullyQualifiedName~Overview_bottom_renders`；TC-001 目检占比/bar 比例与跳转（跳转已由外壳端到端测试覆盖：执行「查看详情」命令 → CurrentPage 到达模型/项目页）
- Results: 全解决方案 937 通过 / 22 跳过 / 0 失败（基线 923 + 本卡新增 14），构建 0 警告；headless 套件 109 通过含 `OverviewBottomTests` 13 用例（stub 绑定对账、环形三段、导航回调与端到端、序号防护、明暗渲染截图、真实服务冒烟）；明暗截图已目检（四块布局、mini bar 比例、三段环形、KPI 脚注不裁切均符合原型）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，无 P0/P1。三段映射全链独立走读（契约计算属性 → 适配器逐一核对无缓存双计 → 查询同源 → VM）、mini bar 口径（TokenShare 分母=全部行）、导航回调（无双向耦合、null 安全、端到端断言真实）、序号防护五处 await 逐一比对、新令牌三色逐位对原型且参与撞名检测、D-006 脚注 P3 修复无副作用、超范围零、截图目检通过。P2 两条均为文档溯源问题，PM 当场修复：① Dev Log 两处把实现定案误写成「任务卡明确/任务卡 N≈6」（核对 c94de17 创建原文后改为如实溯源）；② 项目排行表 mini bar 系超出原型的自选增强未记录偏差（补记留痕，保留实现）。P3 采纳两条：CreateTokenTypeSegments 注释补各适配器展示桶让位指向、table-td 注释措辞更正（AppTextBrush 略亮于原型，可读性优先）。PM 验收后 937 通过 / 22 跳过 / 0 失败、0 警告维持。
