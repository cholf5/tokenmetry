---
id: D-002
title: ScottPlot.Avalonia 集成 spike
type: chore
priority: P2
size: S
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

验证 ScottPlot.Avalonia 5.1.59 在 Avalonia 12 + net10.0 上可用：用真实数据渲染一张 30 天成本趋势图，摸清缩放 / 悬停 / tooltip 的实现成本，把选型结论写回卡片，为 v0.4 正式集成扫清风险。

## Background

- 选型依据见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`：ScottPlot.Avalonia ≥ 5.1.59 依赖 Avalonia ≥ 12.0.0 且支持 net10.0，NuGet 已核实，但需要在本项目实测。
- 这是 spike 卡：产出是结论与原型，不是可发布功能；正式集成的卡在 v0.4 规划时创建。

## Acceptance Criteria

- [ ] 安装 ScottPlot.Avalonia 并在临时页面用真实 facts 数据渲染 30 天成本趋势图
- [ ] 缩放 / 平移可用
- [ ] tooltip / crosshair 有可运行原型，实现成本有结论
- [ ] 结论（可行性、风险、tooltip 方案、代码规模估计）记录在 Development Log

## Subtasks

- [ ] 添加 ScottPlot.Avalonia 包引用
- [ ] 临时 spike 页面 + 真实数据趋势图
- [ ] 缩放 / 平移与 tooltip 原型
- [ ] 写结论并决定 v0.4 是否按原计划推进

## Dependencies

- none（不依赖 I-00x，可先用现有 daily rows 数据）

## Test Cases

### TC-001: spike 运行

Steps:
1. `dotnet run --project src/CCUsage.App` 打开 spike 页面。

Expected:
- 趋势图渲染真实数据，交互可用，无崩溃。

<!-- spike 以人工验证为准，无需自动化测试 -->

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/CCUsage.App.csproj`、spike 临时页面
- How to run/verify: `dotnet run --project src/CCUsage.App`
- Results: 待执行
