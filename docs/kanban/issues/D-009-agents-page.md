---
id: D-009
title: Agent 统计页
type: feature
priority: P2
size: S
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

Agent 维度统计页：顶部 Agent KPI 卡（前 4 个 Agent）+ Agent 排行表（会话数 / 输入 / 输出 / 总 Token / 估算成本 / 占比），行点击跳会话明细页并带上该 Agent 筛选。

## Background

- 设计基准：原型 `#page-agents`。
- 数据全部来自 I-029 的 Agent 维度聚合；无新增口径。

## Acceptance Criteria

- [x] KPI 卡显示前 4 个 Agent 的 token / 会话数 / 成本（未知成本显示「未知」）
- [x] 排行表六列数据接 I-029，占比正确
- [x] 行点击跳转会话页且 Agent 筛选生效
- [x] 随全局筛选联动，明暗两套目检

## Subtasks

- [x] AgentsViewModel + KPI 卡
- [x] 排行表 + 跳转联动

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 跳转联动

Steps:
1. 点击排行表某 Agent 行。

Expected:
- 会话页打开且列表只剩该 Agent 会话。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 实现落地：AgentsViewModel（Top4 KPI 固定四槽位对齐原型 .kpis 网格，不足 4 个 Agent 的槽位隐藏）+ AgentsView（复用 kpi / table-th/td/num/empty / icon-chip+AgentChip 变体 / series-dot 既有令牌，无新增令牌），外壳 CreatePage 真实化（注入 `Action<string>` 跳转回调），MainWindow DataTemplates 注册（AppShellTests 路由断言随三页真实化演进）。
- **决策点 2（行点击跳转途径，D-009/D-011 共用定案）：Agent 维度走全局途径**——写 FilterStateService.Agent（顶部栏单选 Agent 是 Agent 维度的跨页筛选宿主：落地会话页即生效、顶部栏可见、可经「所有 Agent」清除），再经注入回调导航；不采用右栏复选组途径（复选集是"全勾=不过滤"的集合语义，单 Agent 过滤要反勾其余项破坏既有勾选，且与全局单选取交集可能得诚实空页——两条途径并存的冲突恰好说明 Agent 写入口应唯一落在全局单选）。目标 Agent 不在选项清单（目录加载失败窗口期）时外壳如实补入选项（UpdateAgentOptions），不静默丢弃跳转。
- 排行表展示维度全部行（含零 token 的会话型 Agent——facts 存在即真实来源，诚实不筛；服务已按 token 降序，页面防御性重排）。
- token 列用紧凑格式 FormatCompactTokens（同 KPI 卡/环心口径）：千分位全数字在定宽列会被裁切成错值（真实数据 1,261,301,001 实测裁尾）——宁紧凑不裁切，与原型 fmt() 全数字为显式偏差（D-010/D-011 同款裁定）。
- 遗留风险：右栏复选组<b>含</b>该 Agent 但被用户反勾（曾提交的子集）时，跳转仍按 D-008 交集语义呈诚实空态（未勾选状态在右栏可见、「清除」可复位）——尊重用户显式筛选，不做隐式覆盖；Agent 完全不在快照内的变体已由审查修复轮 P1 兜底（见 2026-10-13 审查修复轮条目）。

### 2026-10-13（审查修复轮）

- **P1：修复 Agent 跳转"补入选项"死胡同变体**。触发链：后台刷新使新 Agent X 入库（AgentCatalogService 目录缓存整个生命周期不刷新）→ Agent 页显示 X → 点击 X 行 → 原实现只把 X 追加进全局 AgentOptions，而右栏复选组是启动期快照（不含 X、全默认勾选）→ 会话页 ResolveAgentScope：全局=X、AgentFilterCount>0、checkedIds 不含 X → Empty 不发查询硬呈"显示 0 / 0 条"，且「清除」只重勾已知复选项（X 仍不在）救不回。修复（方案 a）：OpenSessionsForAgent 补全局选项时同步向右栏复选组补入一个**已勾选**的 AgentQuickFilterViewModel（页级交集恢复成立；状态在右栏可见、可手动反勾 / 「清除」复位）；复选组为空（目录未加载的诚实兜底态）时不补入——count==0 分支已按 Single(全局) 兜底，补入会伪装"目录只含 X"的已加载态。新增端到端回归 `Agent_jump_with_agent_outside_checkbox_snapshot_shows_sessions_and_recovers`（快照不含 X + 全局=X → 会话页出数 x1 + 「清除」后仍按 X 出数，覆盖修复前 0/0 死胡同）。
- **P2：竞态安全的未成文不变量落成注释**——补入幂等依赖 AgentCatalogService 目录查询恒为无界严格超集（UsageQueryFilter 无界、facts 存在即在清单中）+ AgentOption record 值相等重绑；已在 OpenSessionsForAgent 与 AgentCatalogService 类型注释双向锁定（"无界超集语义是契约的一部分，勿收窄"）。
- Verification 测试数字随新增回归更新（Headless 24 例 / 全套件 987 通过）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/ViewModels/AgentsViewModel.cs`、`src/TokenPulse.App/Views/AgentsView.axaml(.cs)`、`src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（CreatePage + OpenSessionsForAgent 含 P1 复选组补入）、`src/TokenPulse.App/Services/AgentCatalogService.cs`（无界超集契约注释）、`tests/TokenPulse.App.Headless.Tests/Shell/DimensionPagesTests.cs`
- How to run/verify: `dotnet test tests/TokenPulse.App.Headless.Tests/TokenPulse.App.Headless.Tests.csproj`（DimensionPagesTests 24 例）+ 明暗截图目检
- Results: 2026-10-13（审查修复轮后）全套件 987 通过 / 22 跳过 / 0 失败 / 0 警告（基线 962/22/0，新增 25 例）。TC-001 端到端通过（`Agent_row_click_navigates_shell_to_filtered_sessions`）+ P1 死胡同变体回归（`Agent_jump_with_agent_outside_checkbox_snapshot_shows_sessions_and_recovers`）通过；绑定/Top4/成本四态/全局筛选联动/序号防护/Dispose 退订各有用例。截图 `docs/design/agents-light.png`、`docs/design/agents-dark.png`（真实数据：本机 7 天窗口单 Agent ZCode，1261.3M tokens / $0.00 已知为零）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：三卡组合审查结论"有问题需修复"——1×P1 + 3×P2；契约演进推导、对账三分支手算、两条跨页途径端到端、TC-001 合计口径、数字裁切修复、三页模式合规全部核实为真。修复（原开发 agent 完成，PM 验收）：
1. **P1 Agent 跳转死胡同**（目录缓存不刷新时新 Agent 只补全局选项、右栏复选组快照不含它 → 会话页 Empty 硬 0/0 且「清除」不可恢复）：OpenSessionsForAgent 补全局选项时同步补入已勾选的右栏复选项（复选组非空才补，空组走既有 Single 兜底），死胡同解除且状态对用户可见可清除；新增死胡同变体端到端回归。
2. P2 tie-break 对账缺区分度（键序数胜者恰巧 token 更多）→ 交换 fixture token 量级使断言 discriminating。
3. P2 目录无界超集不变量落成双向注释锁定（防后人收窄目录查询）。
4. P3 DominantAgent XML doc 补第三种 null 来源（会话无有效 Agent 归属）。
PM 验收：build 0 警告 + 全量 987 通过 / 22 跳过 / 0 失败。
