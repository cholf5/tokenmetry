---
id: D-008
title: 会话明细页 + 右栏最近会话联动
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

会话明细页：搜索框 + Agent 筛选 + 排序下拉，明细表（会话/Agent/模型/Token/估算成本/最后活动），行数虚拟化；右栏「最近会话」与快速筛选接真实数据，并与页面及全局筛选联动。

## Background

- 设计基准：原型 `#page-sessions` 与右栏 `.sessions-panel`。
- 吸收 v0.4 原规划的「Reports Session tab」；Monthly 维度不进本卡（记入 Parking Lot）。
- 会话点击行为：原型是 toast 占位；正式版打开会话详情（下钻弹窗或详情页，实现时定案记录）。

## Acceptance Criteria

- [ ] 搜索 + Agent 筛选 + 排序全接 I-029，组合筛选结果正确
- [ ] 大列表滚动流畅（≥1 万行 fixture 虚拟化验证）
- [ ] 右栏最近会话、Agent 复选组、模型/项目下拉与全局筛选联动生效
- [ ] 未知成本显示「未知」；计数条（显示 X / Y 条）正确
- [ ] 明暗两套目检通过

## Subtasks

- [ ] SessionsViewModel（搜索防抖 + 筛选 + 排序）
- [ ] 明细表 DataGrid 行模板 + 虚拟化
- [ ] 右栏最近会话 + 快速筛选接线
- [ ] 会话点击下钻方案定案与实现

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 组合筛选

Steps:
1. 构造 ≥1 万行 fixture 会话。
2. 组合搜索词 + Agent + 排序各维度操作。

Expected:
- 结果与查询服务单测口径一致；滚动帧率无可感知卡顿。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/SessionsView.axaml`、`ViewModels/`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
