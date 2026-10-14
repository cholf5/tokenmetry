---
id: D-006
title: 总览页（上半）：KPI 行 + 使用趋势 + Agent 占比环形
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

重建 Dashboard 为原型总览页的上半区：4 张 KPI 卡（总 Token / 估算成本 / 会话数 / 缓存读取占比，含环比箭头）、Token 使用趋势（D-004）、Agent 使用占比环形（D-005），全部接真实查询服务与全局筛选。

## Background

- 设计基准：原型 `#page-overview` 的 `.kpis` 与 `.grid2` 第一行。
- 这是 v0.4「Dashboard 重构为固定视图」的落地卡（取代旧 DashboardView 的 Fluent 版本）。
- 环比箭头数据来自 I-028 的上一周期对比；未知成本显示「未知」而非 $0。

## Acceptance Criteria

- [x] KPI 卡接 I-028：数值、环比方向（↑绿/↓红）、副文案齐全（headless 断言：值/箭头方向/前景色=令牌画刷）
- [x] 趋势图随全局时间范围 / Agent 筛选刷新（headless 断言筛选→UsageQueryFilter 映射与重查次数）
- [x] Agent 环形随筛选刷新，占比数值与查询结果一致（headless 断言段值 = GetAgentBreakdownAsync Top6+其他）
- [x] 未知成本 KPI 显示「未知」不折零（InlineData 四态：$x.xx / $x.xx+ / 未知 / —）
- [x] 明暗两套目检通过（以离屏截图 docs/design/overview-{light,dark}.png 为证；真机交互目检留 PM 复核）

## Subtasks

- [x] OverviewViewModel（接全局筛选状态）
- [x] KPI 卡布局 ×4 + 绑定
- [x] 趋势图与环形图接入
- [x] 真实数据联调与目检（真实服务 headless 同口径对账冒烟 + 截图留档；真机目检留 PM 复核）

## Dependencies

- D-003, I-028, D-004, D-005

## Test Cases

### TC-001: KPI 数值对账

Steps:
1. 选定时间范围，记录 KPI 四值。
2. 与 `sqlite3` 直查同口径对照（或复用对账测试口径）。

Expected:
- tokens 一致；成本在容差内；缺价模型期间成本显示「未知」。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 落地上半区（`OverviewView` + `OverviewViewModel`）：页头 + KPI×4 + `.grid2` 第一行（TrendChart DailyTokens + DonutChart）；外壳 `PageId.Overview` 路由由占位页换成真实页（占位视图保留给其余 6 页；旧 DashboardView 文件不动，归 Q-002）。下半区（模型分布 / Token 构成 / 项目排行 / 成本趋势）归 D-007。
- **筛选→查询参数映射**：TimeRange → 本地自然日窗口（今天收口：7 天 = today-6..today、30 = -29、90 = -89，含端点，保证 I-028 有 PreviousPeriod 可环比）；All → 双端无界（查询侧 PreviousPeriod=null → 页面呈平箭头 "→ —" 不臆造对比）；AgentOption.Id 直传查询服务，`"all"` 映射为 null（查询服务语义：空白不过滤）。订阅 PropertyChanged 只响应 TimeRange/Agent（Display 是派生串，忽略防一次变更双重触发）。
- **序号防护**（同 ChartPreviewViewModel 模式）：每次 LoadAsync `++_loadSequence`，KPI/日序列/Agent 三个查询每个 await 后比对序号，过期响应直接丢弃——快速切换筛选时旧响应绝不覆盖新选择；错误路径同样比对（旧请求的失败不覆盖新请求状态，失败呈诚实错误行，随下次筛选变化自动重试）。
- **未知成本 KPI 呈现**（四态口径）：(x,false)→`$x.xx`；(x,true)→`$x.xx+`（已知成本下限）；(null,true)→「未知」；(null,false)→`—`（无成本数据）。空窗口诚实呈现：计数如实 `0`、成本/占比无基数留 `—`；缓存占比 0..1 → 一位小数百分数；环心 = KPI 总 Token 紧凑格式（与卡片同源），环形段 = Agent 维度 Top6 + 「其他」聚合、零 token 行不入环。
- **涨跌色**：复用既有 `AppAccentGreenBrush`/`AppAccentRedBrush`（即原型 `.up`/`.down` 的 var green/red，明暗成套且 DesignTokenTests 已覆盖），未新增颜色令牌；新增 `AppKpiTitleBrush`（原型 `.kpi-top{color:#b7c5d9}`，Dark 原值 / Light `#5d6f86` 协调补值）并同步 DesignTokenTests。箭头色经 `Classes.kpi-change-up/down/flat` 类绑定切换（共享样式新增 kpi-top/kpi-value/kpi-foot/kpi-change-* 五组类）。开发中抓到并修复一处 bug：类绑定名（up/down/flat）与样式类名（kpi-change-*）不一致导致箭头恒为 muted——headless 补了 Foreground 同一实例断言作回归防线。
- **外壳接线**：MainWindowViewModel 构造注入 IUsageQueryService，CreatePage 增加 Overview 分支；页面 VM 退订约定由 PagePlaceholderViewModel 泛化为 `(CurrentPage as IDisposable)?.Dispose()`；AppServices 不新增注册（页面 VM 由外壳持筛选实例直接 new，保持 CreateIsolatedShellViewModel 的测试隔离）。
- **TC-001 分工**：查询层 SQL 对账已在 UsageQueryServiceTests 完成；页面层做「stub 值 → 卡片显示值」绑定对账（OverviewTests），另加真实服务同口径直查对账冒烟（环境无关）。
- **遗留风险**：① 明暗目检以离屏截图为证，真机 hover / 明暗热切换 / 窗口缩放目检留 PM 复核；② Agent 下拉仍是 D-003 占位清单，D-008 接真数据后本页无需改动（Id 直传）；③ 会话数卡脚注沿用原型文案「跨 Agent 汇总」，选定单一 Agent 时措辞不再精确（纯文案问题）；④ 环心总量（KPI）与环形段（Agent 维度）来自两次独立查询，后台刷新恰好落在两次查询之间时理论上有极小时窗不一致（概率极低，未处理）；⑤ 页面每次导航重建并三连查，All 范围大数据量首查可能稍慢，页面级缓存暂不做（查询服务侧有 facts 索引）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/ViewModels/OverviewViewModel.cs`（含 KpiCardViewModel）、`src/TokenPulse.App/Views/OverviewView.axaml(.cs)`、`src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（Overview 分支 + IDisposable 退订）、`src/TokenPulse.App/Views/MainWindow.axaml`（DataTemplate）、`src/TokenPulse.App/Styles/Tokens.axaml`（+AppKpiTitleBrush 明暗成套）、`src/TokenPulse.App/Styles/SharedStyles.axaml`（+kpi-top/kpi-value/kpi-foot/kpi-change-up/down/flat）、`tests/TokenPulse.App.Headless.Tests/Shell/OverviewTests.cs`（新增）、`tests/TokenPulse.App.Headless.Tests/Shell/AppShellTests.cs`（初始路由断言随总览真实页更新）、`tests/TokenPulse.App.Headless.Tests/Styles/DesignTokenTests.cs`（令牌同步）。DashboardView 未删改（Q-002 处置）。
- How to run/verify:
  - `dotnet test TokenPulse.sln`（全套）；`dotnet test tests/TokenPulse.App.Headless.Tests --filter "FullyQualifiedName~OverviewTests"`（本卡 15 例：KPI 绑定/成本四态/空窗口/筛选映射/序号防护/环形段一致性/Dispose 退订/明暗渲染+箭头前景色/真实服务冒烟）。
  - 截图再生成：`TOKENPULSE_OVERVIEW_PREVIEW_DIR=<docs/design 绝对路径> dotnet test tests/TokenPulse.App.Headless.Tests --filter "FullyQualifiedName~OverviewTests|FullyQualifiedName~AppShellTests"`。
- Results: 全套 923 通过 / 22 跳过 / 0 失败（基线 908 + 本卡 15），0 警告；headless 子套件 95 通过。截图留档：`docs/design/overview-light.png`、`docs/design/overview-dark.png`（stub 数据，确定性）；`docs/design/app-shell-{light,dark}.png` 随总览真实页重生成。真机目检（hover/交互）以截图为证、留 PM 复核。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，无 P0/P1。筛选→查询映射（窗口端点/All 无界平箭头/Agent null 传递）、三查询序号防护完整性、未知成本四态、Dispose 退订（SettingsViewModel 非 IDisposable 不受泛化影响）、AppShellTests 断言演进、零硬编码色、后台采集线零改动全部核实；15 用例抽检断言真实；截图逐张目检（含用 sqlite3 直查本机 facts 核实成本卡 \$0.00 是"(0,false) 已知为零"而非未知折零）。P2 一条（两处注释残留修复前旧类名 Classes.up/down/flat，与回归防线自相矛盾）由 PM 当场修正为 Classes.kpi-change-up/down/flat；顺手采纳两条 P3：环比百分比取绝对值（消除 "↓ -1.1%" 双重负号，方向由箭头表达）、OverviewViewModel 类注释补线程约定（UI 线程亲和，序号防护无锁的前提）。其余 P3（第 4 卡脚注窄窗截断、daily 挂起用例更硬、CancellationToken、空态截图）留后续卡处理。PM 验收后 923 通过 / 22 跳过 / 0 失败维持。
