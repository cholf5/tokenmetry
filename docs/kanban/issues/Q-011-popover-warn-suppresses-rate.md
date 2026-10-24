---
id: Q-011
title: 托盘弹层 Warn banner 独占——日尺度异常归位今日花费 KPI 徽标，banner 还给实时速率
type: bug
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

弹层 banner 只承载分钟尺度的实时速率（Active/Idle 两态）；「今日花费超过去 7 日均值」这类日尺度异常不再抢占 banner，归位到今日花费 KPI 格的警告徽标（紧凑倍数 + Tooltip 完整文案）——两条信息正交并存。异常存在期间「当前速度 {0}」提示不再整日消失。

## Background

- 用户反馈（2026-10-24 会话）：当日花费超过过去 7 天平均值时常驻警告一出现，"当前正在使用中，消耗速度 xxx"提示就不再显示。
- 取证（三处叠加 = 触发日速率提示全天被压制）：
  1. `AnomalyDetector` 判定是日累计型（今日成本 ≥ 7 日均值 × 2），成本只增不减、历史均值全天固定——一旦为真当天不可能回落；
  2. `DashboardViewModel.RefreshAsync` 每轮重检测重 raise `AnomalyDetected`；
  3. `StatusPopoverViewModel.RecomputeState` 严格优先级 Warn > Active > Idle，Warn 分支提前 return。
  D-015 注释「下一轮无异常的刷新把 Warn 清回 Idle/Active」的前提对日累计型异常不成立——"无异常的下一轮"等不到。
- 设计判断：Warn 是天尺度事实（全天不变，KPI/toast/总览页都可见），Active 是分钟尺度事实（只在最近一轮有增量时存在，错过即无）——让常驻信息挤掉转瞬即逝的信息，优先级是反的。三方向（徽标归位 / banner 共存 / 限时显示）中用户拍板方向 1（信息贴在它描述的数字旁，最合 opinionated IA）。

## Acceptance Criteria

- [x] 异常存在时 `State` 仍可为 Active、速率 banner 照常显示（回归锁：旧行为 Warn 抢占 banner）
- [x] 今日花费 KPI 格出现警告徽标：有 Ratio → 紧凑倍数文案（↑{0:0.0}× 均值 / ↑{0:0.0}× avg），无 Ratio → 通用文案（不伪造倍数）；Tooltip = 完整异常消息；语言切换随 Translations 重算
- [x] 无异常轮次清徽标（`_pendingWarn` 生命周期不变：轮次先清、同轮新异常再置回）
- [x] `StatusPopoverState.Warn` 态、`PopoverLiveWarn` 键、banner warn 配色类撤除；`AppBannerWarn*` 令牌转由徽标复用（不新增令牌）
- [x] 全量 `dotnet test Tokenmetry.sln`：本卡全部用例绿（headless 弹层 19/19，+4 即本卡用例）

## Subtasks

- [x] RED：`StatusPopoverTests` 重写 Warn 用例为徽标用例（并存回归锁 / 无 Ratio 回落 / 清除 / 视图层徽标渲染），看失败（缺失成员编译错误实锤）
- [x] GREEN：`StatusPopoverViewModel` 两态机 + `RefreshCostWarn` 徽标派生；`DashboardViewModel.AnomalyRaised` 增 `Ratio`
- [x] `StatusPopoverView.axaml` banner warn 类撤除 + KPI 花费格徽标；`Translations` 增 2 键删 1 键（中源英覆同改，键奇偶不破）；`StylePreviewView` 演示更新
- [x] 全量测试 + 卡片移 Done

## Dev Log（2026-10-24 收口）

- TDD：RED = 新用例引用缺失成员（`IsCostWarn`/`CostWarnBadgeText`/`CostWarnDetail`/`NoteAnomaly(string, decimal?)`/Translations 两新键）编译失败实锤 → GREEN 实现后 headless 弹层 19/19。中途一翻车留痕：语言切换断言最初未触发 `OnLanguageChanged`（VM 级测试无视图，CurrentLanguage 订阅不存在）——徽标重译生产路径 = 视图订阅 → `OnLanguageChanged` → RecomputeState，测试改为显式调用锁 VM 半边重译（视图半边由 LiveText TC-002 既有覆盖）。
- 设计细节：状态灯只反映实时态（warn 灯一并撤除——灯是单色语义，异常着灯会在灯上重演优先级抢占）；徽标配色复用 D-015 warn banner 的 `AppBannerWarn*` 令牌 + `AppAccentOrangeBrush` 文本（明暗各自协调值），零新增令牌。
- 并发注记：与模型头像特性会话同工作区并行，共享 4 文件（Translations / 弹层 VM / 弹层 XAML / StylePreview）改动区域不同、经选择性暂存分账；全量套件在合并工作树上运行，6 个失败全部属于头像特性进行中用例（ModelDefinitionAvatar / ModelAvatarChip / AvatarImageProcessor / AvatarPicker），与本卡无关。互相解阻塞两笔：本会话给对方 `DataUriImageConverter` 补 `Avalonia.Media` using（`IImage`），对方会话把本卡测试的 `Assert.Single(Where())` 改为谓词重载（xUnit2031）。
