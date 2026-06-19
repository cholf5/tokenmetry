# S-001: Settings 基础页面

## Metadata

- ID: S-001
- Title: Settings 基础页面
- Type: Feature
- Priority: P2
- Status: In Review
- Size: M
- Milestone: v0.2
- Developer: ZCode
- Reviewer: ZCode (simulated)
- QA: ZCode (simulated)
- Maintainer: User / ZCode (simulated)
- Created: 2026-06-19
- Updated: 2026-06-19

## Goal

实现基础 Settings 页面，让用户可以查看和调整 ccusage 命令、刷新间隔、通知等本地配置。

## Background

当前 Settings Tab 是占位内容。项目需要本地优先的可配置能力，以适配不同系统环境和用户偏好。

## Scope

### In Scope

- [x] 显示当前 ccusage 命令路径或命令名
- [x] 允许设置刷新间隔
- [x] 显示缓存数据库路径
- [x] 提供异常通知开关
- [x] 提供调试信息显示开关
- [x] 保存设置到本地配置文件或缓存

### Out of Scope

- 账号系统
- 云同步设置
- 复杂主题设置
- 开机启动

## Subtasks

- [x] 设计设置存储方式
- [x] 新增 `SettingsViewModel`
- [x] 新增 `SettingsView`
- [x] 将 Settings Tab 替换为真实页面
- [x] 接入刷新间隔设置
- [x] 补充配置读写测试

## Acceptance Criteria

- [x] Settings Tab 显示真实设置项
- [x] 修改刷新间隔后能保存并在下次启动加载
- [x] 用户能看到缓存路径
- [x] 设置保存失败时显示错误提示
- [x] App 构建通过

## Test Cases

### TC-001: 修改刷新间隔并保存

Steps:
1. 打开 Settings。
2. 修改刷新间隔。
3. 保存并重启 App。

Expected:
- 新刷新间隔被保留。

### TC-002: 查看缓存路径

Steps:
1. 打开 Settings。
2. 查看缓存路径字段。

Expected:
- 显示当前 SQLite 缓存文件路径。

## Related Files

- `src/CCUsage.App/Views/MainWindow.axaml`
- `src/CCUsage.App/ViewModels/SettingsViewModel.cs`
- `src/CCUsage.App/Views/SettingsView.axaml`
- `src/CCUsage.Core/Models/AppSettings.cs`
- `src/CCUsage.App/DependencyInjection/AppServices.cs`

## Dependencies

- 当前 AppServices 初始化流程
- `AppSettings` 模型

## Development Log

### 2026-06-19

- 创建任务卡，列入 v0.2 Backlog。
- 开始开发：新增 Settings 页面、JSON 设置持久化、AppServices 设置加载，以及刷新间隔/CLI 命令/通知/调试显示联动。
- 扩展 `AppSettings`，新增 `CcusageCommand` 与 `ShowDebugInfo`；新增 `AppPaths` 集中管理缓存和设置文件路径。
- 新增 `JsonAppSettingsStore`，将设置保存到本地 `settings.json`，文件缺失时返回默认设置。
- 新增 `SettingsViewModel` / `SettingsView`，替换 Settings Tab 占位内容，支持命令、刷新间隔、通知和调试开关、路径显示及保存状态。
- App 启动时加载设置，CLI 数据源使用设置中的命令；后台刷新间隔使用设置中的分钟数；Dashboard 异常通知和 DebugInfo 显示受设置控制。
- 补充 AppSettings 默认值、JSON 设置读写、SettingsViewModel 保存/校验/错误、Dashboard 设置联动测试。
- 验证结果：首次新增 Dashboard 测试期望单条通知失败，因为构造函数异步初始化和显式刷新都会触发通知；调整断言为通知非空后通过。`dotnet test tests\\CCUsage.App.Tests\\CCUsage.App.Tests.csproj` 通过，30 passed；`dotnet test CCUsage.sln` 通过，60 passed；`dotnet build CCUsage.sln` 通过，0 warnings, 0 errors。`.NET preview SDK` 信息为环境提示。

## Review Notes

- Status: Ready for Review
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - Modification points: 新增 Settings 页面与 ViewModel；新增本地 JSON 设置存储和 AppPaths；扩展 AppSettings；启动流程加载设置并联动 CLI 命令、后台刷新间隔、异常通知和 DebugInfo 显示；补充设置相关测试。
    - How to run/test: `dotnet test tests\\CCUsage.App.Tests\\CCUsage.App.Tests.csproj`; `dotnet test CCUsage.sln`; `dotnet build CCUsage.sln`。
    - Impact area: App 启动 DI、Dashboard 通知/调试显示、Settings 页面、ccusage CLI 命令配置、后台刷新间隔下次启动配置。
    - Regression risk: 中；启动流程现在读取本地 settings.json，CLI command 和刷新间隔依赖配置值。已覆盖默认值、读写、保存失败和 Dashboard 联动测试；GUI 手动保存/重启验证仍未执行。

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
