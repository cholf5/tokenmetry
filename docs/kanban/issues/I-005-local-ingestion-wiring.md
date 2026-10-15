---
id: I-005
title: 本地采集数据源接线，CLI 降级为对账工具
type: refactor
priority: P1
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

新增基于本地采集的 `IUsageDataSource` 实现（直读 JSONL → facts → 查询），接入 `BackgroundRefreshService` 与全部页面；`CCUsageCliDataSource` 降级为对账 / 手动导入路径，不再是默认数据源。

## Background

- 这是 v0.3 的收口卡：I-001 ~ I-004 的能力在此合并成用户可感知的行为变化。
- `docs/handoff/260618-ccusage-gui-avalonia.md` 时代的"必须装 ccusage"前提自此解除。

## Acceptance Criteria

- [x] 刷新流程走本地采集，无 ccusage CLI、断网环境下 App 能出真实数据
- [x] CLI 适配器保留，仅用于对账测试与手动导入，Settings/文档中说明其新角色
- [x] 空态文案更新：不再把安装 ccusage 描述为必选条件
- [x] 全部测试通过，后台刷新调度行为不变

## Subtasks

- [x] LocalIngestionDataSource 实现 IUsageDataSource（读 facts）
- [x] DI 与 BackgroundRefreshService 接线
- [x] Settings 与空态文案更新
- [x] 回归：全量测试 + 本机冒烟
- [x] 数据源切换时清理旧 facts（I-002 转记：混源日的旧分解行 / full 行清理；JSONL 源的 `ToUsageFacts/ToSessionFacts` 需填 `ProjectDisplayName`，否则项目展示名恒为空串）

## Dependencies

- I-002（facts 查询）、I-004（两个适配器就绪后再切默认数据源）

## Test Cases

### TC-001: 无 CLI 出数

Steps:
1. 临时移除 PATH 中的 ccusage。
2. 运行 App 并刷新。

Expected:
- Dashboard / Reports 显示真实本地数据，无 CLI 相关错误。

### TC-002: 回归

Steps:
1. `dotnet test Tokenmetry.sln`。

Expected:
- 全部通过，无新增失败。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（实现收口）

**LocalIngestionDataSource**（`src/Tokenmetry.Infrastructure/LocalIngestion/`）：管道为 双适配器增量扫描 → PricingEngine 补价 → `ToUsageFacts` / `ToSessionFacts` → `IUsageStore` upsert → 删除旧 CLI 分解行 → 从 store 读回 `ToDailySummaries`。
- 补价：对每条记录调 `PriceRecord`——自带 costUSD 的记录走 LogCost 短路原值返回，无成本的按 token 明细计算；每次刷新 `ClearUnpricedModels` 后全量重算，未命中清单始终反映最新一轮。
- 读回走 facts 存储而非内存聚合：facts 是页面查询的唯一数据面，读回即验证页面将看到的形状（探针确认读回与刷新输出一致）。
- 空扫描（0 记录）不动存储：保留既有数据（含旧 CLI 行），避免日志目录缺失时清掉用户仅存的历史。

**生命周期决策**：两个适配器注册为单例——`IncrementalJsonlScanner` 持有文件 mtime/大小记账与消息级去重状态，单例才能跨刷新增量解析（真实数据实测：首次 43 ms，第二次 5 ms）；每次新建会退化为全量重扫，浪费 I-001/I-004 的增量设计。适配器非线程安全，而手动刷新与后台定时刷新可能并发进入（`AsyncRelayCommand` 不防重入），`LocalIngestionDataSource` 用 `SemaphoreSlim` 串行化整个管道。

**DI 接线**（`AppServices`）：`LocalIngestionDataSource` 成为默认 `IUsageDataSource`；`ModelsDevPricingClient` 单例同时以 `IRemotePricingSource` 供给定价引擎（用户覆盖来自 `settings.PricingOverrides`）；`CCUsageCliDataSource` 保留注册（构造时仍读 `CcusageCommand` 设置）但不再是默认。不设"切回 CLI"的运行时开关：CLI 行是分解粒度、与本地采集的 full 粒度读取语义不同，混源开关会造成"切回 CLI 后覆盖了 full 行覆盖不了"的半更新状态；对账由 `Tokenmetry.Reconciliation.Tests` 承担，手动导入等有 UI 入口时再接线。

**models.dev 定价刷新**：挂在 `LocalIngestionDataSource.GetDailyUsageAsync` 入口（手动刷新与后台 tick 的共同路径），fire-and-forget 调 `RefreshIfDueAsync`——到期才发请求、单飞、失败静默、20s 超时，绝不阻塞主刷新；本次采集先用内置快照/上次成功数据计价，远程数据到位后下一次刷新自然校正。

**数据源切换清理**（Subtask 5 前半）：`IUsageStore` 新增 `DeleteDecompositionFactsAsync`（Sqlite 实现为 `DELETE FROM usage_facts WHERE grain <> 'full'`），本地采集每次成功写入后调用——只清 CLI 源的分解粒度行（day/agent/model/project grain），full 粒度行与 session_facts 不受影响，幂等且无需引入版本标记。边界：切换前旧 CLI 缓存里"日志已删、仅存缓存"的日期会随之清掉（同一批日志本地采集会重建 full 行，真正的损失仅限日志已物理删除的日子）；用户设置在独立 settings.json，不受影响。配套调整：`DashboardViewModel` 不再对自持持久化的数据源重复 `SaveDailyRowsAsync`（`IUsageDataSource` 新增默认接口属性 `PersistsFacts`，本地采集源为 true），避免把 full 行有损重分解回 CLI 形状的分解行、并抵消清理。

**ProjectDisplayName**（Subtask 5 后半）：`ToUsageFacts` / `ToSessionFacts` 填充 `ProjectDisplayName` = ProjectPath 的最后一个非空路径段（`/` 与 `\` 均作分隔）。蓝本核对结论：ccusage-tauri claude.rs 不单独计算展示名，project_name 即日志目录推导的 project_path，前端原样展示——Claude 源的 project_path 恒为 `projects/` 下单个目录段，取末段与蓝本值一致；Codex 源的 ProjectPath 是会话 cwd 绝对路径，蓝本展示完整 cwd，此处取更短的末段（项目目录名），为有意的偏差。

**顺带修复（Bugs 表 B-001）**：`Split('/', '\\', options)` 在 C# 中重载解析为 `Split(char separator, int count, StringSplitOptions options)`——第二个分隔符 `'\\'` 被隐式转成 int（92）当作最大段数。`ClaudeCodeJsonlLineParser.ExtractSessionParts` 的既有写法因此从未按反斜杠拆分（Windows 路径会话推导会坏），`UsageFactsMapper` 新代码同样踩中（测试当场暴露）。两处改为显式 `char[]`，补 Windows 路径回归测试。

**文案**：空态改为"应用直接读取本地会话日志，无需安装 ccusage CLI / 请确认本机存在 Claude Code 或 Codex 的会话日志"；Settings 的 ccusage 卡片标题改为"ccusage CLI（对账 / 手动导入）"并说明默认数据源为本地会话日志。

**测试**：新增 14 个——`LocalIngestionDataSourceTests`（临时目录 fixtures + 内存 store：补价与聚合、LogCost 保留、重复刷新幂等、旧分解行清理且 full 行保留、空扫描不动存储、并发串行化、PersistsFacts）、SqliteUsageStore 清理接口、mapper 展示名 3 个、Windows 路径回归、ViewModel 持久化归属 2 个；4 个测试 fake store 补齐新接口方法。

**本机冒烟（TC-001 探针等价验证，探针不进仓库）**：一次性控制台探针（/tmp，引用 src 工程，临时库）等价执行 扫描 → 补价 → facts → 读回：本机无 Claude 日志（roots 为空、0 记录）；Codex 走 `~/.codex/sessions` + `archived_sessions` 自动探测，19 个文件解析出 2931 条记录（status=Ok），刷新 #1 43 ms 产出 9 个自然日的汇总（全期 $101.39 / 3.67 亿 tokens，项目展示名如 rathena、SabberStone 均为 cwd 末段）；预置的旧 CLI 分解行被清除（仅剩 11 条 full 行 + 11 条 session facts）；未命中价格模型为空（内置快照全覆盖）；刷新 #2 5 ms（增量），结果与读回一致。管道全程不 spawn 任何进程（ccusage 是否在 PATH 无关，本机实际存在 /opt/homebrew/bin/ccusage）。models.dev 远程刷新在本机网络下 20s 超时（响应体传输慢），失败静默、快照兜底，主刷新未被阻塞——与设计一致。Avalonia 窗口人工冒烟受限，以探针等价覆盖 TC-001。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B-001 | medium | `Split('/', '\\', options)` 重载解析为 `Split(char, int, StringSplitOptions)`：第二个分隔符被隐式转成 int（92）当最大段数，反斜杠从未参与拆分。`ClaudeCodeJsonlLineParser.ExtractSessionParts`（既有代码，Windows 路径会话/项目推导失效）与 `UsageFactsMapper` 新增展示名推导（测试暴露）均踩中。 | 已修复 | 两处改为显式 `new[] { '/', '\\' }` char[]，补 Windows 路径回归测试（`Extracts_session_parts_from_windows_backslash_paths`）。 |
| B-002 | medium | 补价写回共享记录实例导致价格锁死：适配器增量扫描跨刷新复用同一批 UsageRecord，`record.CostUsd = 补价` 使二次刷新被 LogCost 短路，用户覆盖 / models.dev 更新对已计价记录永不生效（审查实证：override 改价后刷新仍返回旧价）。 | 已修复 | 补价结果进侧表（`Dictionary<UsageRecord, decimal?>` 按记录引用），`ToUsageFacts/ToSessionFacts` 增加补价侧表参数；不写回记录。回归测试 `GetDailyUsage_RepricesRecordsWhenOverridesChange`（改覆盖价后二次刷新价格更新）。附带修正：诊断 MissingCostRecords 不再被首轮补价清零、补价不再被误报为 LogCost。 |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、`src/Tokenmetry.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs`、`src/Tokenmetry.App/DependencyInjection/AppServices.cs`、`src/Tokenmetry.App/Services/BackgroundRefreshService.cs`、`src/Tokenmetry.App/ViewModels/DashboardViewModel.cs`、`src/Tokenmetry.Core/Abstractions/IUsageStore.cs`、`src/Tokenmetry.Core/Services/UsageFactsMapper.cs`、`src/Tokenmetry.Infrastructure/Cache/SqliteUsageStore.cs`、`src/Tokenmetry.App/Views/EmptyStateView.axaml`、`src/Tokenmetry.App/Views/SettingsView.axaml`
- How to run/verify: `dotnet test Tokenmetry.sln`；TC-001 以一次性控制台探针等价验证（扫描 → 补价 → facts → 读回，真实 Codex 日志 + 临时库，探针不进仓库，见 Development Log）
- Results:
  - `dotnet build Tokenmetry.sln`：0 警告 0 错误。
  - `dotnet test Tokenmetry.sln`：241 通过 / 5 跳过（基线 227/5，新增 14 个测试全绿）；`TOKENMETRY_RECONCILE_REAL=1` 时 242 通过 / 4 跳过（Codex 真实数据对账跑通，Claude 因本机无日志跳过）。
  - TC-001 探针：无 CLI 依赖（管道不 spawn 进程）、无 Claude 日志环境下，真实 Codex 日志（19 文件 / 2931 记录）产出 9 个自然日汇总；重复刷新幂等（facts 数不变）；旧 CLI 分解行清除、full 行保留；读回与刷新输出一致；快照定价 0 未命中。
  - 后台刷新调度未改动（`BackgroundRefreshService` 定时器语义与刷新命令不变）。

### 2026-10-11（独立审查修复）

- 独立审查结论 PASS with minor issues（端到端管线、PersistsFacts 设计、清理语义、并发、fire-and-forget 安全、B-001 修复、冒烟数字全部复核通过），修复 **B-002（P1）价格锁死**：补价写回适配器缓存的共享记录实例，二次刷新被 LogCost 短路锁死旧价，与开发日志"远程数据到位后自然校正"的声称矛盾（对已计价记录不成立）。改为补价侧表 + mapper 重载（见 Bugs 表 B-002）。
- 记录的已知取舍（审查确认，不修）：
  - 选择性删除日志文件后，对应日期的陈旧 full 行会保留显示（清理只删分解行；与旧 CLI 路径"缺席日不清理"语义一致，空扫描不动存储是显式设计）。
  - Settings 的 CLI 命令设置当前无可见效果（对账由 Reconciliation.Tests 承担、手动导入 UI 未接线），文案保留描述新角色。
