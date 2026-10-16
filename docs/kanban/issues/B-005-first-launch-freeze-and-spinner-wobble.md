---
id: B-005
title: 首启全窗冻结（采集扫描内联 UI 线程）+ 初始化菊花转动不匀（忽上忽下）
type: bug
priority: P1
size: M
status: done
created: 2026-10-16
updated: 2026-10-16
blocked: none
---

## Goal

修复用户报告的首次启动两个缺陷：① 全窗 UI 卡死一段时间、中途才恢复，恢复后初始化遮罩还残留一阵（菊花正常转一段时间才消失）；② 遮罩菊花旋转不均匀、视觉上忽上忽下。两者定性为 Bug 非设计，且都发生在 I-031 落地的首启路径上。

## Background

- 用户 2026-10-16 报告原话要点："刚开始时整个 UI 会卡死，过一段时间才活过来……不是初始化完成后活过来的，是到中间就活过来了，还会看到初始化界面转菊花转一段时间"；菊花"看起来不是均匀在转，忽上忽下"。
- **问题 ① 根因（dotnet-trace 采样线程栈实锤）**：`DashboardViewModel` 构造函数 fire-and-forget 的 `InitializeAsync` 在缓存为空（首次使用）时内联 `await RefreshAsync()`——本 VM 在 UI 线程构造（`App.OnFrameworkInitializationCompleted` 的 `Get<DashboardViewModel>()`），整条 19 源采集管道的每个 await 捕获 AvaloniaSynchronizationContext，续体逐段排回 UI 线程；trace 显示主线程近 30s 连续跑在 `DispatcherImpl.Signaled() → GetDailyUsageAsync → CodexJsonl 解析` 的同步续体级联里，消息循环无法泵。`BackgroundRefreshWiring.RunRefreshAsync` 的 `Task.Run` 包装只保护了启动首刷/定时器/重扫路径，构造自触发路径完全绕开它。
- **时间线逐段对齐**：卡死 = 构造路径整轮扫描在 UI 线程；"中途活过来" = 该轮结束、UI 线程释放，但遮罩由启动首刷轮维护（其轮次在 `_ingestionGate` 后排队等构造轮放锁），故遮罩不摘；"菊花再转一阵" = 启动首刷轮拿到锁跑增量轮（增量扫描器对未变更文件重发全量内存记录，不会误报 no data，已核实）。
- **问题 ② 根因**：菊花是 `◌`（U+25CC）字形旋转，`RenderTransformOrigin="50%,50%"` 是文本布局盒中心（Inter 行高 1.21em、基线 0.969em 决定），与字形墨迹中心不重合；解析 `Avalonia.Fonts.Inter.dll` 内嵌全部 6 个字重的 cmap 证实 **Inter 不覆盖 U+25CC**，字形由系统回退字体渲染、偏差更大——墨迹绕错误原点公转，视觉即"忽上忽下"。
- 诊断方法存档：临时 HOME（符号链接真实 agent 日志、Tokenmetry 数据目录隔离）模拟首启 + `sample` 抓原生栈 + `dotnet-trace`（dotnet-sampled-thread-time）拿托管级联证据；macOS 上 .NET 的 `LocalApplicationData` 走系统主目录不认 `HOME`，数据目录隔离需程序化 `PathsOverride`（仅诊断用，未入库）。

## Acceptance Criteria

- [x] 采集管道不可能内联在调用方（含 UI）线程执行——服务公共入口结构性防护
- [x] 构造时机不再触发扫描，首启只剩启动首刷一轮，遮罩寿命与真实扫描重合
- [x] 菊花旋转中心与墨迹中心恒等，转动均匀
- [x] 回归锁两条，均双向验证（修复后绿 / 临时还原旧码红）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿（5 项目 0 失败）

## Subtasks

- [x] 隔离复现 + 采样定位（见 Background 诊断方法）
- [x] `LocalIngestionDataSource.GetDailyUsageAsync` 公共入口 `Task.Run` 隔离（同 `CacheService` 成文约定）
- [x] 删除 `DashboardViewModel` 构造时机自动刷新（Q-002 headless 化后与启动首刷构成冗余双轮）
- [x] 菊花改矢量 `Ellipse` 虚线圆（Avalonia 12 `StrokeLineCap`；`StrokeDashCap` 已移除）
- [x] 回归锁 + 双向验证 + 全量测试

## Dependencies

- none（缺陷修复，挂在 I-031 引入的首启路径上）

## Test Cases

### TC-001: 管道不内联调用线程（回归锁，双向验证）

Steps:
1. 调用线程临时挂 `SynchronizationContext`（模拟 UI 线程形态）后调用 `GetDailyUsageAsync`；管道第一个源用桩适配器记录实际执行线程与当时的同步上下文。

Expected:
- 执行线程 ≠ 调用线程、上下文为 null（管道体经 `Task.Run` 移出）。

Results:
- PASS（修复后）；临时 stash 修复后同用例 FAILED（旧码管道内联调用线程）——锁有效。

### TC-002: 构造时机只热缓存（回归锁）

Steps:
1. 空 store + 假数据源构造 `DashboardViewModel`，断言数据源调用计数为 0。

Expected:
- `CallCount == 0`（首轮刷新归 `BeginStartupDataRefreshAsync`）。

Results:
- PASS（确定性，构造路径无后台触碰数据源的路径）。

### TC-003: 全量回归

Steps:
1. `dotnet test Tokenmetry.sln`。

Results:
- PASS。Core 159 / Reconciliation 49（19 skip 既有）/ Infrastructure 652 / App 125 / Headless 266，全绿（Headless 含真实 App 启动路径与 P0 刷新线回归锁）。

## Development Log

### 2026-10-16

- 建卡（用户报告 → 隔离复现 → 采样定罪，证据如 Background）。
- 修复 1：`LocalIngestionDataSource.GetDailyUsageAsync` 拆公共入口 + `GetDailyUsageCoreAsync`，入口经 `Task.Run` 整体移线程池——与 `CacheService` 公共入口的成文线程约定同构（"调用方可能在 UI 线程，而 Microsoft.Data.Sqlite 的 *Async API 同步完成"）。
- 修复 2：删除 `DashboardViewModel.InitializeAsync` 的 `if (!HasData) await RefreshAsync()`，构造只 `LoadFromCacheAsync`（CacheService 入口自身已 Task.Run，UI 线程构造安全）。该自动刷新源自 76d99b5 仪表盘有视图时代，Q-002 headless 化后真实应用里与 `BeginStartupDataRefreshAsync` 恒为冗余双轮，且是本冻结的入口；顺带消除遮罩尾差。两处测试的过时种子/注释（"构造函数的初始化刷新只在无缓存时触发"）一并清理。
- 修复 3：菊花改 `Ellipse.overlay-spinner` 虚线圆（`StrokeDashArray="0.01,3.2"` + `StrokeLineCap="Round"`，Avalonia 12 已移除 `StrokeDashCap`——元数据探测确认）；旋转中心 = 几何中心，转动均匀；颜色仍走 `AppLinkBrush` 令牌，尺寸节奏（36px/1.4s）不变。
- **未做真机窗口复验**：本机 GUI 进程当时起不来（AvaloniaNative `-6661` 渲染定时器初始化失败，对照实验确认与改动无关、系显示器休眠/锁定类环境问题，崩溃栈早于任何应用代码）；证据链由 TC-001 双向验证 + 全量套件（含 headless 真实启动路径）承担。
- 排查过不成立的候选：`SynchronizationContext` 流入 `Task.Run`（net10 实测不流入，wiring 包装有效）；models.dev 定价刷新（后台单飞、绝不阻塞）；启动期 settings/迁移 sync-over-async（AppServices 已 Task.Run 隔离且先于窗口存在）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、`src/Tokenmetry.App/ViewModels/DashboardViewModel.cs`、`src/Tokenmetry.App/Views/MainWindow.axaml`、`tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、`tests/Tokenmetry.App.Tests/ViewModels/DashboardViewModelTests.cs`
- How to run/verify: TC-001/TC-002 按名过滤单跑；TC-003 全量；真机体感 = 删除数据目录后首次启动，窗口出现即应可交互、菊花连续均匀旋转直至遮罩一次性摘除
- Results: 三条 TC 全 PASS；改动 5 文件 +111/−23，未触碰 wiring/单飞槽/采集语义
