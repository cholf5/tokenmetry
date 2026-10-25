---
id: Q-015
title: 异常 toast 当天只弹一次——日累计异常 × 分钟级刷新的整日轰炸收敛
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

「今日成本 ≥ 7 日均值 × 2」异常 toast 在主窗口**当天只弹一次**（按异常类型记账）；跨日异常重新成立时再弹。弹层 KPI 徽标（Q-011）照旧每轮由 `AnomalyDetected` 事件驱动承载持续状态——去重只限 toast 通道，事件契约不动。

## Background

- 用户反馈（2026-10-25 会话）：主窗口异常提示太频繁、扰民，想干掉。用户猜测与 Q-011（Warn banner 归位徽标）同源。
- 取证：Q-011 只改了弹层侧（徽标 + 事件载荷 Ratio），提交自述「toast 侧行为不动」——每轮重弹是更早既有行为，只是日累计异常触发日才显形：
  1. `AnomalyDetector` 判定日累计型（成本只增、历史均值全天固定），触发后**全天每轮为真**；
  2. `BackgroundRefreshService` 默认 1 分钟一轮，`RefreshAsync` 每轮检测命中即 `ISystemNotification.ShowAsync`；
  3. 主窗可见期 toast 无限重弹（主窗隐藏期 toast 本就不可见，即待办 B-014）。
- 设计判断：与 Q-011 同一哲学的 toast 侧补齐——持续状态交给常驻展示面（徽标），attention-grabbing 通道（toast）只报「新发生」。当天首次触发值得提醒一次，其后每分钟重复同一句没有信息增量。

## Acceptance Criteria

- [x] 同日多轮刷新、异常持续为真：toast 恰 1 条（RED 实锤旧行为 3 轮 3 条）
- [x] 跨日：新一天异常重新成立（倍率再次 ≥2）时再次提醒（账本随日期翻页清空）
- [x] `AnomalyDetected` 事件仍每轮 raise（Q-011 契约回归锁——弹层徽标依赖「异常轮次每轮置回、无异常轮次清空」）
- [x] 设置关闭的轮次不预占当天名额（短路在记账之前）：当天中途开闸后下一轮异常仍提醒一次
- [x] 全量 `dotnet test Tokenmetry.sln` 绿（1540 Passed / 0 Failed / 22 Skipped，+4 即本卡用例）

## Subtasks

- [x] RED：4 用例（同日一次 / 跨日重弹 / 事件每轮照发 / 关闭不占名额），测试基建 `FixedDateRangeService.SetToday` + `FakeUsageDataSource.Rows` 可变化
- [x] GREEN：`DashboardViewModel` 当天 × Kind 记账（`_anomalyToastDay` + `_anomalyToastNotifiedKinds`，日期翻页清空；`settings && Add(kind)` 短路保证关闭轮次不记账）
- [x] 全量测试 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- **追记（同日夜，Q-016）**：本卡的「当天一次」去重当晚被用户二轮反馈推翻——拍板 toast 通道整体删除（Q-016），去重账本随之退场；事件每轮 raise 契约用例由 Q-016 承接保留。本卡留档为频率问题的第一次收敛尝试与取证载体。
- 修法要点：toast 门从 `if (settings.Enabled)` 收紧为 `if (settings.Enabled && _anomalyToastNotifiedKinds.Add(anomaly.Kind))`——`HashSet.Add` 的返回值天然是「首次」判定；`Today()` 字符串作日键（invariant，与 facts 对账键同源）。轮次由 `BackgroundRefreshService` 单飞槽串行，账本无并发写。
- 明确不做：toast 完全删除不做——设置页「启用异常通知」开关是真实出口（A-11 修复过假开关，整删会把它再变假），且「当天首次发生」仍有提醒价值；用户想彻底安静走设置关闭。多异常类型并存按 Kind 分账（当前仅 `daily_cost_spike`，为未来检测器扩充预留）。B-014（托盘常驻态 toast 不可见）不在本卡范围。
