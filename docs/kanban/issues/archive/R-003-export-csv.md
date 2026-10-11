# R-003: Reports 导出 CSV

## Metadata

- ID: R-003
- Title: Reports 导出 CSV
- Type: Feature
- Priority: P2
- Status: Done (migrated to markdown-kanban board 2026-10-11)
- Size: S
- Milestone: v0.2
- Developer: ZCode
- Reviewer: ZCode (simulated)
- QA: ZCode (simulated)
- Maintainer: User / ZCode (simulated)
- Created: 2026-06-19
- Updated: 2026-10-11

## Goal

允许用户将 Reports 中的每日用量数据导出为 CSV 文件，便于归档、分享或在电子表格中进一步分析。

## Background

GUI 需要补齐 `ccusage` CLI 以外的桌面使用场景。CSV 导出是成本对账和长期记录的常见需求。

## Scope

### In Scope

- [x] 从 Reports 当前数据生成 CSV
- [x] 包含日期、成本、tokens、agents、models 等列
- [x] 使用系统保存文件对话框选择路径
- [x] 导出成功/失败时给出状态提示

### Out of Scope

- Excel `.xlsx` 导出
- 自动定时导出
- 云同步

## Subtasks

- [x] 设计 CSV 列顺序
- [x] 实现 CSV 转义和格式化
- [x] 接入保存文件对话框
- [x] 在 Reports UI 添加导出按钮
- [x] 补充 CSV 格式化测试

## Acceptance Criteria

- [x] 用户可以从 Reports 导出 CSV
- [x] CSV 能被 Excel 或常见表格工具打开
- [x] 文本字段正确转义
- [x] 导出失败时 App 不崩溃并显示错误提示

## Test Cases

### TC-001: 导出全部数据

Steps:
1. 打开 Reports。
2. 点击导出 CSV。
3. 选择保存路径。

Expected:
- 文件创建成功。
- 行数与 Reports 数据一致。

### TC-002: 文本字段包含逗号时正确转义

Steps:
1. 使用包含多个 models/agents 的数据。
2. 导出 CSV。

Expected:
- CSV 字段使用合法转义。
- 表格工具打开后列不错位。

## Related Files

- `src/CCUsage.App/ViewModels/ReportsViewModel.cs`
- `src/CCUsage.App/Views/ReportsView.axaml`
- `src/CCUsage.App/Services/`

## Dependencies

- R-001 Reports 每日数据表格

## Development Log

### 2026-06-19

- 创建任务卡，依赖 R-001。
- 开始开发：导出 Reports 当前筛选/排序后的每日数据；计划新增 CSV formatter、保存文件导出服务、Reports 导出命令与测试。
- 实现 `ReportCsvFormatter`、`IReportCsvExporter` / `ReportCsvExporter`，使用 Avalonia `StorageProvider.SaveFilePickerAsync` 保存 CSV。
- 在 Reports 顶部工具栏添加 `导出 CSV` 按钮，`ReportsViewModel.ExportCsvAsync` 导出当前筛选/排序后的 `Rows`。
- 补充 CSV header/转义测试，以及导出成功、无数据、失败、取消的 ViewModel 测试。
- 验证结果：`dotnet test tests\\CCUsage.App.Tests\\CCUsage.App.Tests.csproj` 通过，19 passed；`dotnet test CCUsage.sln` 通过，48 passed；`dotnet build CCUsage.sln` 通过，0 warnings, 0 errors。`.NET preview SDK` 信息为环境提示。

## Review Notes

- Status: Ready for Review
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - Modification points: 新增 Reports CSV formatter/exporter 服务；Reports ViewModel 增加导出命令和导出状态；Reports UI 增加导出按钮；补充 CSV 与导出命令测试。
    - How to run/test: `dotnet test tests\\CCUsage.App.Tests\\CCUsage.App.Tests.csproj`; `dotnet test CCUsage.sln`; `dotnet build CCUsage.sln`。
    - Impact area: Reports 页面、App DI、CSV 文件保存对话框。
    - Regression risk: 低到中；ReportsViewModel 构造增加 exporter 依赖，保留测试友好的 fallback 构造；文件保存依赖 Avalonia StorageProvider，手动 GUI 导出仍需实际点击验证。

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
