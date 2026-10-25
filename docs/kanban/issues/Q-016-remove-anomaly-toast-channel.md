---
id: Q-016
title: 异常通知 in-app toast 通道整体删除——异常唯一出口 = 弹层 KPI 徽标
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

删除主窗口的异常 toast 通知通道（用户拍板"删除掉"）：日累计异常 × 分钟级刷新下扰民。异常的持续状态由托盘弹层「今日花费 KPI 徽标」（Q-011）继续承载——`AnomalyDetected` 事件成为异常唯一出口，检测逻辑本身不动。

## Background

- 用户反馈（2026-10-25 会话，二轮）：主窗口异常提示"还是一直弹弹弹"，拍板整删。仍弹的直接原因：Q-015 修复只落在源码，用户运行的仍是旧构建（未重建重启）；本轮按"删除"口径彻底拆通道，同样需要重建重启才生效。
- Q-015（当天一次去重）被本卡取代：去重缓解频率但用户要的是"没有"。弹层徽标已完整承载持续状态，toast 无独有信息量。
- 删除红线对齐：A-11「假开关」红线反向适用——通道删除后，「启用异常通知」设置开关、`ISystemNotification` 抽象及其实现全部退场，不留无消费者的尸体。

## Acceptance Criteria

- [x] 主窗不再有任何异常 toast：`DashboardViewModel` 不再持有/调用通知出口，检测命中只 raise `AnomalyDetected`
- [x] 弹层徽标不受影响：事件每轮照发（既有契约锁保留），`AnomalyRaised` 载荷 = `Message + Ratio`（Title 随 toast 标题退场删除）
- [x] `ISystemNotification`（Core 抽象）/ `NullSystemNotification`（Infrastructure）/ `ToastSystemNotification`（App 适配）三文件删除；DI 注册撤除
- [x] 设置页「启用异常通知」开关删除；`AppSettings.AnomalyNotificationsEnabled` 删除（旧 settings.json 多余键反序列化忽略，A-11 同款语义）
- [x] `Translations` 删 `CheckAnomalyNotifications` / `AnomalyToastTitle`（中源英覆同删，键奇偶不破）；`AnomalyCostSpikeFormat` 保留（徽标 Tooltip 文案消费）
- [x] 全量 `dotnet test Tokenmetry.sln` 绿

## Subtasks

- [x] Core/Infrastructure/App 源码：通道三文件删除 + DashboardViewModel 通知依赖与 Q-015 账本退场 + 设置 VM/视图/AppSettings/Translations 清理
- [x] 注释对齐：AppServices、MainWindow（一次性模态注释改指 IToastService）、ToastService（线程注释改指重扫 toast）、DesignTokenTests 同步
- [x] 测试：Dashboard 用例重写为事件契约（载荷/每轮 raise/正常不 raise），设置页开关用例删除，Settings/Json/AppSettings/DashboardSummary 断言同步，ManualRefresh 构造去参
- [x] 全量测试 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- 分账：全量 1535 Passed / 0 Failed（22 跳过，基线一致）。App.Tests 257→253 = −5（旧 toast 用例）+2（事件契约新用例：载荷锁 + 正常不 raise）−1（设置开关用例）；Infrastructure 661→660（NullSystemNotificationTests 随类删除）。
- 有意保留：`IToastService` / `ToastService` 不动——它还有设置页重扫完成/失败反馈等消费者，本卡只拆「异常 → toast」这一条接线。
- 边界：托盘弹层徽标、`AnomalyDetector` 阈值（≥ 2× 7 日均值）不动；系统级通知（OS 原生）从未存在（B-014 相关注释），不新增。
