---
id: D-026
title: Windows 原生标题栏深色适配——深色主题下的白色 caption 收口（含窗口标题缩短）
type: feature
priority: P2
size: S
status: done
created: 2026-10-27
updated: 2026-10-28
blocked: none
---

## Goal

Windows 上主窗、首关提示窗、启动错误窗的原生标题栏跟随应用明暗：深色主题下 caption 为深色（Win10 纯黑 / Win11 系统深色），不再出现深色界面顶白色标题栏；浅色主题保持系统浅色。窗口标题缩短为 "Tokenmetry"。

## Background

- 用户反馈（2026-10-27 截图）：Windows 深色界面 + 纯白系统标题栏，观感像未完成的移植。
- 取证：经典 Win32 caption 不跟随应用主题，即使 Windows 深色模式也保持浅色，除非应用经 DWM 主动声明（`DWMWA_USE_IMMERSIVE_DARK_MODE`）。macOS 标题栏是深色材质并集成红绿灯，所以同一套 UI 在 Mac 上不暴露。
- 2026-10-12 D-003 决策「沿用系统边框、不复刻 mac 红绿灯」不变：本卡只改 caption 配色，不动 `WindowDecorations` / 客户端区，不引入自绘标题栏。
- 标题字符串决策取代 D-003 中的 "Tokenmetry — Token Analytics"：副标题在 caption / 任务栏里重复侧栏品牌，且无人阅读。

## Acceptance Criteria

- [x] 深色主题：主窗 caption 深色（真机 DWM 属性 20 回读 = 1 + 截图实证）；运行期明暗双向切换未人工演练——浅色显式清零与重应用逻辑由单测 + `ActualThemeVariantChanged` 接线锁定，目检移交用户
- [x] 首关提示窗 / 启动错误窗同样应用（代码建窗共用同一 helper，接线经代码审查确认；本机首关提示已展示过、启动错误窗不构造故障，均未人工目检）
- [x] 非 Windows / headless / 空句柄静默跳过，零 P/Invoke（单测锁三类跳过条件；headless 全量 398 经 `Attach` 不回归）
- [ ] 系统边框行为不变：最小化/最大化/关闭、双击 caption、拖动、Aero Snap、DWM 动画（实现零触碰窗口装饰 API——DWM 属性只影响 caption 配色，未逐项人工演练，移交用户日常验证）
- [x] 窗口标题为 "Tokenmetry"（真机 `GetWindowText` + 截图实证）
- [x] 单元测试锁决策、回落与跳过条件（6 用例）；真机 Windows 目检留证（DWM 回读 + caption / 整窗截图）

## Dependencies

- 无

## 明确不做

- 不自绘标题栏、不改 `WindowDecorations` / `ExtendClientAreaToDecorationsHint`
- 不做 `DWMWA_CAPTION_COLOR` / `DWMWA_TEXT_COLOR` 精确配色（压掉系统 active/inactive 与高对比度适配）
- 不合并 31px caption 与 52px 顶栏（那是独立的 chrome 升级项）
- 不改 macOS / Linux 行为，不新增设计令牌或 i18n key

## Development Log

- **根因与修法**：经典 Win32 caption 不跟随应用主题（即使 Windows 自身深色模式），深色 UI 顶白色标题栏；macOS 深色材质标题栏不暴露同题。修法：向 DWM 声明沉浸式深色（属性 20，Win10 2004+；被拒回落属性 19 适配 1809–1903；两者都失败静默放弃保持系统默认），随 `Opened` 首显与 `ActualThemeVariantChanged` 重应用；浅色显式写 0 防残留（不能走"跳过"分支）。
- **实现**：`App/Services/WindowsCaptionTheme.cs`（internal，三个注入点：trySetAttribute / isWindows / 环境变量读取，`Default` 生产实例；`TOKENMETRY_HEADLESS == "1"` + `TryGetPlatformHandle()` 双重守卫，B2 双轨纪律同 StatusPopoverWindow DWM 圆角先例）。接线三处：`MainWindow` 构造器（D-018 图标赋值后）、`MainWindow.BuildHintWindow`、`App.ShowStartupError`；`MainWindowViewModel.Title` → "Tokenmetry"；`MainWindow.axaml` 头注释补 D-026 一句。
- **测试**：`WindowsCaptionThemeTests` 6 用例（Dark→1 / Light→0 / 空句柄 / 非 Windows / headless 跳过 + 属性 20 被拒回落 19）。TDD：CS0246 RED 实锤 → 6/6 GREEN。全量：Core 189 / Reconciliation 49（19 跳过）/ Infrastructure 681 / App.Tests 312（+6）/ Headless 398，0 failed；`dotnet build Tokenmetry.sln` 0 警告 0 错误。App.Tests 曾一轮并发跑出 `SingleInstanceGuardTests` 既有 flake（B-020 环境性），单类过滤 5/5 过，豁免在案；全量终轮未复现。
- **真机验证**（Windows 10 19045，系统应用模式 = 深色，settings Theme=system）：启动后 `GetWindowText` = "Tokenmetry"；`DwmGetWindowAttribute(hwnd, 20)` 回读 = 1（hr=0x0）；caption 截图深色、与深色顶栏无缝，无白色横条（截图存会话临时目录 `%TEMP%\d026-caption.png` / `d026-full.png`，不入库——同 Q-019~Q-022 截图纪律）。验证实例以 `Stop-Process` 清理（本机设置关窗 = 进托盘，`CloseMainWindow` 仅隐藏不退出）。
- **计划偏差登记（两处）**：① 计划测试代码把局部 `calls` 声明为无名元组 `(IntPtr, int, int)` 而末例断言用具名成员 `.Attribute/.Value`（CS1061 编译错），六处声明补具名 `(IntPtr Hwnd, int Attribute, int Value)` 对齐工厂签名；② 计划实现注释称「ThemeVariant 没有 == 运算符重载」不实——`ThemeVariant` 是 `sealed record`，编译器合成 `==`/`!=`（按 Key 值比较，与 `Equals` 同语义），实现注释改为陈述真实约束（非 Dark 变体一律写 0），代码路径（`Equals` 比较）与计划一致。
- **移交用户目检**：运行期深→浅→深双向切换、边框原生行为（拖动 / 双击最大化 / Aero Snap / 最小化最大化关闭动画）、首关提示窗 caption。git 提交不在本卡范围（计划约定），变更留工作区待审。
- **代码审查（2026-10-28）**：com-requesting-code-review 通过——无 P0/P1；一处 P2 观察（DwmSetWindowAttribute 两处声明）确认非问题不合并；文档 `docs/plans/2026-10-28-d026-windows-dark-title-bar-review.md`。
