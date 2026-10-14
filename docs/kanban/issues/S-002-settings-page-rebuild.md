---
id: S-002
title: 数据源与设置页重构（原型版）
type: feature
priority: P2
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

按原型重做设置页：已识别数据源表（状态 / 最近扫描 / 记录数 / 重新扫描）+ 统计口径说明与开关 + 现有设置项（主题偏好等）并入新信息架构，替换旧 SettingsView。

## Background

- 设计基准：原型 `#page-settings`。
- 现有 SettingsView 承载主题设置（AppSettings.Theme，light/dark/system）——本卡不是重写持久化，而是信息架构与视觉重做，主题设置迁入。
- 数据源状态须来自采集层真实扫描状态（已连接 / 部分数据 / 未检出），不显示假状态；「部分数据」语义与各适配器口径对齐（如 Copilot CLI 的 shutdown 快照差分，见 I-008）。
- 原型「统计口径」区的开关项逐项评估：真实生效的落 AppSettings，纯说明性文案的明确标注为说明，不做假开关。

## Acceptance Criteria

- [x] 数据源表显示真实状态与记录数，重新扫描按钮触发采集层并可观测（headless 自动化验证 + 真实服务冒烟；真机点按目检待下轮人肉验证，见 Verification）
- [x] 统计口径区逐项定案（5 项全部评估为说明样式——无真实生效的用户开关可接，落 AppSettings 即假开关；逐项结论见 Dev Log）
- [x] 主题设置迁入且三态切换即时预览、保存持久化（ThemeService 路径不动，既有测试全绿）
- [x] 保存/扫描操作有 toast 反馈；旧 SettingsView 完全替换（原文件原地重写，git 历史保留旧版）
- [x] 明暗两套目检通过（headless 离屏截图 docs/design/settings-{light,dark}.png 人工核对通过；真机明暗切换目检待下轮）

## Subtasks

- [x] SettingsViewModel 扩展（数据源状态 + 扫描触发）——原地重写：数据源表行重建（INavigationAware.OnNavigatedTo 重进刷新）+ RescanCommand（复用 BackgroundRefreshService 互斥）+ 主题/既有设置项保留
- [x] 数据源表 + 重新扫描（per-source 重扫不支持，定案为全局「重新扫描（全部源）」按钮 + meta 说明，见 Dev Log）
- [x] 口径区开关定案与实现（5 项全说明样式：4 始终生效 + 1 未提供，无假开关）
- [x] 主题设置迁移 + 旧视图移除（SettingsView.axaml 原地重写；CcusageCommand/RefreshMinutes/异常通知/调试信息/本地路径一并迁入新 IA）

## Dependencies

- D-003, I-026（Semi 控件可用）

## Test Cases

### TC-001: 重新扫描（定案演进出全局语义）

Steps:
1. 点击数据源面板「重新扫描（全部源）」（采集管道为单信号量串行全源管道，不支持 per-source 重扫——按钮语义如实，见 Dev Log 定案）。

Expected:
- 按钮进入扫描态（禁用 + "扫描中…"，防重复触发：与定时器刷新共用容器内同一 BackgroundRefreshService 的单飞行槽，已有刷新在跑时并入该轮等待其真实结果，不重复跑也不空返回），完成后记录数与状态刷新，有 toast「扫描完成」；刷新失败如实报「扫描失败」并在数据源面板提示"数据可能非本轮"（P2-1）。

Automated: SettingsViewModelTests.RescanAsync_*（扫描态生命周期 / 行重建 / toast / 互斥等待）、BackgroundRefreshServiceTests（互斥不重叠 + 排队不早退）、SettingsPageTests.Rescan_button_*（headless 全链路）。

### TC-002: 设置持久化

Steps:
1. 切换主题并保存，重启应用。

Expected:
- 偏好保持。

Automated: SettingsViewModelTests.SaveAsync_PersistsThemePreferenceAndAppliesIt + JsonAppSettingsStoreTests roundtrip（AppSettings.Theme 路径未动）；ThemeService 三态测试全绿。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

实现完成（原型版重构，旧 SettingsView 原地替换）。关键定案：

**1. 页面 VM 生命周期（定案：保持 DI 单例 + OnNavigatedTo 钩子）**
- SettingsViewModel 保持单例（`AppServices` 注册 `AddSingleton`，`MainWindowViewModel` 持有同一实例，外壳 `CreatePage` 复用）——不破坏主题选中态跨导航保持与既有持久化行为。
- 数据源状态需要"重进刷新"：新增 `INavigationAware.OnNavigatedTo()`（App.ViewModels 小接口），外壳 `OnSelectedNavigationChanged` 在新页面就位后调用；设置页实现之，每次进入从扫描注册表重建数据源表。随导航新建的页面（构造即查询）无需实现。启动后首轮管道完成前，行如实显示"● 尚未扫描 / —"，不伪造。

**2. 数据源表各列真实来源表（全部采集层真实可查）**

| 列 | 来源 | 依据 |
|---|---|---|
| 数据源（行清单） | `LocalIngestionDataSource` 管道组合（19 个适配器）构造时注册进 `IngestionScanRegistry`，键 = 各适配器解析器 `AgentName` 常量（= facts agent 维度出账名；pi-agent 按存储作用域出账无单一名，源名取适配器名；Antigravity 无常量取出账名同值字面量） | "从采集层注册表来，别手写清单"——设置页零手写，行序 = 管道扫描序 |
| 状态 | 该源上次 `ScanAsync` 返回的 `JsonlScanDiagnostics.Status`：Ok→已连接 / Partial→部分数据 / NoData→未检出 | 适配器诊断真实产出（JSONL 损坏行→Partial；Copilot CLI shutdown 快照差分缺口→Partial，I-008 口径对齐） |
| 最近扫描 | 该源 `ScanAsync` 完成时刻（管道串行、逐源各自记录；当日 HH:mm / 跨日 MM-dd HH:mm） | 真实完成点时间戳 |
| 记录数 | 该源上次扫描返回 `Records.Count`（消息级去重后的累计统一 usage 记录数；增量扫描跨刷新累计） | 真实扫描计数，千分位展示 |
| 操作 | 无 per-source 操作列；面板级「重新扫描（全部源）」按钮（见定案 3） | 诚实降级 |

- 基建：新增 `TokenPulse.Core/Services/IngestionScanRegistry.cs`（`IIngestionScanReader` 读取面 + 写入实现，锁内不可变快照；写入只发生在管道内），`LocalIngestionDataSource` 可选注入（未传不报告，既有构造点不受影响），扫描循环化（顺序与拼接序与原实现逐位等价，行为保持）。启动时 DI 即物化采集源（注册表清单不依赖 DashboardViewModel 解析时机）。

**3. per-source vs 全局重扫（定案：全局重扫）**
- 采集管道是单信号量串行全源管道（JSONL 增量记账非线程安全），适配器不作为独立可寻址单元暴露；per-source 重扫需要新管道入口与独立 upsert 语义，超出本卡范围且收益低（增量扫描下"单源重扫"与全源重扫成本同阶）。
- 落地：面板头「重新扫描（全部源）」按钮 → `BackgroundRefreshService.TriggerRefreshAsync`（设置页与定时器刷新共用同一实例），互斥闸由 Interlocked CAS 改为 `SemaphoreSlim(1,1)`（同样满足审查 P2 的原子占位防双跑，且并发触发排队串行不重叠、后到者不空返回，可观测一次完整刷新的完成点）。按钮防重复触发（IsScanning 禁用），完成 → 行重建 + toast「扫描完成」；meta 常驻说明"状态与记录数来自上次采集扫描 · 记录数为增量累计值"。
  - ⚠️ 本条中"RegisterInstance late map / 兜底实例 / 设置页与定时器共用同一实例"的表述**被同日审查修复轮推翻**（late map 不拦截嵌套构造注入，设置页实际拿到兜底实例，构成 P0 假成功）——以审查修复轮条目为准。
- 既有采集线不动：`App.axaml.cs` 的刷新动作仍 = DashboardViewModel.RefreshCommand（facts 直解析）+ 外壳时间戳回写，仅创建/注册顺序调整（先于主窗 VM 解析，因设置页 VM 依赖同一实例）。

**4. 统计口径区逐项结论（原型 5 个复选项 → 全部说明样式，无假开关）**

| 原型项 | 结论 | 理由 |
|---|---|---|
| 按 Agent + Session ID 去重 | 说明（始终生效） | 消息级去重 + session_facts 按 agent+session_id 幂等 upsert，管道内建行为，无用户开关可接 |
| 区分输入、输出、缓存读取与缓存写入 | 说明（始终生效） | UsageRecord / facts 固定四向拆分，无开关 |
| 未知模型价格显示为「未知」 | 说明（始终生效） | I-024/I-025 未知成本纪律（HasUnknownCost / UnpricedModels），无开关 |
| 将不同 Agent 的疑似同一任务自动合并（谨慎启用） | 说明（未提供） | 未实现跨 Agent 合并；做开关即假开关（红线） |
| 所有原始记录默认仅在本机处理 | 说明（始终生效） | 架构事实：适配器直读本机日志/账本，联网仅拉价格表，原始记录不出本机（AppSettings.OfflinePricing 为存量未接线字段，本卡不顺手接线） |

- 结论：无新增 AppSettings 字段（AC "生效开关落 AppSettings" 的可行集为空——所有"开关"要么恒真要么未实现，记录于上表）。

**5. 信息架构（原型两面板 + 既有设置项迁入）**
- 页头（数据源与设置 + 原型副标题，ShellPages 同源）→ 已识别的数据源（wide panel）→ 统计口径 → 外观与通用（主题三态 ComboBox 即时预览 + 保存持久化；刷新间隔 / ccusage CLI 命令 / 异常通知 / 调试信息——旧视图功能项全部保留，不丢功能）→ 本地路径（只读）→ 保存设置（primary）。
- 样式：新增 SharedStyles `status-ok/partial/nodata/pending` 四个状态类（颜色复用既有 AppAccentGreen/Orange/Muted 令牌，无新令牌、DesignTokenTests 零改动）；表格复用 table-th/td/num，徽标复用 chip。
- Toast：保存成功「设置已保存」/ 失败「保存失败：…」、扫描完成「扫描完成」均经 IToastService（I-027）。

**6. 遗留风险**
- per-source 重扫未做（有意降级，见定案 3）：若未来要求单源重扫，需给管道加按源入口与独立 upsert/清理语义。
- 一次性触发与定时器并发时会排队串行（SemaphoreSlim 语义）：用户在长扫描中点重扫会等两轮才返回——UI 有"扫描中…"提示，可接受。
- headless 测试宿主不执行 App 桌面生命周期块：`BackgroundRefreshService` 在 DI 有兜底空转实例（触发时 stderr 提示），真实应用先 RegisterInstance 永不命中；若未来调整 App 启动顺序需保持"先注册后解析主窗 VM"。（⚠️ 已被同日审查修复轮取代：late map + 兜底实例删除，容器内正常装配 + 接线类未接线如实失败。）
- 真机（非 headless）明暗目检与 TC-001/TC-002 人肉点按待下轮验证（自动化已覆盖同等语义）。
- 设计基准原型中"已识别的数据源"为 5 源示例清单；真实管道为 19 源（表随注册表行数自然增长）。明暗截图为 7 源 stub 快照；19 源真实长表经 headless DI 冒烟断言覆盖（行数/渲染/文本），未做长表离屏截图留档。
- 真实数据基线（审查修复轮留档，2026-10-13 本机一轮真实扫描）：Codex 2,931 / ZCode 27,137 / DSH 5,811 已连接；Claude Code、Copilot CLI 等其余 16 源未检出（本机无对应日志目录，如实显示）。

### 2026-10-13（审查修复轮：P0 ×1 + P2 ×2）

**P0（必修）真机设置页「重新扫描」触发的是兜底空转实例，不是真实采集线——已修复**
- 根因（审查确认）：`SettingsViewModel` 由 MSP DI 容器嵌套构造注入（`Get<MainWindowViewModel>` 链内解析），其 `BackgroundRefreshService` 参数被容器解析为 `AppServices` 里注册的兜底 stderr 空转实例；`RegisterInstance` 的 late map 只拦截顶层 `Get<T>` 调用，**嵌套构造注入不经过它**。全仓无任何 `Get<BackgroundRefreshService>()` 顶层调用方，故 late map 形同虚设。后果：真机点重扫 → 兜底瞬间空转 → 行"刷新"（数据不变）→ toast「扫描完成」——假成功，违反卡内红线。测试全绿是因为全部手工 new VM、DI 冒烟不点 RescanCommand。
- 修复（正常容器装配，late map 与兜底实例**全部删除**）：`BackgroundRefreshService` 在 `AppServices.Initialize` 内以容器单例注册（嵌套注入与顶层 Get 解析到同一实例）；刷新动作经新 `BackgroundRefreshWiring`（App/Services/BackgroundRefreshService.cs）执行——dashboard 与外壳 VM 在 App 启动序列就绪后 `Attach(dashboard, shell)`，未接线时**如实抛错**（服务层捕获转为失败结果）。处置理由：late map 无其余调用方且其"只拦顶层"的语义本身就是 P0 根源，删除；兜底空转实例正是假成功载体，由"未接线如实失败"取代（headless 宿主不执行启动序列，其下重扫得到明确「扫描失败」而非假成功）。I-028 启动首刷同步受益：`App.axaml.cs` 现经 `Get<BackgroundRefreshService>()` 取容器实例，与设置页重扫、定时器刷新三路汇聚同一实例。附带收敛了 `App.axaml.cs` 的 `shell!` 赋值顺序依赖（恢复原始解析顺序，Attach 在两者就绪后）。
- 回归锁：新增 headless `SettingsPageTests.Di_settings_rescan_drives_real_dashboard_refresh_line`——真实 DI 解析 SettingsViewModel → 执行 RescanCommand → 断言走通真实采集线（`scan finished` + 外壳"最近更新"时间戳被回写 + 19 源全部有快照）。该测试第一跑即抓到修复初版接线漏掉时间戳回写的缺陷（`_shell` 未使用），已修正。

**P2-1 重扫 toast 无条件成功——已修复**
- 根因：`TriggerRefreshAsync` 内部吞异常 + `DashboardViewModel.RefreshAsync` 也吞 → 设置页 catch 是死代码，适配器抛错时 UI 仍「扫描完成」。
- 修复：刷新路径可报告成败——`TriggerRefreshAsync` 返回 `Task<bool>`（动作正常完成且 `dashboard.Status != "error"` 为 true；"no data" 是成功的扫描；动作抛异常/上报失败为 false）。设置页据此区分 toast「扫描完成 / 扫描失败：数据表保持最近一次成功快照」；失败时数据源面板 meta 给出行级提示「上次扫描未完成——下表为最近一次成功快照，可能非本轮数据」（保持到下次成功扫描；期间行数据未变，提示始终为真）。**定时器/启动首刷路径保持既有静默失败语义不变**（fire-and-forget 不观察返回值）。

**P2-2 SemaphoreSlim 排队无合并——已修复**
- 根因：慢刷新（>15min 间隔）下每个定时器 tick 排队一整轮补跑、无界堆积。
- 修复：互斥由 `SemaphoreSlim` 改为**单飞行槽 + 触发合并**——CAS 抢占者成为本轮执行者，后到触发并入在跑轮次等待其真实结果（不追加新轮、不空返回）；轮次完成即释放槽，之后的触发开启新一轮。同一时刻至多一排在跑（审查 P2"原子占位防双跑"语义继续满足）。
- **合并语义与 IsScanning 的相容性定案**：等待中的触发对设置页 IsScanning 仍算"进行中"（其调用尚未返回，按钮保持禁用直到拿到并入轮的真实结果）；用户点重扫时若定时器刷新在跑，则等那一轮完成后展示其结果 + toast，不重复跑第二遍。测试锁定：`ConcurrentTriggers_CoalesceIntoSingleRun`（5 并发触发只跑一轮）、`MergedTrigger_WaitsForInFlightRound_DoesNotReturnEarly`、`NewTriggerAfterRoundCompletes_StartsFreshRound`、`ActionThrowing_ReportsFailureInsteadOfPropagating`、`ActionReportingError_PropagatesFalseResultToMergedWaiters`。

**P2-3 视觉验证如实化**：明暗截图为 7 源 stub 快照，19 源真实长表仅 headless DI 冒烟断言覆盖（未留档长表截图）；真实数据基线数字（Codex 2,931 / ZCode 27,137 / DSH 5,811）已记录于遗留风险节。

**环境记录**：审查修复后真机 GUI 启动冒烟因本 shell 会话 WindowServer 受限无法完成（Avalonia Native RenderTimer 初始化 -6661，崩溃点在平台引导、任何 App 代码执行前）；已做 HEAD 对照实验（stash 全部改动后原版同样崩溃）确认与本卡改动无关；桌面块接线由 headless P0 回归锁覆盖。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/Views/SettingsView.axaml`（原地重构）、`src/TokenPulse.App/ViewModels/SettingsViewModel.cs`（重写）、`src/TokenPulse.App/ViewModels/INavigationAware.cs`、`src/TokenPulse.Core/Services/IngestionScanRegistry.cs`、`src/TokenPulse.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（逐源快照报告）、`src/TokenPulse.App/Services/BackgroundRefreshService.cs`（单飞行槽 + 触发合并 + BackgroundRefreshWiring 接线）、`src/TokenPulse.App/DependencyInjection/AppServices.cs`、`src/TokenPulse.App/App.axaml.cs`、`src/TokenPulse.App/Styles/SharedStyles.axaml`（status-* 类）、`src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（OnNavigatedTo 钩子）
- How to run/verify: TC-001 / TC-002（见上）；截图 `TOKENPULSE_SETTINGS_PREVIEW_DIR=docs/design dotnet test ...SettingsPageTests`；P0 回归锁 `dotnet test ...SettingsPageTests.Di_settings_rescan`
- Results:
  - 测试全绿无回归（含审查修复轮）：Core 103 通过；Infrastructure 640 通过 / 3 跳过；App.Tests 57 通过（SettingsViewModelTests 重写 + 重扫成败/未接线诚实失败/合并语义 + BackgroundRefreshServiceTests 6 例）；Headless 181 通过 / 0 失败（含 SettingsPageTests 7 例：**P0 回归锁 Di_settings_rescan_drives_real_dashboard_refresh_line** + 数据源表绑定与状态着色 / 口径区无假开关 / 重扫扫描态全链路 / 主题三态迁移 / 真实 DI 冒烟 19 源 / 明暗截图）；Reconciliation 49 通过 / 19 跳过。合计 **1029 通过 / 22 跳过 / 0 失败 / 0 警告**（S-002 前基线 1003/22/0）。
  - P0 修复验证：`Di_settings_rescan_drives_real_dashboard_refresh_line` 以真实 DI 解析设置页 VM → RescanCommand → 断言 `scan finished` + 外壳「最近更新」时间戳被本轮回写 + 19 源全部有快照（走通 容器内同一 BackgroundRefreshService → BackgroundRefreshWiring → DashboardViewModel.RefreshCommand 真实采集线）；未接线路径经单测 `RescanAsync_WhenServiceUnwired_FailsHonestlyInsteadOfFakeSuccess` 锁定（如实「扫描失败」，无假成功）。
  - 真实服务基线（2026-10-13 本机一轮真实扫描，headless 验证）：19/19 源有快照——Codex 2,931 / ZCode 27,137 / DSH 5,811 已连接，Claude Code、Copilot CLI 等其余 16 源未检出（本机无对应日志目录，如实显示）；facts 落库正常。
  - 明暗截图留档：`docs/design/settings-light.png` / `docs/design/settings-dark.png`（headless 离屏渲染，7 源 stub 快照，人工核对通过；19 源长表由 DI 冒烟断言覆盖）。
  - 真机 GUI 启动冒烟：本 shell 会话 WindowServer 受限无法完成（Avalonia Native RenderTimer -6661，平台引导期崩溃、App 代码未执行）；HEAD 对照实验（stash 后原版同样崩溃）确认与本卡无关；桌面块接线由 P0 回归锁覆盖。
  - 真机点按（TC-001 按钮点按 / TC-002 重启持久化 / 明暗切换目检）：自动化已覆盖同等语义，人肉目检待下轮。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论"有问题需修复"——1×P0 + 3×P2；管道等价性逐位核实、Registry 线程安全与 19 键唯一性 grep 实证、互斥语义测试锁定、INavigationAware 接线、数据源表真实性与口径区逐项对照全部通过。修复（原开发 agent 完成，PM 验收）：
1. **P0 真实 App 重扫假成功**：SettingsViewModel 由容器嵌套构造注入，BackgroundRefreshService 被解析为 late-map 之外的兜底空转实例（RegisterInstance 只拦顶层 Get<T>）——点重扫无扫描发生却弹「扫描完成」。修复：撤 late map 与兜底实例，BackgroundRefreshService 改正常容器单例（设置页重扫/定时器/启动首刷三路汇聚同一实例），新增 BackgroundRefreshWiring 未接线如实抛错；P0 回归锁 headless 用真实 DI 解析设置页 VM → RescanCommand → 断言真实扫描完成 + 外壳「最近更新」回写 + 19 源快照（该测试首跑即抓出修复初版漏回写时间戳的缺陷并已修正）。
2. P2 重扫 toast 无条件成功 → TriggerRefreshAsync 返回 Task<bool>，toast 区分完成/失败，失败保留最近一次成功快照 + 行级提示；定时器/首刷静默语义不变。
3. P2 信号量排队无合并（慢刷新下定时器 tick 无界堆积）→ 单飞行槽 + 触发合并（后到者并入在跑轮等待真实结果），5 并发只跑一轮等语义有测试锁定。
4. P2 Dev Log 截图表述改为如实（7 源 stub；19 源长表以 DI 冒烟 + 基线数字 Codex 2,931 / ZCode 27,137 / DSH 5,811 留档）。
5. 环境备注：本会话 shell 的 WindowServer 受限无法真机 GUI 冒烟（Avalonia Native RenderTimer -6661，HEAD 对照实验证实与本卡无关）——真机点按目检留待用户/下轮，桌面接线由 P0 回归锁覆盖。PM 验收：build 0 警告 + 全量 1029 通过 / 22 跳过 / 0 失败。
