---
id: D-012
title: 时间分析页（小时分布 + 活跃日历热力图）
type: feature
priority: P2
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

时间分析页：按小时分布图（D-004 小时形态，本地时区）+ 活跃日历热力图（D-005）+ 时间维度观察项说明表。数据边界约束落地：无精确事件时间的源不进小时分布，页面显示「未知」占比说明。

## Background

- 设计基准：原型 `#page-time`。
- 原型 meta 文案「缺少精确事件时间的来源应显示未知，不应伪造小时级分布」对应产品立场，I-029 已按此返回 unknown 计数。
- 会话长度观察项（原型建议表第二条）依赖会话持续时间数据，若 session_facts 无此字段则该行显示为建议性文案，不做假数据。

## Acceptance Criteria

- [ ] 小时分布图接 I-029（本地时区），unknown 源显式排除并在页面标注占比
- [ ] 热力图按日 token 强度着色，hover 显示当日数值，接 I-028/029 日序列
- [ ] 观察项说明表三行内容与真实数据能力一致（不承诺没有的字段）
- [ ] 随全局筛选联动，明暗两套目检

## Subtasks

- [ ] TimeViewModel（小时分布 + 日强度）
- [ ] 图表接入与 unknown 标注
- [ ] 观察项表内容定稿

## Dependencies

- D-003, I-028, I-029, D-004, D-005

## Test Cases

### TC-001: unknown 标注

Steps:
1. fixture 含无分钟级时间戳的源。
2. 查看小时分布与标注。

Expected:
- 该源记录不出现在小时分布；页面可见「未知」占比说明。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/TimeAnalysisView.axaml`
- How to run/verify: TC-001 + 目检
- Results: （待执行）
