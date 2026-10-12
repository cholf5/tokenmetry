---
id: I-029
title: UI 聚合查询服务 B：维度聚合 + 会话明细 + 小时分布
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

提供 Agent / 模型 / 项目三个维度的聚合排行查询，会话明细列表查询（搜索、组合筛选、排序、分页），以及小时级分布查询。缺精确事件时间的来源按"未知"返回并在结果中可见，不伪造小时分布。

## Background

- 原型七个内容页里有四页（会话明细 / Agent / 模型 / 项目）+ 时间分析页全部吃这一层。
- 项目维度需要路径归一化为项目名（原型中 Project-A/AI-Project 等）；归一化歧义守卫对齐 I-025 的思路：歧义时宁可显示原始路径也不猜。
- session_facts 表结构 I-002 已就绪；会话去重口径（Agent + Session ID）与采集层既有语义保持一致。
- 小时级分布：部分源只有会话级粗粒度时间戳，对这类记录聚合到小时是伪造精度——按产品立场显式排除并标注。

## Acceptance Criteria

- [ ] Agent/模型/项目维度聚合：会话数、输入/输出/总 token、成本、占比；项目名经路径归一化
- [ ] 会话列表：标题/项目/模型/Agent 搜索 + 组合筛选 + 排序（token/成本/最近活动）+ 分页
- [ ] 小时级分布：仅纳入有时间精度的记录；无精度来源返回 unknown 计数，调用方可展示
- [ ] 未知成本口径与 I-028 一致（null ≠ 0）
- [ ] 单元测试覆盖各维度与"未知"口径

## Subtasks

- [ ] 三维度聚合查询
- [ ] 会话列表查询（搜索/筛选/排序/分页）
- [ ] 小时分布查询（含时间精度判定）
- [ ] 单元测试

## Dependencies

- none（复用 I-002 存储）

## Test Cases

### TC-001: 维度聚合与未知口径

Steps:
1. 构造 fixture：多 Agent/模型/项目 + 一个无分钟级时间戳的源 + 一个缺价模型。
2. 依次调用三类查询。

Expected:
- 聚合数值与手写 SQL 一致；unknown 时间源不出现在小时分布但计数可见；缺价成本为 null。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/`、`tests/CCUsage.Infrastructure.Tests/`
- How to run/verify: `dotnet test CCUsage.sln`
- Results: （待执行）
