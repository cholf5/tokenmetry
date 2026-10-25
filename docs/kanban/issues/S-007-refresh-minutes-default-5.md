---
id: S-007
title: 刷新间隔默认值 1 分钟 → 5 分钟——采集管道每分钟空转的常态开销收口
type: setting
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

`AppSettings.RefreshMinutes` 默认值从 1 改为 5，把"后台定时全量采集"的常态频率降到与数据实际变化节奏匹配的档位。

## Background

- 内存调查（2026-10-25 会话）实测：后台刷新定时器按 `RefreshMinutes` 驱动，默认 1 分钟一轮完整采集管道（19 源扫描 → 全历史去重 → 计价 → facts 聚合落库）。JSONL 增量扫描器虽跳过未变更文件，但每轮仍对全历史做 SelectMany + 消息级去重重建（I-042 卡的管线）；零数据变化时这些工作是纯空转。
- 默认 5 分钟下，同样的空转频率降为 1/5；用户在设置页仍可调回 1–1440 任意档（校验范围不变）。手改 settings.json 绕过校验的路径已有下限钳制（AppServices 装配处 `Math.Max(1, ...)`），语义不动。

## Acceptance Criteria

- [x] `AppSettings.RefreshMinutes` 默认值 = 5，带注释说明取值理由
- [x] 既有默认值断言（JsonAppSettingsStoreTests 两处 Be(1)）同步更新
- [x] 设置页 1–1440 校验范围与 AppServices 下限钳制不动
- [x] 全量测试绿

## Dependencies

- none

## Development Log

### 2026-10-25 收口

- 改动三处：`AppSettings.cs` 默认值 + 注释；`JsonAppSettingsStoreTests` 两处默认值断言；`AppServices.cs` 装配注释同步（"默认已在 AppSettings 层改为 1 分钟" → 5 分钟）。
- 有意不做：不改 BackgroundRefreshService 构造参数默认值（测试便利，与产品默认无关）；不改设置页校验下限（1 分钟仍是合法档位）。
