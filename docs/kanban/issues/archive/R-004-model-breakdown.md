# R-004: Model Breakdown 页面

## Metadata

- ID: R-004
- Title: Model Breakdown 页面
- Type: Feature
- Priority: P2
- Status: Done (migrated to markdown-kanban board 2026-10-11)
- Size: M
- Milestone: v0.2
- Developer: ZCode
- Reviewer: ZCode (simulated)
- QA: ZCode (simulated)
- Maintainer: User / ZCode (simulated)
- Created: 2026-06-19
- Updated: 2026-10-11

## Goal

新增按模型汇总的 Breakdown 页面，让用户了解不同模型的成本、tokens 和占比。

## Background

`ccusage --json --offline daily` 输出中包含 `modelBreakdowns`，当前 parser 已映射到 `DailyUsageSummary.Models`。UI 尚未提供模型维度分析页面。

## Scope

### In Scope

- [x] 新增 Breakdown / Models Tab
- [x] 按 model 汇总成本和 tokens
- [x] 显示成本占比和 tokens 占比
- [x] 支持无模型数据空状态
- [x] 从缓存 daily rows 计算汇总

### Out of Scope

- Agent 成本拆分，当前 CLI 不提供 agent 级成本
- Project 成本拆分，当前 CLI 暂不提供项目级数据
- 图表可视化

## Subtasks

- [x] 设计 `ModelBreakdownRow` 展示模型
- [x] 实现模型汇总逻辑
- [x] 新增 Breakdown ViewModel/View
- [x] 在 MainWindow 增加页面入口
- [x] 补充汇总逻辑测试

## Acceptance Criteria

- [x] 用户能看到按模型汇总的成本和 tokens
- [x] 总成本与 daily rows 中模型成本合计一致
- [x] 占比计算正确
- [x] 无模型数据时显示空状态
- [x] 不伪造 agent/project 级成本

## Test Cases

### TC-001: 单模型汇总

Steps:
1. 使用只有一个 model 的数据。
2. 打开 Model Breakdown 页面。

Expected:
- 显示一个模型行。
- 成本占比为 100%。

Result:
- Covered by `ModelBreakdownViewModelTests.MapRows_WithSingleModel_ReturnsFullShares`.

### TC-002: 多模型汇总

Steps:
1. 使用包含多个 modelBreakdowns 的数据。
2. 打开 Model Breakdown 页面。

Expected:
- 多个模型分别显示。
- 成本和占比合计正确。

Result:
- Covered by `ModelBreakdownViewModelTests.MapRows_ComputesSharesFromModelTotalsAndSortsByCost` and `LoadAsync_WithModelData_UpdatesSummary`.

### TC-003: 无模型数据

Steps:
1. 使用没有 modelBreakdowns 的 daily rows。
2. 打开 Model Breakdown 页面。

Expected:
- 显示空状态。
- 不展示伪造的 agent/project 成本。

Result:
- Covered by `ModelBreakdownViewModelTests.LoadAsync_WithNoModelData_ShowsEmptyState`.

## Related Files

- `src/TokenPulse.Core/Models/DailyUsageSummary.cs`
- `src/TokenPulse.App/ViewModels/ModelBreakdownViewModel.cs`
- `src/TokenPulse.App/Views/ModelBreakdownView.axaml`
- `src/TokenPulse.App/Views/MainWindow.axaml`
- `tests/TokenPulse.App.Tests/ViewModels/ModelBreakdownViewModelTests.cs`

## Dependencies

- R-001 Reports 每日数据表格建议先完成，以复用数据加载模式。

## Development Log

### 2026-06-19

- 创建任务卡，列入 v0.2 Backlog。
- 开始实现 R-004：复用缓存 daily rows，按 `DailyUsageSummary.Models` 聚合模型成本与 tokens；占比分母使用模型维度合计，避免用 daily row 总量造成不一致；不增加 agent/project 成本拆分。
- 新增 `ModelBreakdownViewModel` / `ModelBreakdownRow`，支持加载、汇总、格式化、空状态和按成本降序排序。
- 新增 `ModelBreakdownView` 并在 MainWindow 增加 `Model Breakdown` Tab。
- 补充 App 层汇总测试、CLI parser 模型映射断言、SQLite nested `Models` roundtrip 断言。
- 验证结果：`dotnet test tests\TokenPulse.App.Tests\TokenPulse.App.Tests.csproj` 通过，37 passed；`dotnet test TokenPulse.sln` 通过，67 passed；`dotnet build TokenPulse.sln` 通过，0 warnings，0 errors。命令输出中有 .NET preview SDK 的 NETSDK1057 informational message。
- 开发完成，移动到 In Review。

## Review Notes

- Status: Ready for review
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - Modification points: 新增模型汇总 ViewModel/View，接入 DI 与 MainWindow Tab；补充 parser/cache/App 测试。
    - How to run/test: `dotnet test tests\TokenPulse.App.Tests\TokenPulse.App.Tests.csproj`、`dotnet test TokenPulse.sln`、`dotnet build TokenPulse.sln`。
    - Impact area: App Reports 邻近页面、MainWindow Tab 布局、daily row 模型数据读取链路。
    - Regression risk: 低到中；新增页面为独立 Tab，DI 构造函数变化会影响 MainWindow 初始化，已通过 build 和 App tests 覆盖。

## QA Notes

- Status: Not Started
- QA: ZCode (simulated)
- Results:
  - 暂未进行手动 GUI QA。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Acceptance Notes

- Status: Not Accepted
- Maintainer: User / ZCode (simulated)
- Notes:
  - 等待验证。
