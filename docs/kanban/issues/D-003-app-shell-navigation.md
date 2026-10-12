---
id: D-003
title: 三栏应用外壳 + 导航 + 全局筛选状态
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

按原型搭应用骨架：左侧导航（8 个固定视图）、顶部栏（全局时间范围 + Agent 筛选 + 数据状态 + 设置入口）、右侧最近会话栏、主内容区页面路由；全局筛选状态写入共享 VM 并跨页生效。替换现有 MainWindow 的 TabControl 结构。

## Background

- 设计基准：`docs/design/ccusage_analytics_prototype.html` 的 `.app/.topbar/.shell/.sidebar/.sessions-panel`。
- 2026-10-12 决定：窗口 chrome 沿用系统边框，不复刻原型左上角的 mac 红绿灯（那是网页模拟窗口的装饰；Avalonia 有 ExtendClientAreaToDecorationsHint 可选，不做首期）。
- 右侧栏可见性：原型为全页常驻（<1200px 隐藏，桌面版可忽略响应式）；实现时目检后定，倾向保持常驻。
- 原型响应式断点（@media 三档）桌面端不做，定一个合理最小窗口尺寸即可。

## Acceptance Criteria

- [ ] 三栏布局 + 8 项导航 + 页面路由（各页可为空占位）
- [ ] 顶部栏全局筛选（时间范围 7/30/90/全部 + Agent）写入共享筛选状态，切换时通知各页 VM
- [ ] 右栏最近会话 + 快速筛选骨架就位（真实数据接线在 D-008）
- [ ] 最小窗口尺寸下布局不破，导航键盘可达
- [ ] 明暗两套目检通过

## Subtasks

- [ ] ShellWindow/Grid 三栏 + 导航 VM + 路由
- [ ] 全局筛选状态服务（时间范围 + Agent）与通知机制
- [ ] 顶部栏与右栏骨架
- [ ] 旧 TabControl 移除、页面迁移占位

## Dependencies

- I-026, I-027

## Test Cases

### TC-001: 导航与筛选联动

Steps:
1. `dotnet run --project src/CCUsage.App`。
2. 点击 8 个导航项；切换时间范围与 Agent。

Expected:
- 页面切换正常；筛选状态变更各占位页可观测（临时日志或绑定显示）。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/Views/MainWindow.axaml`、`src/CCUsage.App/ViewModels/`
- How to run/verify: TC-001
- Results: （待执行）
