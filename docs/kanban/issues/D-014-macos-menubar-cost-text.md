---
id: D-014
title: 状态栏直显今日用量（模板图标 → 动态文字位图，不走 NSStatusItem interop）
type: feature
priority: P2
size: M
status: todo
created: 2026-10-17
updated: 2026-10-17
blocked: none
---

## Goal

macOS 菜单栏（及 Windows 托盘可选）图标直接渲染「42.4K · $12.46」级紧凑用量文本，无需展开菜单——补齐 A-0「托盘常驻显示今日成本」（pivot §5）的另一半，也是状态栏快捷信息窗（D-015/D-016）的入口价值前提。

## Background

- **规格缺口**（2026-10-17 用户会话核实）：macOS 菜单栏图标没有 tooltip——`UpdateToolTip` 写的 tooltip 在 Mac 上渲染不出，菜单首行 `_costItem` 是 Mac 上成本的唯一出口（`TrayServiceTests.cs:57` 注释同口径）。Windows 靠 tooltip 算是兑现了规格，Mac 只兑现了一半：状态栏本身不显示任何信息，想看成本必须展开菜单。
- **路径评估**（2026-10-17 会话结论）：
  - ① 纯托管 hack（本卡方案）：成本文字动态渲染成单色位图 → `WindowIcon`，改数字时重绘。不动 Avalonia 外部 API，成本低；难点是 22pt 状态栏高度下小数字的字形与 DPI（1x/2x）调优，做不好会显糊。
  - ② 原生 interop（iStat 菜单类做法）：NSStatusItem + NSTextField，需 unsafe/objc interop 层，工程量大，**本阶段不做**；仅当位图方案画质不可接受且证据充分时再议。
- 可复用路径：`TrayService.CreateIcon()` 已有运行时换图入口；`Dashboard.PropertyChanged → UpdateToolTip(BuildCostText(...))` 已有成本联动管线（KPI 更新即回写），重绘挂钩同一信号。
- 约束：保持 template 单色语义（macOS template icon 是 alpha 蒙版位图，跟随明暗菜单栏自动反色——文字位图同样必须 alpha-only，禁用彩字）；紧凑数字格式（$12.50 → $12.5 / $1.2k）宽度才放得下；无数据 / 扫描中态有诚实降级字形。
- **原型补充规格**（2026-10-17 状态栏路线定稿，原型 `docs/design/tokenmetry_statusbar_prototype.html` 底部 tray chip）：状态栏文本 = 紧凑 tokens + 成本（`42.4K · $12.46`）；Warn 态可切警告字形（`⚠ 38.2K/min`，可选）。Windows 托盘 tooltip 已有成本展示，Windows 侧是否同步上文字位图在本卡实施时决策（macOS 优先——tooltip 在 Mac 渲染不出）。

## Acceptance Criteria

- [ ] macOS 状态栏直接渲染当日成本文本（紧凑格式），KPI 更新即重绘，无需展开菜单
- [ ] 明暗菜单栏两态 + 1x/2x DPI 截图留档目检不糊（真机截图，headless 渲染不采信）
- [ ] 无数据 / 异常态有诚实降级（"—" 或回落纯模板图标），不留陈旧数字
- [ ] Windows 行为零变化（仍 tooltip + 菜单首行；`CreateIcon` 的 Windows 资产路径不动）
- [ ] 若位图方案画质不可接受，卡内记录截图证据与结论后决策：调优 or 升级 interop 方案 or 关卡明示放弃（不静默搁置）

## Subtasks

- [ ] TrayService：macOS 分支动态位图渲染（文字 → alpha-only 位图 → WindowIcon），挂 Dashboard KPI 信号重绘
- [ ] 紧凑成本格式化（与图表 `FormatCompactTokens` 口径对齐或独立 TrayCostCompactFormat 键，i18n 走 Translations）
- [ ] 无数据 / 扫描中 / 刷新失败三态降级字形
- [ ] 真机 macOS 截图验证（明暗 × 1x/2x）+ 现有 TrayServiceTests 回归

## Dependencies

- none（寄生在 TrayService 既有换图路径与成本联动管线上）

## Test Cases

### TC-001: 成本更新触发重绘（headless 可测部分）

Steps: 构造 TrayService（注入 dashboard fixture）→ 改 `TodayCostUsd` → 断言图标被替换（非原始模板资产）与菜单首行文案同步。

### TC-002: 真机画质验收

Steps: macOS 真机明暗两态截图（1x/2x）→ 目检字形锐度与反色正确性。

Expected: 数字可读不糊；暗色菜单栏白字、亮色黑字（template 反色）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Development Log

### 2026-10-17

- 建卡（S-005 实施会话顺带定案的规格缺口）：Mac 状态栏成本唯一出口是菜单首行，状态栏本身零信息；方案选定纯托管位图渲染，interop 方案明确不在本阶段。
- 2026-10-17（晚）状态栏路线定稿：升 P2，标题从「macOS 状态栏直显今日成本」放宽为「状态栏直显今日用量」，并入原型 tray chip 规格（紧凑 tokens + 成本，Warn 态可选）；popover 路线拆 D-015（内容层）/ D-016（壳层），本卡是其入口前提。
