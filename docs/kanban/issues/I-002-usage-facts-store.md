---
id: I-002
title: SQLite facts 存储 + migration runner
type: refactor
priority: P1
size: M
status: todo
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

- [ ] `schema_migrations` 表 + 顺序迁移 runner，启动时自动执行，迁移可重复执行不出错
- [ ] facts 表 upsert 幂等：重复采集同一数据不产生重复行
- [ ] 现有读取方（Dashboard / Reports / Model Breakdown）迁移到 facts 查询后行为不变，全部测试通过
- [ ] 旧 `ccusage.db` 有明确升级或重建策略，不崩溃、不丢配置

## Subtasks

- [ ] schema_migrations 表 + runner 实现
- [ ] usage_facts 表结构与 upsert 语句
- [ ] session_facts 表结构与 upsert 语句
- [ ] 现有读取方切换到 facts 查询
- [ ] 旧库迁移 / 重建策略
- [ ] 单元测试（迁移幂等、upsert 幂等、读写 roundtrip）

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

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/Cache/SqliteUsageCache.cs`、`tests/CCUsage.Infrastructure.Tests/Cache/`
- How to run/verify: `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj`
- Results: 待执行
