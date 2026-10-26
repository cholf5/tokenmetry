---
id: D-023
title: Windows 托盘双击唤出主窗——单击瞄弹层 / 双击进主窗的分层（时间窗拦截，不改单击语义）
type: feature
priority: P3
size: S
status: todo
created: 2026-10-26
updated: 2026-10-26
blocked: 用户决策
---

## Goal

Windows 上双击托盘图标直接唤出主窗（单击弹层 Toggle 语义完全不动），形成「单击瞄一眼 / 双击看全貌」的 OneDrive 式分层。

## Background

- 用户提案："双击托盘图标显示主窗口，合理吗？符合用户习惯吗？" → brainstorm 会话评估拍板。
- 习惯裁决：双击开主程序是 Windows 老牌托盘惯例（OneDrive/Dropbox 式：单击 flyout / 双击主窗）；macOS 菜单栏无双击习惯且 interop 左键不经过 `OnTrayClicked`，Linux 各 DE 的 Clicked 触发行为不一——**平台范围仅 Windows**。
- 技术事实：Avalonia `TrayIcon` 只有 `Clicked`、无 DoubleClick 事件，Win32 双击表现为两次 `Clicked`（WM_LBUTTONUP）——时间窗判定是唯一路径，单击零新增延迟（不做延迟单击判定，为次要动作拖慢主交互被明确否决）。
- 设计全文：`docs/plans/2026-10-26-tray-double-click-design.md`（该文件从未入库，悬空引用——见 Development Log 回退记录）。

## Acceptance Criteria

- [ ] `TrayService.OnTrayClicked` 前置双击拦截：两次 Clicked 间隔 ≤ 判定窗（默认 `GetDoubleClickTime()`，尊重系统设置）且 Windows 门控开 → 第二击不 Toggle，收弹层（仅可见时）+ `ShowWindow()`（与右键菜单「打开主窗」同语义）
- [ ] 双击消费后时间戳清空——连点第三击回到单击语义
- [ ] 收弹层仅可见时收（`StatusPopoverController.HideIfVisible`）——Win32 双击第二击按下瞬间弹层多半已被失焦通道收起，无条件 Hide 会重复触发 `VM.OnHidden`
- [ ] 阈值 / 时钟 / 平台门控三个 internal 注入点——headless 用例注入确定值与时序，不赌真实计时与宿主平台（开发机可能在 macOS/Linux 跑测试）
- [ ] 单击行为回归保护：间隔超窗的两次点击仍走弹层 Toggle；既有单击分派用例改注入慢时钟（微秒间隔的三连击在 Windows 宿主默认双击启用下会命中双击分派，语义不符）
- [ ] macOS interop / Linux 路径零行为变化（macOS 左键直进 `TogglePopoverOrShowWindow`，不经过拦截层）
- [ ] 零新增文案（i18n 不动）、无「可配置单击行为」设置项（YAGNI，守住 opinionated 立场）

## Dependencies

- none（与 B-017 失焦守卫同文件相邻面，已收口；交互推演见设计文档 §3——守卫只作用于第一击的 Toggle 语义，双击计时在拦截层独立记录）

## Development Log

### 2026-10-26

- 建卡即收口（TDD：RED 编译失败 8 处全指向缺失注入点 → GREEN）。实现三件：`TrayService` 增 `_lastTrayClickedUtc` + `TrayDoubleClickEnabled/Window/Clock` 三注入点（P/Invoke `GetDoubleClickTime` 与 `StatusPopoverController.GetCursorPos` 同风格，属性初始化器按 `IsWindows()` 门控）；`OnTrayClicked` 前置拦截（`isDoubleClick` 判定 → `HideIfVisible` + `ShowWindow`，消费制清时间戳）；`StatusPopoverController.HideIfVisible` 新公共入口（保留 `WindowForTest`"正常运行无其他消费者"的既有契约）。
- 测试 +3（双击窗内两击开主窗收弹层 / 超窗两击仍 Toggle 单击保护 / 无控制器旧构造双击幂等不炸）+ 既有 `Tray_click_toggles_popover...` 改注入 1s 步进假时钟（语义显式化为"非双击的独立单击"）。全量：Core 189 / Reconciliation 49（19 跳过）/ Infra 681 / Headless 399 全绿；App.Tests 266 单独重跑全绿（全解决方案并发轮 1 失败 `SingleInstanceGuardTests` 单类过滤必过——同 B-017 注记的环境性单例失败家族，与本卡零交集）。
- 明确不做（当时裁决）：macOS/Linux 双击；延迟单击判定；「可配置单击行为」设置项。
- 真机双击手感（系统双击时长阈值下的连点/三击体验）headless 无法模拟 WM_LBUTTONUP 真实时序，移交用户目检。

### 2026-10-26 回退（真机翻车，同日收口即回退）

- 用户 Windows 真机实测反馈「体验极差，交互上会和单击 Switch 开关冲突」→ revert 7416e82 整体回退，卡片退回 Todo 待用户决策出路。冲突机理：B-017 收口后托盘左键单击是 Switch 语义（弹层开着点=收层）——弹层可见时快速第二击本意收层，却被时间窗判定成双击直接唤出主窗；两次 WM_LBUTTONUP `Clicked` 流上的时间窗判定无法区分「快速连击收层」与「双击开窗」，属方案级冲突，调参不可救。
- 回退面：`TrayService` 拦截层（时间戳字段 / 三注入点 / `GetDoubleClickTime` P/Invoke）、`StatusPopoverController.HideIfVisible`、+3 用例与既有单击用例假时钟改造全部撤除，不留死代码；B-017 失焦守卫原样不动。AC 复选框随回退复位。
- 澄清：本卡 Background 曾引用设计文档 `docs/plans/2026-10-26-tray-double-click-design.md`，该文件从未入库（悬空引用）；若日后重启本卡，先补设计记录再动工。
