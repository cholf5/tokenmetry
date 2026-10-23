---
id: B-013
title: 无单实例互斥——自启动 + 手动双开产生双托盘与 SQLite 并发写
type: bug
priority: P3
size: S
status: todo
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

加跨平台单实例守卫：第二个实例启动时激活/前置既有实例（或静默退出），杜绝双进程同写一个 tokenmetry.db。

## Background

- 2026-10-23 扩大扫描（平台守卫维度）发现：全仓 grep `Mutex|NamedPipe|single-instance` 零命中。
- 机理：S-006 开机自启动 + 用户手动双击 → 双进程并发。`SqliteUsageStore.cs:464` 注释自证该场景在预期内（"跨进程并发首迁"用 INSERT OR IGNORE 兜），但运行期并发写仍可 SQLITE_BUSY、双托盘图标、双倍采集扫描。
- 触发面：S-006（2026-10-19）引入 `--autostart` 后从"需要刻意双开"变成"开机路径 + 手动路径的自然组合"。

## Acceptance Criteria

- [ ] 跨平台单实例守卫（Windows/macOS 命名 Mutex；Linux 文件锁或命名管道），第二实例带"激活既有窗口"语义或静默退出（UX 二选一在卡内定案）
- [ ] headless 测试宿主（TOKENMETRY_HEADLESS）不启用互斥（多测试进程并行）
- [ ] 全量测试绿；三平台实机各双开验证一次

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；属结构性缺口而非回归，触发需要自启动+手动双开组合，定 P3）。
