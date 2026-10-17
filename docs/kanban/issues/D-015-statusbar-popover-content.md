---
id: D-015
title: 状态栏快捷信息窗 · 内容层（跨平台共享 UserControl：今日 KPI + Token 构成 + 主要 Agent + 三态 banner）
type: feature
priority: P2
size: M
status: done
created: 2026-10-17
updated: 2026-10-17
blocked: none
---

## Goal

CodexBar 式「点击状态栏弹固定内容面板」的**内容层**：一个跨平台共享的 Avalonia UserControl，信息架构按原型锁定（今日 KPI 三格 + Token 构成条 + 主要 Agent Top4 + 三态 banner + 快捷入口），数据全走既有查询管线。壳层（弹窗定位/收起）在 D-016，状态栏文字在 D-014。

## Background

- 设计基线：`docs/design/tokenmetry_statusbar_prototype.html`（2026-10-17 用户经 ChatGPT 产出、原样收入仓库；同 `ccusage_analytics_prototype.html` 先例——原型是设计参照，落地按本卡修正项偏离，不改原型文件）。
- 产品立场：popover 是固定信息架构的**压缩视图**，不是面板平台（pivot §3.4）——KPI 三格、Agent Top4、构成条三段全部锁死，不加任何配置项；内容增减走卡片流程。
- 数据可得性：三格 KPI（今日 tokens / 估算成本 / 缓存命中）与总览页 KPI 同源（IUsageQueryService / KpiCacheShare 同口径）；构成条 = 既有四向拆分（in/out/cache 读/cache 写合并为三段显示）；主要 Agent = 既有 Agent 维度聚合 Top4。
- 常驻正当性定调（2026-10-17 会话）：数据重启可补扫不丢失，常驻价值 = 状态栏 + 未来推送——本卡与 D-014/D-016 就是把「状态栏」做值钱的主线。

## 设计基线修正项（落地照此偏离原型，卡内锁定）

1. **Agent 图标用真品牌标**：`Styles/AgentLogos.axaml`（I-034）+ 既有 `agent-chip` 样式，不用原型的 unicode 字形（✳◈⬡◉）。
2. **颜色全令牌化**：`App*` 前缀 + DynamicResource（禁硬编码色值）；构成条三段色与总览页 Token 构成同系列语义（`AppSeries*`，不随明暗翻转）；原型为暗色单态渐变——落地用 `AppPanelBrush` + 描边对齐既有弹层，亮色变体走 ThemeDictionaries 自动翻转。
3. **最小字号 10px**：原型 9px cap 文字在 Avalonia 里发虚，落地下限 10。
4. **速率口径（"当前会话 · 8.2K/min"）**：= 本轮刷新今日 tokens 差值 ÷ 刷新间隔（后台刷新 1 分钟粒度，DashboardViewModel 每轮回写可对比）；刷新间隔 >1 分钟或无增量时降级「最近活动 hh:mm」；首轮无上一轮基线不显速率。不新增采集口径。
5. **footer 左键改语义**：原型「当前 Session · 8.4K」与 banner 活动态信息重复——改「最近会话 →」直达会话页（与右栏语义一致）或移除，实施时二选一并记录。
6. **点击行为收敛**：agent 行 → 会话页、「查看详情」→ Agent 页、「打开完整统计」→ 总览页、⚙ → 设置页，全部走既有导航命令，不新增路由。

## Acceptance Criteria

- [x] UserControl 按原型信息架构实现三态：Idle（实时）/ Active（工作中 banner）/ Warn（异常 banner，接 AnomalyDetector 既有输出与 `ISystemNotification` 同源信号）
- [x] 数据全走 IUsageQueryService / DashboardViewModel 既有回写，除修正项 4 的差值速率外零新口径
- [x] 明暗两套令牌目检（StylePreviewView 挂预览，沿 D-004/D-005 先例）+ headless 断言不硬编码色
- [x] i18n 全走 Translations（zh 源 + en 覆盖）；空态诚实（无数据 / 扫描中 / 首轮无速率）
- [x] 内容层零平台 API（纯 UserControl，壳在 D-016；Windows/macOS 可同卡 headless 验证）
- [x] 全量测试绿

## Subtasks

- [x] UserControl + ViewModel（今日 KPI / 构成条 / Top4 agent 行 / banner 三态机）
- [x] 速率差值计算挂 DashboardViewModel 回写（含降级路径）
- [x] Translations 键 + StylePreviewView 预览挂载
- [x] headless 测试（三态 / 切语言重译 / 令牌断言）+ 全量回归

## Dependencies

- none 硬依赖；建议 D-014 先行（状态栏文字是 popover 的入口价值前提）

## Test Cases

### TC-001: 三态渲染（headless）

Steps: fixture 数据构造 → 断言 Idle 数字与构成百分比；注入活跃速率 → Active banner；注入异常事件 → Warn banner（warn 配色类生效）。

### TC-002: 切语言重译

Steps: zh→en→zh，断言静态文案随 `CurrentLanguage` 重译（沿 B-007 约定）。

### TC-003: 全量回归

Steps: `dotnet test Tokenmetry.sln`。Expected: 全绿。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Development Log

### 2026-10-17

- 建卡（状态栏路线定稿，原型同日入库）。六条修正项在卡内锁定；速率口径明确为「刷新差值 ÷ 间隔」的派生数据，不做实时流。

### 2026-10-17（实施收口，与 D-016 同日同批落地）

- **落地文件**：`ViewModels/StatusPopoverViewModel.cs`（VM + `StatusPopoverAgentRowViewModel` + `RefreshRateTracker` 三态/速率机）、`Views/StatusPopoverView.axaml(.cs)`（内容层，438px 卡）、`Controls/ProportionalColumns.cs`（构成条占比 → Grid 列定义的附加属性翻译）、`Localization/Translations.cs` 17 键、`Styles/Tokens.axaml` 7 组新令牌、`DashboardViewModel` 增 `AnomalyDetected` 事件（Warn 态同源信号，无论 toast 开关 raise）、`StylePreviewView` 三态演示区。
- **六条修正项逐条记录**：① Agent 图标 = `agent-chip-lg` + `AgentChip.Logo`（I-034 机制，未识别落 generic 散列字形），不用 unicode 字形；② 全部颜色 DynamicResource（构成条三段用 `AppTokenType*Brush` 与总览页 Token 构成同令牌；banner/投影/logo 渐变新增 `AppBanner*`/`AppPopoverShadowColor`/`AppPopoverLogoBackgroundBrush` 令牌，明暗走 ThemeDictionaries 或普通资源——headless 断言固化为「剔注释后零 hex 字面量」）；③ 最小字号 10px；④ 速率 = 相邻两轮今日 tokens 差值 ÷ 轮间隔，有效窗口 45–90s（上限卡定「>1 分钟降级」，下限防"缓存热载 → 首扫完成"的离线积压被几秒除出荒谬速率）；首轮只定基线不显速率；降级「最近活动 hh:mm」取自最近会话探针查询（仅当日显示时间，跨日诚实留空），分 Agent 速率由相邻两次 Agent 查询快照对同公式配对；⑤ footer 二选一取**「最近会话 →」直达会话页**（「当前 Session」与 banner 信息重复，删）；⑥ 点击收敛既有导航：agent 行 → `OpenSessionsForAgent`（D-009 路由转 public，非新增）、「查看详情」→ Agent 页、「打开完整统计」→ 总览页、⚙ → 设置页、「最近会话」→ 会话页。
- **XAML 限制绕行**：Avalonia 12 对 `Grid.ColumnDefinitions` 不支持任何绑定（XAML 编译器按内容-adder 属性拒收 Binding/ReflectionBinding），构成条段宽经 `ProportionalColumns.Shares` 附加属性翻译（AgentChip 同款机制，VM 只给 `double[]` 权重）。
- **诚实空态**：全零（KPI/构成/Agent 皆 0）→ `IsNoData` + KPI "—"（零 token 不冒充数字）；扫描中头部灯切「扫描中…」；首轮无速率 = 仅基线。
- **瞬态语义**：banner 文案按出现时语言（Translations 既定取舍）；`LiveText` 经视图 CurrentLanguage 订阅重算（B-007 挂树/脱树对称）。

## Verification

- Related files: `src/Tokenmetry.App/ViewModels/StatusPopoverViewModel.cs`、`src/Tokenmetry.App/Views/StatusPopoverView.axaml(.cs)`、`src/Tokenmetry.App/Controls/ProportionalColumns.cs`、`Localization/Translations.cs`、`Styles/Tokens.axaml`、`ViewModels/DashboardViewModel.cs`、`Views/StylePreviewView.axaml(.cs)`
- How to run/verify: headless = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter StatusPopover`（TC-001 三态 / TC-002 切语言 / 令牌断言 + 速率跟踪器纯函数 18 例）；目检 = `TOKENMETRY_PREVIEW_WINDOW=1` 明暗两态对照原型（三态按钮驱动 demo）
- Results: **全量 `dotnet test Tokenmetry.sln` 1310 Passed / 0 Failed**（增量 18 例）；三态、速率窗口、降级路径、语言重译、零硬编码色均已锁；真机明暗目检随 D-016 TC-003 一并移交用户。
