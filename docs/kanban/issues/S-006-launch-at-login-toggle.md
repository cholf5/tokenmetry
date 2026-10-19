---
id: S-006
title: 开机自启动开关（默认关）+ --autostart 静默启动
type: feature
priority: P2
size: S
status: done
created: 2026-10-19
updated: 2026-10-19
blocked: none
---

## Goal

设置页新增「开机自启动」开关（**默认关**）：勾选后随用户登录自动启动应用并**静默常驻托盘**（不弹主窗），符合 A-0 托盘常驻语义；注销即时生效，改完即落盘（全自动保存语义）。

## Background

- 用户提案（2026-10-19 会话）：「Settings 界面加一个『开机自启动』的选项，默认关闭」。
- 托盘常驻应用（A-0）的自启动若每次登录都弹主窗会很扰人——注册内容统一带 `--autostart` 参数，App 启动序列识别后跳过 `mainWindow.Show()`，进程常驻托盘（弹层 / 托盘菜单随时唤回）。这是本功能的应有语义，不是可选项。
- 三平台零额外依赖的原生机制：Windows = HKCU `Run` 键、macOS = `~/Library/LaunchAgents` plist（RunAtLoad）、Linux = XDG autostart `.desktop`。发布目标是 Windows（macOS/Linux 为开发机顺带可用）。
- 归类设置页：S-004 拆分立场——「不影响数字的偏好」归设置页（同 S-005 关窗行为先例）。

## Acceptance Criteria

- [x] 设置页「外观与通用」面板新增「开机自启动」开关 + 提示文案（默认关），改完即落盘
- [x] 勾选即 OS 注册（当前可执行文件 + `--autostart`），OS 失败回滚开关显示且**不落盘**（settings 与 OS 状态一致优先）；注销同理
- [x] `--autostart` 启动：主窗创建但不 Show，进程常驻托盘；托盘菜单 / 弹层 / 「打开统计」可唤回主窗
- [x] 启动对账：settings 为源，仅 enabled 时重写 OS 注册（应用更新换可执行路径后自启动不失效）；失败只记诊断不阻塞启动；默认关路径零 OS 写入，headless 测试宿主（TOKENMETRY_HEADLESS=1）绝不触碰真实 OS 注册状态
- [x] i18n：`CheckAutoStartOnBoot/Hint` 走 Translations（zh 源 + en 覆盖）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] Core：`AppSettings.AutoStartOnBoot`（默认 false）+ 消费者注释
- [x] App/Services：`IAutoStartManager` + `AutoStartManager`（同文件接口+实现，同 ThemeService 范式；三平台 SetEnabled + 内部纯构造函数 `BuildWindowsRunValue` / `BuildLaunchAgentPlist` / `BuildDesktopEntry`）
- [x] AppServices.cs：DI 注册
- [x] SettingsViewModel：`AutoStartOnBoot` 开关（先 OS 注册后落盘，失败回滚 = 字段直写避免重入回调）
- [x] SettingsView.axaml：开关 + 提示（XML 注释不得含 `--`，精确参数名只在 C# 文档）
- [x] App.axaml.cs：`--autostart` 静默启动 + 启动对账（enabled 且非 headless 才执行）
- [x] TrayService：`IsHeadlessTestHost` private → internal（App 启动对账复用同一标记）
- [x] 测试：App.Tests 开关注册/回滚 + 管理器纯构造内容形状；Headless 设置页开关联动

## Dependencies

- none（寄生在 A-0 托盘常驻与 S-004 设置页结构上）

## Test Cases

### TC-001: 开关注册与回滚（App.Tests）

Steps: fake 管理器注入 VM → 置 true / 置 false 断言调用序列与落盘；注入抛异常的 fake → 置 true 断言开关回滚、settings 不变、SaveCalls=0。

Expected: 成功路径 OS 调用顺序与 settings/落盘一致；失败路径不落盘、开关显示回滚。

### TC-002: 注册内容形状（App.Tests，纯构造函数）

Steps: 三平台构造函数喂带空格 / `&` 的路径。

Expected: Run 值 = 带引号路径 + `--autostart`；plist 含 XML 声明头、DOCTYPE、Label、ProgramArguments、RunAtLoad，XML 转义正确；desktop 入口 Exec 带引号。

### TC-003: 设置页开关联动（headless）

Steps: SettingsView + VM（fake 管理器）→ 找到自启动 CheckBox（按 Translations 串定位，语言无关）→ 勾选。

Expected: 默认关；勾选后 VM 状态置真且管理器收到 true。

### 真机人工核验（headless 能力外）

- Windows：勾选后 `reg query HKCU\...\Run /v Tokenmetry` 可见 `"exe" --autostart`；重启/注销登录后应用静默启动（无主窗）、托盘在；关闭开关后键值消失。
- macOS：勾选后 `~/Library/LaunchAgents/com.tokenmetry.app.plist` 生成且 `plutil -lint` 通过；下次登录静默常驻；关闭开关后 plist 删除。
- 注意：plist 只写不 load（避免本进程运行中再拉起一个实例），生效点为下次登录。

## Development Log

### 2026-10-19（建卡即实施）

- 用户会话一句话提案，设计沿 S-005 套路：Core 设置字段 + App 层开关（全自动保存）+ 消费者行为。OS 注册实现放 App/Services（应用级 OS 集成，同 ThemeService/ToastService 先例），不进 Core/Infrastructure——它不采数据、不碰 facts。
- 启动对账只在 enabled 时重写注册：修「应用更新换路径后自启动失效」这一真实失效模式；同时默认关路径零 OS 写入，headless 测试宿主（真实 App 启动序列会跑）绝不触碰真实用户 OS 状态（PathsOverride 同红线）。对账失败只记 stderr 不阻塞启动。
- `--autostart` 静默启动 = 不 Show 主窗但照常创建（DataContext / 托盘 / 弹层都挂它）：弹层自持独立窗口（StatusPopoverController），托盘菜单 ShowWindow 唤回，OnLastWindowClose 不会被误触（无窗可关）。
- VM 失败回滚用字段直写 + 手动 PropertyChanged（避免在 OnChanged 回调里 Set 属性重入回调 → 失败分支可能递归）。
- 踩坑一：XAML 注释含 `--autostart` 炸 XML（comment cannot contain '--'），XAML 注释改说「见 AutoStartManager.LaunchArg」，精确参数名只留在 C# 文档与代码。
- 踩坑二：`XDocument.ToString()` 省略 XML 声明（launchd 期望的 plist 头）且带缩进——显式拼回 Declaration；元素间换行为缩进格式，launchd/plutil 均接受。
- CA1416 平台守卫（TreatWarningsAsErrors）：`OperatingSystem.IsWindows()` 守卫 + 私有平台实现挂 `[SupportedOSPlatform(...)]`，Registry API 在 net10.0 非 Windows TFM 下编译通过（ref pack 携带）、运行时 PNSE 不可达。
- 有意不做：macOS `launchctl load`（会立即拉起第二实例）；SMAppService（macOS 13+ 需 objc interop，LaunchAgent plist 足够且同样进系统登录项管理界面）；单实例互斥（现状本来就没有，登录自启动不与手动双开问题叠加，需则立卡）；托盘菜单加同款开关（设置页唯一出口，opinionated）。
- 测试结果：`dotnet build Tokenmetry.sln` 0 error；全量 `dotnet test Tokenmetry.sln` = Passed 1364 / Failed 0（App.Tests 161→166，Headless 330→331；22 skip 为既有平台相关跳过）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Core/Models/AppSettings.cs`、`src/Tokenmetry.App/Services/AutoStartManager.cs`、`DependencyInjection/AppServices.cs`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsView.axaml`、`App.axaml.cs`、`Tray/TrayService.cs`、`Localization/Translations.cs`；测试 `tests/Tokenmetry.App.Tests/ViewModels/SettingsViewModelTests.cs`、`tests/Tokenmetry.App.Tests/Services/AutoStartManagerTests.cs`、`tests/Tokenmetry.App.Headless.Tests/Shell/SettingsPageTests.cs`
- How to run/verify: 自动 = TC-001~TC-003；真机 = 卡片「真机人工核验」段（Windows 注册表查询 / macOS plutil -lint + 下次登录静默常驻）
- Results: 完成（2026-10-19）。TC-001~TC-003 全绿；全量 1364 Passed / 0 Failed。
