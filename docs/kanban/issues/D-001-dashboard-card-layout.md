# D-001: Dashboard 卡片布局优化

## Metadata

- ID: D-001
- Title: Dashboard 卡片布局优化
- Type: Feature
- Priority: P1
- Status: In Review
- Size: S
- Milestone: v0.1
- Developer: ZCode
- Reviewer: ZCode (simulated)
- QA: ZCode (simulated)
- Maintainer: User / ZCode (simulated)
- Created: 2026-06-19
- Updated: 2026-06-19

## Goal

优化 Dashboard 卡片布局，让当前周期数据和历史数据的含义更清晰，避免用户误以为今日/本周为 0 是数据加载失败。

## Background

当前 Dashboard 已能显示今日、本周、本月、最近一天和历史累计，但布局仍偏原型，信息层级不够清晰。

## Scope

### In Scope

- [ ] 重排 Dashboard 卡片信息层级
- [ ] 明确区分当前周期数据与历史累计数据
- [ ] 今日/本周/本月均为 0 但历史有数据时显示提示
- [ ] 保留刷新按钮和状态信息

### Out of Scope

- 趋势图
- 明细表格
- 主题系统

## Subtasks

- [x] 梳理 Dashboard 卡片字段
- [x] 调整 `DashboardView.axaml` 布局
- [x] 必要时补充 ViewModel 展示属性
- [ ] 手动验证有今日数据和仅历史数据两种状态

## Acceptance Criteria

- [ ] 用户能清楚看到今日、本周、本月和历史累计
- [ ] 当当前周期无数据但历史有数据时，界面给出明确提示
- [ ] Dashboard 刷新后布局不跳动或遮挡
- [ ] App 构建通过

## Test Cases

### TC-001: 仅历史数据时提示明确

Steps:
1. 使用只有历史日期的数据。
2. 打开 Dashboard。

Expected:
- 当前周期卡片可以为 0。
- 历史累计或最近有数据日期清晰可见。
- 用户不会看到空状态。

### TC-002: 当前日期有数据时显示当前周期

Steps:
1. 使用包含今日记录的数据。
2. 打开 Dashboard。

Expected:
- 今日成本和今日 tokens 显示非 0。
- 历史累计仍正常显示。

## Related Files

- `src/CCUsage.App/Views/DashboardView.axaml`
- `src/CCUsage.App/ViewModels/DashboardViewModel.cs`
- `src/CCUsage.Core/Models/DashboardSummary.cs`

## Dependencies

- 当前 Dashboard 已能加载真实 ccusage 数据

## Development Log

### 2026-06-19

- 创建任务卡，列入 v0.1 Backlog。
- 开始开发，复用已有 latest/all-time dashboard summary 字段，补充当前周期为空但历史有数据的提示属性，并优化 Dashboard 当前周期/历史累计卡片层级。
- 完成 DashboardViewModel 历史数据/当前周期状态属性和历史-only 提示判断。
- 完成 Dashboard 布局重排：当前周期区与历史/最近数据区分离，补充历史累计 tokens 与最近一天 tokens。
- 补充 App 层 DashboardViewModel 提示逻辑测试。
- `dotnet test CCUsage.sln` 通过：Core 17、Infrastructure 10、App 5，共 32 个测试通过。
- `dotnet build CCUsage.sln` 通过：0 warning，0 error。环境提示正在使用 .NET 预览版 SDK（NETSDK1057）。
- 移动到 In Review，等待模拟 review/QA 或人工验收。

## Review Notes

- Status: Not Reviewed
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - 修改点：新增 Dashboard 历史数据/当前周期状态属性、historical-only 提示判断，重排 Dashboard 当前周期与历史累计卡片，并补充 App ViewModel 测试。
    - 测试方式：`dotnet test CCUsage.sln`；构建方式：`dotnet build CCUsage.sln`。
    - 影响范围：Dashboard ViewModel、Dashboard Avalonia 布局、App 测试。
    - 回归风险：Dashboard 数据加载链路未改变；主要风险在 UI 布局显示，需要后续人工或 GUI 环境确认无遮挡。

## QA Notes

- Status: Not Started
- QA: ZCode (simulated)
- Results:
  - 暂无。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Acceptance Notes

- Status: Not Accepted
- Maintainer: User / ZCode (simulated)
- Notes:
  - 暂无。
