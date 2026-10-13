---
id: D-012
title: 时间分析页（小时分布 + 活跃日历热力图）
type: feature
priority: P2
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

时间分析页：按小时分布图（D-004 小时形态，本地时区）+ 活跃日历热力图（D-005）+ 时间维度观察项说明表。数据边界约束落地：无精确事件时间的源不进小时分布，页面显示「未知」占比说明。

## Background

- 设计基准：原型 `#page-time`。
- 原型 meta 文案「缺少精确事件时间的来源应显示未知，不应伪造小时级分布」对应产品立场，I-029 已按此返回 unknown 计数。
- 会话长度观察项（原型建议表第二条）依赖会话持续时间数据，若 session_facts 无此字段则该行显示为建议性文案，不做假数据。

## Acceptance Criteria

- [x] 小时分布图接 I-029（本地时区），unknown 源显式排除并在页面标注占比（排除由 I-029 查询层完成，页面经 TrendChart HourlyTokens 直接消费 + unknown 标注行）
- [x] 热力图按日 token 强度着色，hover 显示当日数值，接 I-028/029 日序列（补零日淡格呈现，选型见 Dev Log）
- [x] 观察项说明表三行内容与真实数据能力一致（不承诺没有的字段）
- [x] 随全局筛选联动，明暗两套目检（docs/design/time-{light,dark}.png）

## Subtasks

- [x] TimeViewModel（小时分布 + 日强度）
- [x] 图表接入与 unknown 标注
- [x] 观察项表内容定稿

## Dependencies

- D-003, I-028, I-029, D-004, D-005

## Test Cases

### TC-001: unknown 标注

Steps:
1. fixture 含无分钟级时间戳的源。
2. 查看小时分布与标注。

Expected:
- 该源记录不出现在小时分布；页面可见「未知」占比说明。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 实现完成（`TimeViewModel` + `TimeAnalysisView`，外壳路由 `PageId.Time` 真实化，`MainWindow.axaml` DataTemplate 接线）。页面结构对齐原型 `#page-time`：左「按小时分布」（TrendChart HourlyTokens）+ 右「活跃日历」（Heatmap）+ 「时间维度建议观察项」静态三行表。
- **unknown 占比分母定义（决策点）**：占比 = unknown 会话数 /（入桶会话数 + unknown 会话数）。分母取"会话数"而非 token——I-029 不变式「每个会话要么入桶要么计入 unknown，无静默丢失」使该分母完整；token 体量另以「涉及 M tokens 用量」追加展示（UnknownTotalTokens）。unknown=0 时标注隐藏（不显示「0 个」占位）。
- **热力图补零日选型（决策点）**：双端有界窗口（7/30/90 天）的补零日（HasData=false）以**淡格**保留（Heatmap 既有空态底色 + 「无数据」tooltip），不从序列丢弃——对齐 D-002 补零定案（日历连续性可辨，稀疏日不画成连续活跃）与 TrendChart DailyTokens 同一语义；无界 All 只返回实际有数据日，自然只画实际日。复用 D-005 既有样式，零新增渲染。
- **观察项表三行依据（决策点）**：读 `SqliteUsageStore` 建表 DDL 核实 session_facts **有** first_seen_at / last_seen_at——会话时长可推导，故第二行不是纯"无字段"兜底文案，但如实标注 I-029 已知限制（部分来源为采集兜底时间戳，时长仅供参考）；第一行（高峰时段）对应小时分布+按日成本真实能力；第三行（数据边界）为 I-029 unknown 契约的产品立场表述，与页面 unknown 标注互相印证。全表为静态文案块，不携带数据。
- TC-001 落地说明：页面层（headless）stub 按 I-029 契约回传「8 会话入桶 + 2 unknown」的分布（无精度源不入桶），断言标注可见；排除判定逻辑本身由 `UsageQueryServiceTests.Hourly_distribution_excludes_imprecise_timestamps_and_counts_unknown` 在服务层钉死（纪元兜底 / 本地日锚定构造同源）。
- 测试：新增 `TimeCostPagesTests` 16 用例（unknown 标注可见/隐藏、热力图补零格保留、观察项表内容、筛选联动窗口映射、序号防护、错误保留、Dispose（VM 级 + 外壳导航离开端到端）、真实服务冒烟、明暗截图）；AppShellTests 两个占位页用例随 Time/Cost 真实化改写（占位语义收敛到 VM 层，外壳 Dispose 端到端迁入本类）。全量 1003 通过 / 22 跳过 / 0 失败、0 警告（基线 987 + 新增 16，无回归）。
- 遗留风险：① I-029 已知边界——真实发生在 UTC 午夜 / 本地午夜的会话被保守排除计入 unknown（可见非丢失），权威逐记录精度判断需采集层加列（未来迁移）；② 90 天窗口热力图 13 行约 380px，页面已包 ScrollViewer，窄窗可滚动查看；③ 本机真实数据 unknown=0，标注行为以 stub 用例与真实服务冒烟共同覆盖。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/ViewModels/TimeViewModel.cs`, `src/CCUsage.App/Views/TimeAnalysisView.axaml(.cs)`, `src/CCUsage.App/ViewModels/MainWindowViewModel.cs`（路由）, `src/CCUsage.App/Views/MainWindow.axaml`（DataTemplate）, `tests/CCUsage.App.Headless.Tests/Shell/TimeCostPagesTests.cs`
- How to run/verify: `dotnet test tests/CCUsage.App.Headless.Tests --filter FullyQualifiedName~TimeCostPagesTests`（16 用例，含 TC-001 unknown 标注）；明暗目检 `docs/design/time-light.png` / `time-dark.png`（重生成：`CCUSAGE_TIMECOST_PREVIEW_DIR=docs/design dotnet test --filter Time_and_cost_pages_render_light_and_dark_screenshots_for_archive`）
- Results: 2026-10-13 全量 `dotnet test CCUsage.sln` 1003 通过 / 22 跳过 / 0 失败、0 警告；TC-001 通过（无精度源不入桶 + 页面「2 个会话缺少精确时间……占 20.0%」可见）；明暗截图已留档并目检（真实本机数据：小时分布 / 热力图 / 观察项表渲染正常，本机 unknown=0 故标注按设计隐藏）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论通过，无 P0/P1。unknown 占比口径（完备不变式有测试锁定、占比算式手算一致）、热力图数据链（强度分母=窗口内有数据日最大值、补零淡格对齐 D-002 定案、无界实际日语义）、方案 b 诚实性（脚注卡内可见非藏 tooltip、分母算式核实、AC 偏差登记一致）、PricingEngine 接线（真引擎 TC-001 + 3→4 实时联动、单例同法核实、全局口径筛选不变性有测）、静态表逐句对照原型与对账文档（无凭记忆表述）、超范围零改动全部核实。P2 三条由 PM 处理：① 看板状态迁移（本次收尾操作）；② D-013 卡用例计数 9→8 更正；③ 成本页脚注补「（自启动累计）」语义（174/174 headless 复验通过）。PM 验收：全量 1003 通过 / 22 跳过 / 0 失败维持。
