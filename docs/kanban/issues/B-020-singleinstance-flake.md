---
id: B-020
title: SingleInstanceGuardTests.Second_instance_cannot_acquire_while_first_holds 间歇失败——AbandonedMutexException 分支是唯一 true 产出路径，疑 GC/线程退出时序
type: bug
priority: P3
size: M
status: todo
created: 2026-10-26
updated: 2026-10-26
blocked: none
---

## Goal

`Second_instance_cannot_acquire_while_first_holds` 不再间歇失败（"Expected secondAcquired to be False, but found True"）。

## Background

- 出现史（环境性家族，多次吃掉验证周期）：2026-10-25 B-017 注记（git stash 干净工作区复现同款、单类过滤必过）、2026-10-25 深夜八注记（并行会话互踩怀疑）、2026-10-26 D-023 全量轮 + B-018 两连败（全量与单跑全套均败、单类过滤过）。
- **2026-10-26 证据钉死两点**：
  1. 与本机运行的真实 Tokenmetry 进程无关——测试用例名 Guid 隔离（`Tokenmetry-<guid>`），与真 App 的 `Local\Tokenmetry.SingleInstance` 不同名；
  2. 失败时 second 拿到 true 的唯一可能路径是 `TryAcquire` 的 `catch (AbandonedMutexException) return true`（SingleInstanceGuard.cs:52）——即 second 的 WaitOne 看到的是**被遗弃的命名互斥体**（持有者的句柄在持有期间被关闭，或持有线程已退出）。first 在用例里明明存活（`using var` 挂在异步状态机字段上）。
- 候选根因（待查）：GC SafeHandle 终结时序 / xunit v3 工作线程在 await 挂起期的归属判定 / `new Mutex(false, name)` 句柄生存期细节。需要带 `DOTNET_gc` 压力或复现脚本多跑取证。

## Acceptance Criteria

- [ ] 复现脚本化（循环跑单用例直至失败，记录失败率）
- [ ] 定位遗弃时序的真凶（GC 终结器日志 / handle 追踪）
- [ ] 修复（测试侧或守卫侧）+ 回归锁
- [ ] 全量套件连跑 3 次零失败

## Dependencies

- none（独立可查；查清前各卡验证遇此失败按本卡豁免记录）

## Development Log

### 2026-10-26

- 建卡（B-018 调查时顺手钉死两条证据，见 Background）。
