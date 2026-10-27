---
id: B-021
title: 托盘弹窗首显仍偏下——SizeToContent 数据异步回填撑高窗口，位置只在 Show 后算一次（底缘越过任务栏）
type: bug
priority: P2
size: S
status: done
created: 2026-10-27
updated: 2026-10-27
blocked: none
---

## Goal

Windows 托盘左键首显快捷信息窗时，底缘始终贴住托盘光标上方（gap 10 DIP），不再随数据回填向下长过任务栏；二次点击（内容已就绪）行为不变，macOS / 回落锚定语义不变。

## Background

- 用户报告（B-016 修复后）：首次点托盘弹窗位置仍偏下，footer 被屏幕底缘裁掉；收起再点第二次位置就正常——截图对照首显缺 footer、二次完整。
- 机理（B-016 只修了「首显取陈旧 Bounds 高度」，没覆盖「显示后高度继续增长」）：`Show()` 是
  `Show → PositionWindow()` 一次性锚定；此刻 VM 刚创建，内容为空态（headless 实测 ClientSize 420 DIP），而 `ReloadAsync` 的 SQLite 查询在 Show 返回后才回填。`SizeToContent="Height"` 窗口只向下生长，位置不再更新 → 底缘 = 初始底缘 + 高度增量，越过任务栏；第二次 Show 时数据已就绪、ClientSize 即最终值，故正常。
- 实测（headless 1920×1280 @1x，真实滚动式查询 stub）：空态 420 DIP → 4 Agent + 2 模型回填后 719 DIP；修复前 Position 完全不动，底缘向下跑 299 DIP。用户截图形态（footer 整块被裁）与此一致；只加载 1 Agent + 1 模型时增量 123 DIP。
- B-016 的 `ClientSize` 修法仍是对的（首显不能取陈旧 Bounds）；缺的是「显示期尺寸变化的跟随」。

## Acceptance Criteria

- [x] `Show()` 捕获本次点击瞬间的光标点作为显示周期首锚（每次 Show 重取；重锚复用，不跟当前光标跑——用户把鼠标移开后再遇到 banner 显隐也不会跳位）
- [x] 壳窗 `SizeChanged`（显示期）→ 按同一首锚重算位置：Windows 底缘钉在光标上方，窗口向上长；macOS / 回落顶缘锚定重算幂等。隐藏期尺寸变化不处理（下次 Show 用新光标 + 当时尺寸重定位）
- [x] headless 回归锁 `First_show_reanchors_to_click_point_when_async_content_grows`：GatedQueryService 挂起查询复刻真实异步回填，注入确定光标锚点；断言初始/回填后两次底缘都 = 光标上方 10 DIP（真实光标不可控，新增 `CursorAnchorOverrideForTest`）；去掉 SizeChanged 接线实测 RED（底缘漂到 1373 vs 期望 1250）→ GREEN
- [x] `PopoverPositioner` 纯函数与锚定间隙零改动；`ScreenSize` 继续用 `ClientSize`（每条路径取当次值）
- [x] 全量：Core 189 / Reconciliation 49（19 跳过按本机 fixture）/ Infra 681 / Headless 398 / App.Tests 306 全绿（App.Tests 首轮 1 失败 = `SingleInstanceGuardTests` 既有 B-020 环境性 flake，单类过滤与重跑全量均通过）

## Dependencies

- B-016（同文件前序修复，已收口）

## Development Log

### 2026-10-27

- 建卡即收口。修法三处（`StatusPopoverController`）：① `Show()` 在建窗后捕获 `_anchorCursor`（测试注入优先，否则 Windows `GetCursorPos`；非 Windows 为 null）；② 建窗时订阅 `window.SizeChanged += OnShellWindowSizeChanged`，显示期（`IsVisible`）才 `PositionWindow()`；③ Windows 分支改用首锚光标（不再每次重取 `GetCursorPos`），`ScreenSize` 每条路径取当次 `ClientSize`。
- 取证路径：临时 headless 量测（已删）——同步 stub 量出空态 420 / 满载 719 DIP；GatedQueryService（查询挂起到 Release）复刻真实时序，修复前 `initialClient=420, afterClient=719, Position` 不变；注入锚点后修复前底缘 830+543=1373，修复后 707+543=1250。
- 测试 +1（`StatusPopoverShellTests` 内，Windows 条件守卫——底缘锚定只在 Windows 分支；macOS/回落顶缘锚定天然不受高度增长影响）。踩坑：断言须在回填后把 dispatcher 泵到队列排空（数据回填 → 属性通知 → 布局 → SizeToContent 尺寸回写分属多轮，只泵到 `TopAgents` 非空会读到中间尺寸）。
- 明确不做：不做「等数据就绪再 Show」（弹层要秒开，且 banner 显隐仍会在显示期改高度）；不加弹出/生长动画；不改 macOS 锚定语义（顶缘锚定本就幂等）；真机 Windows 首显目检移交用户（headless 已验证几何不变量，真机观感以用户截图为准）。
