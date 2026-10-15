# P-001: Windows 打包发布

## Metadata

- ID: P-001
- Title: Windows 打包发布
- Type: Release
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

为 ccusage-gui 生成可分发的 Windows 桌面应用构建产物，便于用户不通过源码运行。

## Background

当前 App 可通过 `dotnet run` 启动，但项目目标包含 Windows 可执行文件、托盘常驻和本地优先使用体验。需要建立发布流程。

## Scope

### In Scope

- [x] 定义 Windows publish 命令
- [x] 生成 Release 构建产物
- [ ] 验证启动、刷新、托盘基础行为
- [x] 编写发布说明草稿
- [x] 记录已知限制

### Out of Scope

- 安装程序
- 自动更新
- 代码签名
- GitHub Release 自动化

## Subtasks

- [x] 确定 `dotnet publish` 参数
- [x] 测试 self-contained 或 framework-dependent 方案
- [x] 检查图标和应用名称
- [ ] 验证发布产物可启动
- [x] 编写 `docs` 或 README 发布说明

## Acceptance Criteria

- [ ] 可以生成 Windows 可执行文件
- [ ] 发布产物启动后能显示 Dashboard
- [ ] 点击刷新能读取真实 ccusage 数据
- [ ] 托盘菜单基础可用
- [ ] 发布命令和使用说明被记录

## Test Cases

### TC-001: 发布产物启动

Steps:
1. 执行发布命令。
2. 打开发布目录中的可执行文件。

Expected:
- App 正常启动，不崩溃。

### TC-002: 发布产物刷新数据

Steps:
1. 启动发布产物。
2. 点击刷新数据。

Expected:
- 能调用本机 `ccusage` 并显示数据。

### TC-003: 托盘行为

Steps:
1. 启动 App。
2. 检查系统托盘图标和右键菜单。

Expected:
- 托盘图标存在。
- 基础菜单项可用。

## Related Files

- `src/Tokenmetry.App/Tokenmetry.App.csproj`
- `src/Tokenmetry.App/app.manifest`
- `src/Tokenmetry.App/Tray/TrayService.cs`
- `README.md`
- `docs/`

## Dependencies

- 建议 R-001 Reports 每日数据表格完成后进入 v0.1 发布准备
- Dashboard 真实数据链路已可用

## Development Log

### 2026-06-19

- 创建任务卡，列入 v0.1 Backlog。
- 开始开发，方案为完善 Windows extensionless `ccusage` 命令解析，验证 framework-dependent publish，并新增 Windows 发布说明文档。
- 完成 Windows extensionless 命令解析：支持 `PATHEXT`，PATH 查找，以及带目录路径的 sibling `.cmd/.exe/.bat` 查找。
- 补充 CLI runner 测试：PATH shim、显式扩展名命令、带路径 extensionless shim。
- 新增 `docs/release/windows-publish.md`，记录默认 publish 命令、输出路径、验证清单和已知限制。
- `dotnet test Tokenmetry.sln` 通过：Core 17、Infrastructure 12、App 5，共 34 个测试通过。
- `dotnet build Tokenmetry.sln` 通过：0 warning，0 error。环境提示正在使用 .NET 预览版 SDK（NETSDK1057）。
- `dotnet publish src\\Tokenmetry.App\\Tokenmetry.App.csproj -c Release -r win-x64 --self-contained false` 通过，生成 `src/Tokenmetry.App/bin/Release/net8.0/win-x64/publish/Tokenmetry.App.exe`。
- 未执行 GUI 手动启动、真实刷新和托盘验证；需要在可交互 Windows 桌面环境中完成。
- 移动到 In Review，等待模拟 review/QA 或人工验收。

## Review Notes

- Status: Not Reviewed
- Reviewer: ZCode (simulated)
- Notes:
  - PR-equivalent Review Summary:
    - 修改点：完善 Windows `ccusage` 命令 shim 解析，补充 CLI runner 测试，新增 Windows publish 文档，并验证 framework-dependent 发布产物生成。
    - 测试方式：`dotnet test Tokenmetry.sln`；构建方式：`dotnet build Tokenmetry.sln`；发布方式：`dotnet publish src\\Tokenmetry.App\\Tokenmetry.App.csproj -c Release -r win-x64 --self-contained false`。
    - 影响范围：Windows 上 CLI 调用可靠性、发布文档、Infrastructure 测试。
    - 回归风险：非 Windows 或已带扩展名命令保持原行为；真实 GUI 启动、刷新、托盘仍需可交互桌面环境手动验证。

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
