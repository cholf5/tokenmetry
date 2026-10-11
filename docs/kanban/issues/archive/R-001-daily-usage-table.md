# R-001: Reports 每日数据表格

## Metadata

- ID: R-001
- Title: Reports 每日数据表格
- Type: Feature
- Priority: P1
- Status: Done (migrated to markdown-kanban board 2026-10-11)
- Size: M
- Milestone: v0.1
- Developer: ZCode
- Reviewer: ZCode (simulated)
- QA: ZCode (simulated)
- Maintainer: User / ZCode (simulated)
- Created: 2026-06-19
- Updated: 2026-10-11

## Goal

实现类似 `ccusage daily` 的每日数据表格，让用户能在 GUI 中核对每一天的成本、tokens 和模型信息。

## Background

当前 Dashboard 已能显示真实数据和汇总卡片，但缺少每日明细表。用户无法像使用 `ccusage` CLI 一样逐日核对原始用量。

## Scope

### In Scope

- [ ] 新增 Reports Tab
- [ ] 展示每日用量表格
- [ ] 默认按日期倒序显示
- [ ] 显示 Date、Cost、Total Tokens、Input、Output、Cache Creation、Cache Read、Agents、Models
- [ ] 从 `CacheService.GetDailyRowsAsync()` 加载缓存数据
- [ ] 提供无数据空状态

### Out of Scope

- CSV 导出
- 复杂筛选
- 分页
- 图表
- 项目级拆分

## Subtasks

- [x] 新增 `ReportsViewModel`
- [x] 新增 `ReportsView`
- [x] 在依赖注入中注册 Reports ViewModel
- [x] 在 `MainWindow.axaml` 增加 Reports Tab
- [x] 将 `DailyUsageSummary` 映射为表格行显示模型
- [x] 补充可测试的排序/格式化逻辑
- [ ] 手动验证真实 ccusage 数据显示

## Acceptance Criteria

- [ ] App 中存在 Reports Tab
- [ ] 点击 Dashboard 刷新并获取数据后，Reports 能显示每日记录
- [ ] 表格默认按日期倒序排列
- [ ] 成本和 tokens 与缓存中的 `DailyUsageSummary` 一致
- [ ] 没有缓存数据时显示空状态而不是崩溃
- [ ] 相关测试通过
- [ ] App 构建通过

## Test Cases

### TC-001: 有真实数据时显示每日表格

Steps:
1. 启动 App。
2. 在 Dashboard 点击刷新数据。
3. 打开 Reports Tab。

Expected:
- 表格显示多条每日记录。
- 日期倒序排列。
- 成本和 tokens 不全为 0。

### TC-002: 无缓存数据时显示空状态

Steps:
1. 清空或使用空缓存。
2. 启动 App。
3. 打开 Reports Tab。

Expected:
- 显示无数据提示。
- App 不崩溃。

### TC-003: 字段格式正确

Steps:
1. 打开含有数据的 Reports Tab。
2. 查看成本、tokens、agents、models 列。

Expected:
- 成本使用美元格式。
- tokens 使用千分位。
- agents/models 使用逗号分隔文本。

## Related Files

- `src/CCUsage.App/ViewModels/ReportsViewModel.cs`
- `src/CCUsage.App/Views/ReportsView.axaml`
- `src/CCUsage.App/Views/MainWindow.axaml`
- `src/CCUsage.App/DependencyInjection/AppServices.cs`
- `src/CCUsage.Core/Models/DailyUsageSummary.cs`
- `src/CCUsage.Core/Services/CacheService.cs`

## Dependencies

- Dashboard 刷新流程已能保存 `DailyUsageSummary`
- `SqliteUsageCache.GetDailyRowsAsync()` 可用

## Development Log

### 2026-06-19

- 创建任务卡，列入 v0.1 当前待办。
- 开始开发，方案为新增 Reports ViewModel/View，从 CacheService 加载 daily rows，显式按日期倒序映射表格行，并补充排序/格式化测试。
- 完成 ReportsViewModel、ReportsView、DI/MainWindow 接入和 App 测试项目。
- `dotnet test CCUsage.sln` 通过：Core 17、Infrastructure 10、App 3，共 30 个测试通过。
- `dotnet build CCUsage.sln` 通过：0 warning，0 error。环境提示正在使用 .NET 预览版 SDK（NETSDK1057）。
- 移动到 In Review，等待模拟 review/QA 或人工验收。

## Review Notes

- Status: Not Reviewed
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - 修改点：新增 Reports tab、ReportsViewModel、ReportsView、表格行映射/格式化、App 测试项目，并接入 DI 与 MainWindow。
    - 测试方式：`dotnet test CCUsage.sln`；构建方式：`dotnet build CCUsage.sln`。
    - 影响范围：Avalonia App 主窗口、Reports 页面、测试 solution 配置。
    - 回归风险：Dashboard 刷新流程未改动；Reports 读取缓存需要用户在 Dashboard 刷新后点击 Reports 的“刷新列表”以重新读取最新缓存。

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
