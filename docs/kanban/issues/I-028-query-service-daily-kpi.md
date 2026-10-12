---
id: I-028
title: UI 聚合查询服务 A：日趋势 + KPI + 区间对比
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

在 facts 存储之上提供面向 UI 的聚合查询服务：按日 token（输入/输出/缓存）与成本序列、KPI 汇总（总 token / 估算成本 / 会话数 / 缓存读取占比）及环比区间对比，支撑总览页与成本分析页。查询参数统一含时间范围与 Agent 过滤。

## Background

- 原型里每个数字都对应一个聚合口径；UI 层与解析层之间需要显式查询服务，ViewModel 不直接写 SQL（30k 行解析层已收口，本卡是 UI 与既有数据的接缝）。
- D-002 遗留决策点在本卡定案：日历窗口语义（空日补零 / 断线 / 仅画实际日）——本机 facts 实际 9 个数据日横跨 8 个月，稀疏日期的长直线有误导性。倾向：范围内空日补零并让图层可辨识；最终以实现结论为准，记录于 Development Log。
- 产品口径：未知成本（缺价模型）返回 null 且显式区分，绝不折算为 0 混入合计（对齐 I-024/I-025 收口时确立的 UnpricedModels 纪律）。

## Acceptance Criteria

- [ ] 日粒度序列查询：任意日期范围返回 in/out/cacheRead/cacheWrite/total/cost 按日聚合；日历窗口语义实现并记录结论
- [ ] KPI 汇总查询：区间汇总 + 上一周期对比（环比方向与百分比）
- [ ] 查询参数支持 Agent 过滤（为全局筛选联动准备）
- [ ] 未知成本口径：接口层显式区分 null 与 0，文档化返回契约
- [ ] 单元测试覆盖聚合正确性（fixture 数据与手写 SQL 对照）

## Subtasks

- [ ] 查询服务接口定义（Core/Infrastructure 归属斟酌）
- [ ] 日序列实现 + 日历窗口语义定案
- [ ] KPI 汇总 + 环比区间对比
- [ ] Agent 过滤参数贯通
- [ ] 单元测试

## Dependencies

- none（复用 I-002 存储、I-003 定价引擎）

## Test Cases

### TC-001: 聚合正确性

Steps:
1. 构造 fixture facts（已知逐日逐模型数据 + 一个缺价模型）。
2. 调用日序列与 KPI 查询，与手写 SQL 结果对照。

Expected:
- 逐日逐项一致；缺价模型成本为 null，KPI 中计入"未知"而非 0。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/`（查询服务）、`tests/CCUsage.Infrastructure.Tests/`
- How to run/verify: `dotnet test CCUsage.sln`
- Results: （待执行）
