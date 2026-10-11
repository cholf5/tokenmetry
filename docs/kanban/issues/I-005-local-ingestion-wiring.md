---
id: I-005
title: 本地采集数据源接线，CLI 降级为对账工具
type: refactor
priority: P1
size: S
status: todo
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

- [ ] 刷新流程走本地采集，无 ccusage CLI、断网环境下 App 能出真实数据
- [ ] CLI 适配器保留，仅用于对账测试与手动导入，Settings/文档中说明其新角色
- [ ] 空态文案更新：不再把安装 ccusage 描述为必选条件
- [ ] 全部测试通过，后台刷新调度行为不变

## Subtasks

- [ ] LocalIngestionDataSource 实现 IUsageDataSource（读 facts）
- [ ] DI 与 BackgroundRefreshService 接线
- [ ] Settings 与空态文案更新
- [ ] 回归：全量测试 + 本机冒烟

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
1. `dotnet test CCUsage.sln`。

Expected:
- 全部通过，无新增失败。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/CCUsageCli/`、`src/CCUsage.App/DependencyInjection/AppServices.cs`、`src/CCUsage.App/Services/BackgroundRefreshService.cs`
- How to run/verify: `dotnet test CCUsage.sln` + 手动冒烟（断网 + 无 CLI）
- Results: 待执行
