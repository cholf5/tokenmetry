---
id: I-026
title: Semi.Avalonia 主题迁移（含 Avalonia 12.1.x 升级）
type: chore
priority: P1
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

把应用基础主题从 FluentTheme 换成 Semi.Avalonia，Avalonia 从 12.0.4 升到 12.1.x，保证现有全部视图在 Semi 下正常渲染、亮/暗/跟随系统切换不回归，为 UI 原型落地（v0.4 主体）打底。

## Background

- 2026-10-12 选型结论：Semi.Avalonia 最新 12.1.0.1（2026-07-31 发布）依赖 Avalonia ≥ 12.1.0，需小版本升级；备选 SukiUI 7.0.1（依赖 ≥ 12.0.3 可直用，但风格强势、偏离原型）与保留 Fluent 手写重写控件样式（保底路线）。设计蓝本气质与 Semi 同源（web 设计系统风格），故选 Semi。
- 设计基准：`docs/design/ccusage_analytics_prototype.html`（2026-10-12 自 ~/Downloads 收入仓库）。
- 迁移对象：`App.axaml` 根主题 + 现有 6 个视图；现有自建 ThemeDictionaries（App.axaml 中 10 组画刷 ×明暗）是否与 SemiToken 冲突需盘点。

## Acceptance Criteria

- [ ] Avalonia 全家桶升级到 12.1.x，`dotnet build` / `dotnet test` 基线不回归（242 通过 / 5 跳过量级）
- [ ] App.axaml 以 SemiTheme 替换 FluentTheme；现有全部视图可打开、无样式崩坏、无绑定错误日志
- [ ] 亮 / 暗 / 跟随系统三态切换照常工作（ThemeService → RequestedThemeVariant 与 SemiTheme 的桥接方式记录在 Development Log）
- [ ] Avalonia.Controls.DataGrid 包（12.1.x）引入，Semi 下有可用样式（一个演示表格即可）
- [ ] 现有 ThemeDictionaries 画刷与 Semi 的冲突清单成文（保留 / 改名 / 删除各有结论）

## Subtasks

- [ ] 升级 Avalonia 12.0.4 → 12.1.x，全量 build + test
- [ ] 引入 Semi.Avalonia + Irihi.Avalonia.Shared，替换 App.axaml 根主题
- [ ] 明暗切换三态验证，冲突画刷盘点清单
- [ ] DataGrid 包引入 + 最小演示验证

## Dependencies

- none

## Test Cases

### TC-001: 主题三态切换

Steps:
1. `dotnet run --project src/CCUsage.App`
2. 设置页依次切换 日间 / 夜间 / 跟随系统。

Expected:
- 三种状态全部视图渲染正常，无异常日志，重启后偏好保持。

### TC-002: 测试基线

Steps:
1. `dotnet test CCUsage.sln`

Expected:
- 通过数不低于迁移前基线（242 通过 / 5 跳过），无新增失败。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.App/CCUsage.App.csproj`、`src/CCUsage.App/App.axaml`
- How to run/verify: TC-001 / TC-002
- Results: （待执行）
