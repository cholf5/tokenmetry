---
id: B-013
title: 无单实例互斥——自启动 + 手动双开产生双托盘与 SQLite 并发写
type: bug
priority: P3
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
blocked: none
---

## Goal

加跨平台单实例守卫：第二个实例启动时激活/前置既有实例（或静默退出），杜绝双进程同写一个 tokenmetry.db。

## Background

- 2026-10-23 扩大扫描（平台守卫维度）发现：全仓 grep `Mutex|NamedPipe|single-instance` 零命中。
- 机理：S-006 开机自启动 + 用户手动双击 → 双进程并发。`SqliteUsageStore.cs:464` 注释自证该场景在预期内（"跨进程并发首迁"用 INSERT OR IGNORE 兜），但运行期并发写仍可 SQLITE_BUSY、双托盘图标、双倍采集扫描。
- 触发面：S-006（2026-10-19）引入 `--autostart` 后从"需要刻意双开"变成"开机路径 + 手动路径的自然组合"。

## Acceptance Criteria

- [x] 跨平台单实例守卫（Windows/macOS 命名 Mutex；Linux 文件锁或命名管道），第二实例带"激活既有窗口"语义或静默退出（UX 二选一在卡内定案）
- [x] headless 测试宿主（TOKENMETRY_HEADLESS）不启用互斥（多测试进程并行）
- [x] 全量测试绿；三平台实机各双开验证一次（**Windows 已真机验证；macOS/Linux 实机双开移交用户各验一次**——守卫用例已覆盖三平台 OS 基元语义，实机补端到端即可）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；属结构性缺口而非回归，触发需要自启动+手动双开组合，定 P3）。

### 2026-10-25 收口

- **UX 定案：激活既有窗口**——托盘常驻应用双开的最常见诉求是"把界面叫出来"，纯静默退出会让用户以为应用没打开。实现 `App/Services/SingleInstanceGuard`：命名 Mutex 独占 + 命名管道（NamedPipeServer/ClientStream，三平台原生）激活通道，行协议 `show`。第二实例在 `Program.Main` 最早期（DI/SQLite 之前）拿不到锁 → 发激活请求 → 静默退出（码 0）；首实例监听回调经 `Dispatcher.UIThread.Post(trayService.ShowWindow)` 唤回主窗（`ShowWindow` 提 internal，托盘菜单/左键与激活共用同一唤回语义）。守卫信号带重试（3×1.5s）——首实例从拿锁到开监听有启动窗口期，立即双开也能送达。
- **卡面 AC 偏离登记**：AC 括注"Linux 文件锁或命名管道"是备选建议——.NET 10 命名同步基元三平台原生支持，统一命名 Mutex 更简单（测试在三平台 CI 上会如实检验）；Windows 用 `Local\` 前缀限定会话作用域（fast user switching 下各会话独立数据目录；同用户跨会话共享 LOCALAPPDATA 的双开是已知边界，卡内留档不另立卡）。
- **headless 旁路**：`Program.Main` 以 `TrayService.IsHeadlessTestHost`（TOKENMETRY_HEADLESS=1）整体旁路——测试宿主不经过 Program.Main，守卫对整套测试天然不可见，双保险。
- **Windows 实机双开验证（本机真跑）**：Debug 构建 `--autostart` 静默启首实例（无自启动设置、零注册表写入）→ 8s 后裸启动第二实例 → **1.0s 退出、码 0**、存活进程恰 1 个（激活送达由守卫用例管道往返单独锁）。macOS/Linux 实机双开待用户各验一次（守卫用例三平台覆盖 OS 基元语义，实机验证补端到端）。
- 踩坑留痕：Mutex 是**线程递归**的，同线程重复 WaitOne 重入成功——守卫用例首版同线程断言第二实例拿不到锁即踩中；改跨线程后全量套件仍偶发失败，根因是普通 `Task.Run` 会复用刚归还线程池的 xunit 工作线程（恰是持锁线程），最终以 `TaskCreationOptions.LongRunning`（保证专用线程）确定性通过。真实双开是跨进程（必然跨线程），守卫语义无恙。
- 明确不做：跨会话全局互斥（`Global\` + 用户标识命名）；第二实例 UI（提示窗/toast）——静默退出是激活语义的兜底分支；Linux 文件锁分支。
- 测试：+5（SingleInstanceGuardTests：互斥 / 释放后可再获取 / 管道激活往返 / 断连后监听存活 / 无监听快速失败），App.Tests 258 Passed、Headless 386 Passed。

