---
id: S-002
title: 数据源与设置页重构（原型版）
type: feature
priority: P2
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

按原型重做设置页：已识别数据源表（状态 / 最近扫描 / 记录数 / 重新扫描）+ 统计口径说明与开关 + 现有设置项（主题偏好等）并入新信息架构，替换旧 SettingsView。

## Background

- 设计基准：原型 `#page-settings`。
- 现有 SettingsView 承载主题设置（AppSettings.Theme，light/dark/system）——本卡不是重写持久化，而是信息架构与视觉重做，主题设置迁入。
- 数据源状态须来自采集层真实扫描状态（已连接 / 部分数据 / 未检出），不显示假状态；「部分数据」语义与各适配器口径对齐（如 Copilot CLI 的 shutdown 快照差分，见 I-008）。
- 原型「统计口径」区的开关项逐项评估：真实生效的落 AppSettings，纯说明性文案的明确标注为说明，不做假开关。

## Acceptance Criteria

- [ ] 数据源表显示真实状态与记录数，重新扫描按钮触发采集层并可观测
- [ ] 统计口径区逐项定案（生效开关落 AppSettings；说明项有说明样式）
- [ ] 主题设置迁入且三态切换即时预览、保存持久化
- [ ] 保存/扫描操作有 toast 反馈；旧 SettingsView 完全替换
- [ ] 明暗两套目检通过

## Subtasks

- [ ] SettingsViewModel 扩展（数据源状态 + 扫描触发）
- [ ] 数据源表 + 重新扫描
- [ ] 口径区开关定案与实现
- [ ] 主题设置迁移 + 旧视图移除

## Dependencies

- D-003, I-026（Semi 控件可用）

## Test Cases

### TC-001: 重新扫描

Steps:
1. 点击某数据源「重新扫描」。

Expected:
- 按钮进入扫描态，完成后记录数与状态刷新，有 toast。

### TC-002: 设置持久化

Steps:
1. 切换主题并保存，重启应用。

Expected:
- 偏好保持。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/SettingsView.axaml`（重构）
- How to run/verify: TC-001 / TC-002
- Results: （待执行）
