---
id: B-009
title: ScrollBarOverlay headless 测试泵缺渲染失效（渲染空闲后 DispatcherTimer 不推进）
type: bug
priority: P3
size: S
status: done
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

`ScrollBarOverlayTests.Hovering_bar_pins_it_shown_past_idle` 在 Windows headless 下稳定通过，且测试泵的 headless 陷阱被注释钉死，防止再误判为控件状态机问题。

## Background

- 2026-10-23 Windows 11 冒烟实锤：该测试最后断言（ScrollBarOverlayTests.cs:176，指针移出后 `overlay-shown` 应消失）超时。父提交 b227c79 worktree 复现 → 既有问题。macOS 时序不同碰巧通过。
- Windows 本机诊断证据链：移出后 `bar.IsPointerOver=False`、`PointerExited` 已触发，但 class 不消失；100 轮 `RunJobs + ForceRenderTimerTick + Sleep(1)` 里另建的 1ms `DispatcherTimer(Background)` 探针 tick 0 次；每轮加 `InvalidateVisual()` 后探针 tick 100 次、overlay 正常收回。
- 结论：**headless 平台渲染空闲后不再推进 DispatcherTimer 时钟**——测试私有 `PumpUntil` 没制造渲染失效，Background 优先级的闲置计时器一次都不 tick。产品控件由真实桌面平台调度/渲染循环驱动，生产行为无问题。测试侧修，产品 `ScrollBarOverlay` 状态机零改动。

## Acceptance Criteria

- [x] `PumpUntil` 每轮先 `InvalidateVisual()` 再 RunJobs + ForceRenderTimerTick（不删断言、不 Skip、不放大超时）
- [x] 注释钉死陷阱结论（渲染空闲 → DispatcherTimer 不推进），与 Shell/DispatcherPump 的异步加载泵语义边界写清
- [x] macOS 全量 Headless.Tests 无新增失败（339 绿，含全部 6 条 ScrollBarOverlay 契约测试）

## Development Log

### 2026-10-23（实施与收口，分支 fix/smoke-2026-10-23 eb3d6a2）

- 修复：`PumpUntil` 增 `Visual frameSource` 参数，六个调用点传 `window`，每轮先失效渲染帧；doc 注释写明陷阱与"勿与 DispatcherPump 合并"（那边等线程池续体，不需要帧）。
- 共享泵评估结论：全 headless 套件里只有本文件需要等墙钟 `DispatcherTimer` 到期（其余 ForceRenderTimerTick 用法都是单帧推进/动画钟），单消费者不抽公共助手。
- 验证：macOS Headless.Tests 339 绿。**Windows 实机复核待办**（陷阱机理的复现与修复效果都在 Windows 侧）。

## Verification

- Related files: `tests/Tokenmetry.App.Headless.Tests/Controls/ScrollBarOverlayTests.cs`
- How to run/verify: `dotnet test tests/Tokenmetry.App.Headless.Tests --filter FullyQualifiedName~ScrollBarOverlayTests`
- Results: macOS 全绿；Windows 11 实机重跑该测试待复核
