# R-002: Reports 排序和筛选

## Metadata

- ID: R-002
- Title: Reports 排序和筛选
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

为 Reports 每日数据表格增加基础排序和筛选能力，方便用户按日期、成本、tokens、agent 或 model 查找记录。

## Background

R-001 将提供每日数据表格。随着历史数据增长，用户需要快速定位高成本日期或特定模型的使用记录。

## Scope

### In Scope

- [ ] 支持按日期、成本、tokens 排序
- [ ] 支持日期范围筛选
- [ ] 支持 agent/model 文本筛选
- [ ] 显示筛选后的记录数量与合计成本

### Out of Scope

- 保存筛选条件
- 高级查询语言
- 分页虚拟化优化

## Subtasks

- [x] 设计 Reports 筛选状态模型
- [x] 实现排序逻辑
- [x] 实现日期范围筛选
- [x] 实现 agent/model 文本筛选
- [x] 补充排序和筛选单元测试
- [x] 更新 Reports UI

## Acceptance Criteria

- [ ] 用户可以按日期/成本/tokens 排序
- [ ] 用户可以筛选日期范围
- [ ] 用户可以按 agent 或 model 过滤
- [ ] 筛选后合计值正确
- [ ] 清空筛选后恢复完整列表

## Test Cases

### TC-001: 成本降序排序

Steps:
1. 打开 Reports。
2. 选择成本降序排序。

Expected:
- 高成本日期排在前面。

### TC-002: 日期范围筛选

Steps:
1. 设置开始日期和结束日期。
2. 应用筛选。

Expected:
- 只显示日期范围内记录。
- 合计值只统计筛选结果。

### TC-003: Model 文本筛选

Steps:
1. 输入一个存在的 model 名称片段。
2. 应用筛选。

Expected:
- 只显示包含该 model 的日期记录。

## Related Files

- `src/TokenPulse.App/ViewModels/ReportsViewModel.cs`
- `src/TokenPulse.App/Views/ReportsView.axaml`
- `src/TokenPulse.Core/Models/DailyUsageSummary.cs`

## Dependencies

- R-001 Reports 每日数据表格

## Development Log

### 2026-06-19

- 创建任务卡，依赖 R-001。
- 开始开发，方案为在 ReportsViewModel 中维护完整行集合和当前显示集合，增加日期范围、agent/model 文本筛选、排序与筛选合计，并更新 Reports UI 工具栏和 App 测试。
- 完成 ReportsViewModel 查询状态、完整行缓存、日期范围筛选、agent/model 大小写不敏感筛选、日期/成本/tokens 排序、筛选计数和筛选成本合计。
- 更新 Reports UI，新增日期范围、agent/model 搜索、排序、降序、应用/清空工具栏和筛选摘要。
- 补充 App 测试覆盖排序、筛选、合计、清空筛选和无效日期处理。
- `dotnet test tests\\TokenPulse.App.Tests\\TokenPulse.App.Tests.csproj` 通过：App 13 个测试通过。
- `dotnet test TokenPulse.sln` 通过：Core 17、Infrastructure 12、App 13，共 42 个测试通过。
- `dotnet build TokenPulse.sln` 通过：0 warning，0 error。环境提示正在使用 .NET 预览版 SDK（NETSDK1057）。
- 未执行 GUI 手动验证；需要在可交互桌面环境中确认 toolbar 布局。
- 移动到 In Review，等待模拟 review/QA 或人工验收。

## Review Notes

- Status: Not Reviewed
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - 修改点：Reports 增加筛选/排序 query 状态、筛选统计、UI 工具栏和 App 单元测试。
    - 测试方式：`dotnet test tests\\TokenPulse.App.Tests\\TokenPulse.App.Tests.csproj`、`dotnet test TokenPulse.sln`；构建方式：`dotnet build TokenPulse.sln`。
    - 影响范围：Reports ViewModel、Reports Avalonia UI、App 测试。
    - 回归风险：Reports 加载和默认日期倒序保留；主要风险在工具栏布局和纯文本日期输入体验，需要 GUI 手动确认。

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
