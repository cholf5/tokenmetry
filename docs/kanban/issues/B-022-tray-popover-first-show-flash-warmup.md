---
id: B-022
title: 托盘弹窗首显闪动——Show 先画空态再被回填数据撑高，改启动预热让首帧即最终高度
type: bug
priority: P2
size: S
status: done
created: 2026-10-27
updated: 2026-10-27
blocked: none
---

## Goal

Windows 首次点托盘弹出快捷信息窗时不再「先出现矮卡（空态）→ 数据回来整体向上跳一截」；首帧就是完整高度。二次打开与 macOS / 回落路径行为不变。

## Background

- 用户反馈（B-021 收口后）：首次打开托盘快捷窗会「闪一下」。
- 机理（B-021 的底缘锚定把原有加载时序暴露成可见跳动）：`Show()` 里先 `ViewModel.OnShown()` 发起查询、紧接着显示窗口——此刻内容层是空态（headless 实测 420 DIP）；SQLite 查询在 Show 返回后才回填，`SizeToContent="Height"` 把窗口撑高（1 Agent + 1 模型 543 DIP，满载 719 DIP），`SizeChanged` 按首锚重算位置 → 底缘不动、顶缘上跳 123/299 DIP。加上占位「—」被真实数据替换，观感即「闪一下」。
- 二次打开不闪：VM 已持有数据，首帧就是最终高度。B-021 之前不显眼：同样长高，但窗口向下长（底缘越界），表现为「位置偏下/被裁」而非上跳。

## Acceptance Criteria

- [x] `StatusPopoverController.WarmUpAsync()`：显示前只读预热——创建 VM 并跑一轮 `ReloadAsync`，不显示窗口、不置可见态；已显示时跳过（新鲜度归 OnShown / 刷新轮次）；可重复调用（VM 加载序号丢弃过期响应）；失败只退回首显重查的两段式加载
- [x] App 启动接线两次装载：创建托盘后立即预热（库内已有上次会话数据），`BeginStartupDataRefreshAsync` 结束后再预热（首次使用/今日数据要等首轮采集入库）；预热任务异常只记诊断，不阻塞启动、不影响托盘
- [x] headless 回归锁 `Warm_up_loads_content_before_first_show_so_the_first_frame_is_final`：预热后 VM 有数据 / 无窗口 / 非可见态；Show 的重查挂起时首帧高度 == 放行后高度（预热为空操作时该断言 RED）
- [x] 全量：Core 189 / Reconciliation 49（19 跳过按本机 fixture）/ Infra 681 / Headless 397 / App.Tests 306 全绿
- [x] 明确边界：预热不改变 `OnShown` 的每轮刷新语义；不引入「等数据就绪再 Show」（点击延迟）与生长动画

## Dependencies

- B-021（首显底缘锚定，已收口）

## Development Log

### 2026-10-27

- 建卡即收口。实现两处：① `StatusPopoverController.WarmUpAsync`（internal，App 与测试同 assembly/InternalsVisibleTo 可达）——`ViewModel` 懒建 + `ReloadAsync`，窗口不建、`_visible` 不置；② `App.axaml.cs` 把 `BeginStartupDataRefreshAsync()` 的任务留存，`WarmUpPopoverAsync` 先立即装一次、`await` 首轮采集后再装一次，异常统一记 stderr。
- 取证/回归：临时 trace（已删）实测首显空态 420 DIP → 回填 543（1 Agent + 1 模型）/ 719（4 Agent + 2 模型），且 `SizeChanged` 只在上跳时改一次位置；预热后首帧直接是回填高度。回归锁用 `GatedQueryService.GateEnabled` 先关后开：预热同步装数、Show 的重查挂起，断言首帧高度不再变化；把 `WarmUpAsync` 内部改成空操作实测 RED → 恢复 GREEN。
- 测试：Headless 397（+1）。全量 Core 189 / Reconciliation 49（19 跳过）/ Infra 681 / App.Tests 306 全绿（App.Tests 首轮 1 失败仍为 `SingleInstanceGuardTests` B-020 环境性 flake，重跑即过）。
- 已知边界：启动后首轮采集尚未完成就点托盘的场景（尤其首次安装读到的库还是空的），立即预热拿到的行可能少于采集后的最终行 → 仍可能小跳一次；首轮结束后的第二次预热覆盖之后的点击。不在点击路径上等待（弹层仍秒开）。
- 明确不做：不改 `Show()` 时序/不加动画（弹层仍秒开，预热只是把数据先装好）；不动 `PopoverPositioner` / B-021 锚定；未处理首显毛玻璃形态协商的窗体宽 498→438 切换（若真机仍见横向小幅抖动，归该路径，另立卡）。
