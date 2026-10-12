---
id: Q-002
title: 旧视图清理与视觉验收打磨
type: refactor
priority: P2
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

v0.4 UI 批次收尾质量卡：移除旧视图与死代码，统一滚动条 / 行 hover / 焦点样式，空态复用 EmptyStateView，八个页面亮暗两套逐页目检并对照原型出差异清单。

## Background

- 批次内各卡各自目检，本卡是跨页一致性 pass：导航态、全局筛选态、空数据态、大数据量态统一定性。
- 差异清单原则：与原型一致的记还原完成；有意偏离的（如不做响应式断点、系统窗口边框）记录理由，不算缺陷。

## Acceptance Criteria

- [ ] 旧 DashboardView / ReportsView / ModelBreakdownView / TrendSpikeView 及死代码清除，导航无残留
- [ ] 8 页 × 亮暗两套全量目检，差异清单成文（接受的差异附理由）
- [ ] 键盘可达性：Tab 焦点顺序、focus ring 在 Semi 下正常，无不可达控件
- [ ] 空态 / 加载态统一（EmptyStateView 复用或改造）
- [ ] `dotnet test CCUsage.sln` 基线不回归

## Subtasks

- [ ] 旧视图与死代码清除
- [ ] 全量目检 + 差异清单
- [ ] 键盘可达性与焦点样式
- [ ] 空态/加载态统一

## Dependencies

- D-006, D-007, D-008, D-009, D-010, D-011, D-012, D-013, S-002（全部完成后执行）

## Test Cases

### TC-001: 全量回归

Steps:
1. `dotnet test CCUsage.sln`。
2. 按 D-002 离屏截图钩子思路对关键页面留证（可选）。

Expected:
- 测试基线不回归；目检无 P0/P1 差异。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: 全部 Views/
- How to run/verify: TC-001 + 逐页目检
- Results: （待执行）
