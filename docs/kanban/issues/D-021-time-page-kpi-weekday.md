---
id: D-021
title: 时间分析页扩充——时间 KPI 四卡 + 星期分布面板
type: feature
priority: P2
size: M
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

时间分析页在 D-012（小时分布 + 活跃日历 + 静态观察项表）基础上扩充两块真实数据内容，落实页副标题「识别使用高峰、工作时段分布」中尚无数据呈现的两项：

1. **时间 KPI 四卡**（页头下、版式对齐成本分析页）：高峰时段 / 最活跃日 / 日均 tokens / 活跃天数。
2. **星期分布面板**（图表行与观察项表之间）：周一..周日七行（标签 + mini bar + tokens·占比），meta 工作日/周末占比。

**约束：零新查询**——全部由页面已加载的 I-029 小时分布 + I-028 日序列客户端聚合；不新增接口、不动存储。

## Background

- 用户反馈「时间分析页签现在还很空」；D-012 交付后页面只有两张图 + 一张静态文字表，副标题承诺的三项里「工作时段分布」无任何数据呈现。
- 候选第三项「会话时长视角」被否：需新增查询，且 D-012 已定案「部分来源为采集兜底时间戳，时长仅供参考」，做它等于部分逆转既有决策——不做（边界见 Non-goals）。
- 产品立场（opinionated-usage-stats-pivot §5）：克制的数据表达——趋势/构成/TopN 够用，KPI 卡 + 条形构成行都在既有表达语汇内。

## Acceptance Criteria

- [x] KPI 四卡零新查询（I-029/I-028 同源聚合）；无数据/未加载 "—"（不冒充 0，同 Overview Pending 语义）
- [x] 高峰时段卡脚注 = 占入桶 Token 百分比（与小时分布同口径，分母不含 unknown——诚实标注）
- [x] 活跃天数卡脚注分窗口口径：双端有界「窗口共 N 天」/ 无界「自 {首日} 起」（无界窗口天数不可知不伪造）
- [x] 日均卡口径 = Σ HasData 日 tokens / 活跃日数（补零日不摊薄，脚注显式标注）
- [x] 星期分布：HasData 日按本地星期聚合（补零日不入桶），bar = 占窗口全部 token 百分比（与 DimensionRowViewModel 同口径）；Σ token=0 整块隐藏（不画全零行伪信息）
- [x] 星期分布脚注注明口径差异：日序列含无精确时间会话（同活跃日历），与仅含精确时间的小时分布不同
- [x] Translations 中英全键覆盖（zh 源 + en 覆盖，键奇偶零差异）
- [x] 随全局筛选联动（复用 LoadAsync，序号防护/错误保留/Dispose 纪律同 D-012，不另测）
- [x] 明暗目检留档（time-enriched-{light,dark}.png，stub 数据版——见 Dev Log 隔离空库深因）

## Subtasks

- [x] TimeViewModel：TimeKpiCardViewModel / WeekdayUsageRowViewModel + BuildTimeKpis / BuildWeekdayDistribution（internal 供测试对账）
- [x] TimeAnalysisView：KPI 行（四卡单排，icon-chip Blue/Purple/Green/Neutral + clock/zap/coins/table 既有几何）+ 星期分布面板（ProgressBar.mini 复用）
- [x] Translations 22 键（4 卡标题 + 5 脚注格式 + 面板标题 + meta + 口径脚注 + 7 星期名）中英双语
- [x] headless 测试 4 用例（数值对账 / 空态隐藏 / 星期聚合 / KPI 几何契约）+ stub 数据明暗截图测试
- [x] 既有截图测试修正：导航后泵到数据加载完成再抓帧（消除「启动中」飞行中帧）

## Dependencies

- D-012（页面骨架）、I-028（日序列）、I-029（小时分布）、D-013（KPI 卡版式参照）

## Test Cases

### TC-001: KPI 数值对账

Steps: stub 回传 9 点 50k / 14 点 30k 入桶 + 2 unknown + 日序列（3-10 周二 6750 / 3-11 补零 / 3-12 周四 1200）。
Expected: 高峰 "09:00–10:00" 脚注占入桶 62.5%；最活跃日 2026-03-10；日均 3975（÷2 活跃日）；活跃天数 2 脚注「窗口共 7 天」；无界 All 档脚注转「自 2026-03-10 起」。

### TC-002: 星期分布聚合

Steps: 乱序日序列（补零周三 + 周二 6750 + 周六 2000）。
Expected: 七行周一..周日；周二 6750 bar 77.1%、周六 2000 bar 22.9%，其余 0；meta「工作日 77.1% · 周末 22.9%」。

### TC-003: 空态

Steps: 空 stub。
Expected: 四卡 "—" 脚注全隐藏；HasWeekdayRows=false、meta=null（整块隐藏）。

## Development Log

### 2026-10-25

- TDD 全程：新增 4 用例先 RED（CS1061 缺成员实锤）→ GREEN。实现为纯客户端聚合，`BuildTimeKpis(hourly, daily, filter)` / `BuildWeekdayDistribution(daily)` internal 可直测。
- **并列取值定案**：高峰时段 token 并列取最早小时、最活跃日并列取最早日（稳定可复现，不引入次序抖动）。
- **23 点高峰显示**：`{(h+1)%24:00}` 跨午夜写 00:00（"23:00–00:00"），不留 "24:00"。
- **星期映射**：`((int)DayOfWeek + 6) % 7`（周一=0..周日=6），yyyy-MM-dd 严格解析（不可解析日静默跳过）。
- **既有截图测试两处深因修正**：① 抓帧停在「启动中… 尚未刷新」飞行中态——导航后 `DispatcherPump.PumpUntil(谓词)`（冒烟测试同口径）再抓；② 修完仍是空态——追因（临时诊断测试直查 DI 单例，用后即删）：headless 测试经 `SemiTestAppBuilder` 的 `AppServices.PathsOverride` 读**隔离临时库**（隔离是有意设计），恒无数据 → KPI "—"、星期分布隐藏是空库的诚实渲染而非回归。为此新增 stub 数据版明暗留档（time-enriched-*.png），与真实 DI 空态留档（time-*.png）并存，各自记录一种状态。
- **与 D-020 并行会话同工作区**：其 TimeRangeOption 重构（Options→命名实例/InitialOptions）曾使全测试项目编译不过；本卡做了机械迁移（旧下标 [0..5]→Today/Last7Days/Last30Days/Last90Days/All/Custom 命名实例、AppShellTests 线程编组用例捕获类型随新类型），语义逐点保持，属互相解阻塞（Q-011 先例）。GlobalFilterMapperTests 的 TimeRange 枚举删除属 D-020 语义工作未越界（对方随后自修）。
- 测试：headless 新增 5 用例（4 数据 + 1 stub 截图）；全量 1536 通过 / 0 失败 / 22 跳过（Core 189 / Infrastructure 661+3 / Reconciliation 49+19 / App 253 / headless 384）。
- 明暗目检：time-enriched-light/dark.png（stub 数据）四卡数值脚注齐全、星期分布两行有条有值、unknown 标注在；time-light/dark.png（隔离空库）"—" 空态诚实呈现。真机观感移交用户。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/ViewModels/TimeViewModel.cs`、`src/Tokenmetry.App/Views/TimeAnalysisView.axaml`、`src/Tokenmetry.App/Localization/Translations.cs`、`tests/Tokenmetry.App.Headless.Tests/Shell/TimeCostPagesTests.cs`
- How to run/verify: `dotnet test tests/Tokenmetry.App.Headless.Tests --filter FullyQualifiedName~TimeCostPagesTests`（22 用例含 TC-001~003）；留档重生成 `TOKENMETRY_TIMECOST_PREVIEW_DIR=docs/design dotnet test --filter "FullyQualifiedName~render_light_and_dark_screenshots"`
- Results: 2026-10-25 全量五套件 1536 通过 / 0 失败 / 22 跳过；time-enriched-{light,dark}.png 已留档并目检。
