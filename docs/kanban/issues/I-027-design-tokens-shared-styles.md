---
id: I-027
title: 设计令牌与共享样式（原型色板 / 卡片 / 系列色 / mini 进度条 / toast）
type: feature
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

把 UI 原型的视觉规范沉淀为共享资源与样式——色板（明暗两套）、图表系列色、卡片/面板样式、圆角间距字号规范、表格迷你进度条、轻量 toast——供八个页面统一复用，避免各页各写各的样式。

## Background

- 原型观感的核心是 CSS 变量组（`--bg/--panel/--panel2/--line/--muted/--text` + 7 个系列色）与统一的卡片语言（11px 圆角、1px `#24344d` 描边、145deg 渐变、KPI 卡四色图标底）。这些在 Avalonia 里对应画刷与 Style 资源，与主题库无关，是视觉还原度的决定因素之一。
- 设计基准：`docs/design/ccusage_analytics_prototype.html` `<style>` 段 `:root` 与 `.kpi/.panel/.chip/.mini-track` 等选择器。
- 系列色是图表语义色（Agent/模型维度着色），不随明暗翻转，放普通资源不放 ThemeDictionaries。

## Acceptance Criteria

- [ ] 原型色板落进 ThemeDictionaries：Dark = 原型原值逐项对应，Light 补一套协调值；画刷命名规范成文（写入本卡或 docs）
- [ ] 7 个系列色定义为共享资源，供图表、表格圆点、环形图共用
- [ ] 共享样式：Card/Panel（圆角描边渐变）、页面标题区、链接按钮、chip、mini 进度条（track + fill）
- [ ] 轻量 toast 反馈控件（或采用 Semi 通知组件，结论与理由记录在 Development Log）
- [ ] 在临时页面目检明暗两套效果（截图或运行说明记录）

## Subtasks

- [ ] 色板逐项映射 + Light 侧补值
- [ ] 共享样式资源文件（Styles/*.axaml）
- [ ] mini 进度条控件
- [ ] toast 方案选型与实现
- [ ] 临时页面目检明暗两套

## Dependencies

- I-026

## Test Cases

### TC-001: 样式目检

Steps:
1. `dotnet run --project src/CCUsage.App` 打开样式预览页。
2. 切换明暗主题。

Expected:
- 明暗两套下卡片、文字、描边层次与原型一致，无硬编码色残留于视图。

<!-- 样式类工作以人工目检为准 -->

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/App.axaml`、`src/CCUsage.App/Styles/`（新建）
- How to run/verify: TC-001
- Results: （待执行）
