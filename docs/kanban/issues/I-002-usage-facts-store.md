---
id: I-002
title: SQLite facts 存储 + migration runner
type: refactor
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

把缓存层重构为 facts 存储：`usage_facts`（day × agent × model × project）+ `session_facts` + `schema_migrations`，配一个真正的顺序迁移 runner，替换现有 `SqliteUsageCache` 的整表覆盖模式，作为所有页面查询的唯一数据面。

## Background

- 固定视图（趋势图、Monthly/Session tab、TopN）需要比"每日一行"更细的可聚合数据。
- `../ccusage-tauri` 的 cache.rs 没有迁移框架、靠 PRAGMA 探列后 DROP 重建，这个坑不复制。

## Acceptance Criteria

- [x] `schema_migrations` 表 + 顺序迁移 runner，启动时自动执行，迁移可重复执行不出错
- [x] facts 表 upsert 幂等：重复采集同一数据不产生重复行
- [x] 现有读取方（Dashboard / Reports / Model Breakdown）迁移到 facts 查询后行为不变，全部测试通过
- [x] 旧 `ccusage.db` 有明确升级或重建策略，不崩溃、不丢配置

## Subtasks

- [x] schema_migrations 表 + runner 实现
- [x] usage_facts 表结构与 upsert 语句
- [x] session_facts 表结构与 upsert 语句
- [x] 现有读取方切换到 facts 查询
- [x] 旧库迁移 / 重建策略
- [x] 单元测试（迁移幂等、upsert 幂等、读写 roundtrip）

## Dependencies

- I-001（统一记录模型的形状决定 facts 表结构）

## Test Cases

### TC-001: 迁移幂等

Steps:
1. 对同一数据库连续启动两次（执行迁移两次）。

Expected:
- 第二次迁移为空操作，无错误，数据不变。

### TC-002: upsert 幂等

Steps:
1. 写入同一批 facts 两次。

Expected:
- 行数不变，数值为最新值。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（实现）

- 缓存层重构为 facts 存储，统一记录模型（I-001 产出）成为 facts 的输入形状：
  - `TokenPulse.Core/Models/UsageFact.cs`：usage_facts 行模型 + `UsageFactGrain` 粒度常量（day / agent / model / project / full）。
  - `TokenPulse.Core/Models/SessionFact.cs`：session_facts 行模型（session_id、agent、project、dominant model、时间范围、tokens、cost）。
  - `TokenPulse.Core/Abstractions/IUsageStore.cs`：facts 存储抽象（替代 IUsageCache，后者已删除）。
  - `TokenPulse.Core/Services/UsageFactsMapper.cs`：双向映射——UsageRecord 聚合为 full 粒度 facts 与 session facts；CLI 的 DailyUsageSummary 分解为 facts 并可无损重建；facts 按日聚合回页面消费的 DailyUsageSummary 形状（含 agent / model / project 三个 breakdown）。
  - `TokenPulse.Infrastructure/Cache/SqliteUsageStore.cs`：SQLite facts 存储 + 顺序迁移 runner（替代 SqliteUsageCache，后者已删除）。
- 关键决策：
  - **迁移 runner**：迁移在代码内注册为 `(Version, Name, ApplyAsync)` 有序列表（新增迁移 = 末尾追加一条，禁止修改已发布条目），`schema_migrations` 为 runner 自举表（不占版本号）；每个迁移在独立事务内应用并落账，失败回滚不落账、下次重试；`InitializeAsync` 幂等（快速路径 + 信号量防并发双重执行），公开方法内部也会确保迁移已执行。
  - **grain 列（与 tauri 版的关键差异）**：CLI 数据源只知道日合计、model 拆分和 agent 名单（无 token/成本拆分）、无项目拆分，统一记录聚合则是完整 day × agent × model × project。usage_facts 引入显式 `grain` 列并纳入主键（day, grain, agent, model, project），分解行按粒度路由、full 行自由聚合；不复制 tauri 靠 PRAGMA 探列补 schema 的做法，schema 变更一律走新版本迁移。
  - **upsert 语义（TC-002"数值为最新值"）**：`ON CONFLICT ... DO UPDATE SET 列 = excluded.列`，即写入方对该维度组合的值是权威值、最新写入生效（非累加）。这与增量扫描兼容：适配器每次返回全量累计记录集，重新聚合后整体覆盖写即幂等。累加语义留给未来的显式 API（本卡无此需求）。
  - **cost 列设计**：`cost_usd TEXT` 可空，固定文化十进制文本存储保证精确往返（SQLite 无 decimal 原生类型；Microsoft.Data.Sqlite 默认 decimal→TEXT 但读取端文化敏感，故显式转换）。聚合语义：已知 costUSD 之和，全部未知时为 null——I-003 定价引擎按行补缺口，partial cost 可区分。upsert 时 cost 同样"最新值生效"。
  - **日分桶时区**：统一记录按 `TimeZoneInfo.ConvertTime(timestamp, tz)` 的本地自然日分桶（默认系统时区，与 ccusage-tauri 默认一致）；mapper 提供可选 tz 参数，测试注入固定时区保持确定性，未来时区设置可直接接线。
  - **session_facts 主键**：`session_id` 单列（Claude 会话 id 为 UUID，实践中全局唯一；agent/project 为属性列）。若 I-004 / I-005 出现跨 agent 撞 id，届时追加迁移扩展主键——这正是迁移 runner 的用途。
  - **旧库策略：重建（推荐方案）**。迁移 1 直接 `DROP TABLE IF EXISTS dashboard_cache / daily_rows_cache` 后建 facts schema：旧库是 JSON 快照表且无迁移框架可循，数据可由采集（JSONL 扫描 / CLI 刷新）完整重建，丢缓存可接受；在冻结的历史迁移里内嵌 JSON 反序列化逻辑是footgun（映射语义演进后历史迁移结果不可复现）。用户设置在独立的 `settings.json`（JsonAppSettingsStore），不受影响。升级后首次启动 facts 为空，DashboardViewModel 的 `!HasData → 自动刷新` 路径自行重建。
  - **Dashboard 快照取消持久化**：DashboardSummary 改为 facts 读取时按需重建（BuildDashboard），不再有 dashboard_cache JSON 行——facts 是唯一数据面。行为差异：冷启动时的 Today* 值按"今天"重算（旧快照是上次刷新日口径，跨日后本就过期），页面字段与异常检测输入（数据源原始 rows）不变。
  - **已知展示层细微变化（记录在案）**：单日内 breakdown 列表顺序由"数据源顺序"变为按名称序（ordinal）。仅影响 Reports 的 AgentsText / ModelsText 拼接顺序；Model Breakdown 页自按 cost 排序，不受影响。
  - **全粒度行优先**：同一天同时存在 full 粒度行与分解粒度行（两种来源混写）时，读取以 full 粒度为准，避免混算重复计数；数据源切换时的旧 facts 清理/对账是 I-005 的职责（本卡不做，已留注释）。
- 现有读取方迁移：
  - `CacheService` 门面保持 `SaveDailyRowsAsync / GetDailyRowsAsync` 签名不变，内部改走"分解 → upsert / 读取 → 重建"；`SaveDashboardAsync / GetLatestDashboardAsync` 随快照取消而移除。
  - Reports / Model Breakdown ViewModel 零改动（同一 GetDailyRowsAsync 契约）；DashboardViewModel 仅最小改动（LoadFromCache 读 facts 重建 dashboard，Refresh 落 facts 后重建）。
  - AppServices 最小改动：注册 `SqliteUsageStore → IUsageStore`，启动时跑迁移；未接 IUsageDataSource 数据源选择（I-005）。
  - 未动 CCUsageCli/ 与 ClaudeCodeJsonl/ 适配器代码。
- 验证：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` 全绿 135 通过 / 3 跳过（跳过为既有 Windows 专属测试，未改动），净增 17 个测试。TC-001（迁移幂等：同库两次 Initialize，schema_migrations 单行、数据不变）与 TC-002（upsert 幂等：同批写两次行数不变、最新值生效）由单测覆盖并通过。

### 2026-10-11（独立审查修复）

- 独立审查结论 PASS with minor issues（无 P0/P1；grain 聚合、NULL 维度、cost TEXT、DST 分桶、旧库重建、迁移时序等经实验验证正确），修复 3 个 P2：
  - [P2] 日内 stale 分解行残留：CLI 分解写入从纯 upsert 改为"按日替换"（`ReplaceUsageFactsByDayAsync`：删除该日非 full 粒度行后写本批，同一事务；full 粒度行不动）。否则某日 model/agent 列表收缩时旧行残留，breakdown 双份计数（审查实验复现）。新增 store / CacheService 回归测试。
  - [P2] 跨进程并发首迁竞态：版本落账改 `INSERT OR IGNORE`，DDL 本身 IF NOT EXISTS 幂等，他方已迁移不再使启动失败。
  - [P2] "库比代码新"无检测：applied version 超出代码已知最大版本时抛 NotSupportedException（旧版应用打开新版库会因缺列静默崩溃，现明确报错提示升级）。
- 顺带修复审查发现的既有 bug：`DateRangeService.Today()` / `GetLastNDays` 用当前文化格式化日期，非格里历文化下与 facts 的 invariant 日期键失配，改为 invariant culture + ParseExact。
- 补充测试（审查指出的缺口）：混合 grain 同日读取 full 优先（mapper）、空串 model 维度 upsert 幂等（store）。
- 转记到 I-005 的前瞻项：JSONL 源接线后 `ToUsageFacts/ToSessionFacts` 不填 `ProjectDisplayName`（展示名恒空串）；数据源切换时旧 facts 的清理（本卡已有"full 优先"保底）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.Core/Models/UsageFact.cs`、`src/TokenPulse.Core/Models/SessionFact.cs`、`src/TokenPulse.Core/Abstractions/IUsageStore.cs`、`src/TokenPulse.Core/Services/UsageFactsMapper.cs`、`src/TokenPulse.Core/Services/CacheService.cs`、`src/TokenPulse.Infrastructure/Cache/SqliteUsageStore.cs`（替换 SqliteUsageCache）、`src/TokenPulse.App/DependencyInjection/AppServices.cs`、`src/TokenPulse.App/ViewModels/DashboardViewModel.cs`、`tests/TokenPulse.Infrastructure.Tests/Cache/SqliteUsageStoreTests.cs`、`tests/TokenPulse.Core.Tests/Services/UsageFactsMapperTests.cs`、`tests/TokenPulse.Core.Tests/Services/CacheServiceTests.cs`
- How to run/verify: `dotnet test tests/TokenPulse.Infrastructure.Tests/TokenPulse.Infrastructure.Tests.csproj`（全解决方案：`dotnet build TokenPulse.sln && dotnet test TokenPulse.sln`）
- Results: 2026-10-11 — build 0 警告 0 错误；全解决方案 139 通过 / 3 跳过（Windows 专属基线保持），净增 21 个测试（含审查修复后的 4 个回归测试）。TC-001 迁移幂等与 TC-002 upsert 幂等均由单测覆盖并通过；旧库重建路径（含 legacy 表 + 行数据）单测验证不崩溃、旧表清除、facts 可写可读；session_facts roundtrip（含 ephemeral 5m/1h 细分、时区偏移时间戳、cost null 与非 null）通过。审查复核：AC 逐条 ✓，混合粒度/空维度/收缩场景均有测试。
