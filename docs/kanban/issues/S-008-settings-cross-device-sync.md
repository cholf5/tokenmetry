---
id: S-008
title: 应用设置跨设备同步——外观/语言/海报默认值/自定义时间预设（逐 key LWW，独立开关默认关）
type: feature
priority: P2
size: L
status: done
created: 2026-11-23
updated: 2026-11-24
blocked: none
---

## Goal

在已有中继同步之上，按「显式 opt-in 的独立开关」同步一小块**纯偏好设置**：外观画板 + 明暗模式、
界面语言、分享海报默认值、自定义时间预设。设备级设置与计价语义（模型目录/别名/价格覆盖）明确不同步。
设计见 `docs/plans/2026-11-23-settings-sync-design.md`（本地文档）。

## Background

- 开工前先修掉的 bug：设置页 `SyncTokenInput` 从未从 `settings.SyncToken` 播种，关闭→再启用会用空串
  覆盖持久化 token，此后所有同步请求 401（真机数据目录可复现：token 为空、远端设备最后活跃停在旧日期）。
  同批修复还包含空凭据拒绝启用与状态行带出错误原因。
- relay 设计 A1 原定设置全部不同步；用户 2026-11-23 裁定按上述四类放开，并要求独立开关。
- 中继保持「笨桶」：只加一张 `account_settings` 表与 GET/PUT 两端点，无业务校验（仅长度/数量上限）。

## Acceptance Criteria

- [x] 中继 `account_settings` 表 + `GET/PUT /api/me/settings`（服务端时间戳 LWW）；删号/换 token/吊销级联清
- [x] Core DTO + source-gen 往返测试；`ISyncBackend` 增设置读写；四个测试 fake 补齐
- [x] 本地 `settings_sync_state`（迁移 4）+ `ISettingsSyncStateStore`
- [x] `SettingsSyncRunner`：拉后推、本地未推变更不被旧远端覆盖、首同步冲突三选一（未裁决不动两边）、
      中继重建后全量重推、关闭零出站
- [x] 设置页「同步应用设置」开关（仅同步已启用可见、默认关）+ 十语种文案
- [x] 热应用：主题字典、语言、预设清单 + 设置页选择项重投影
- [x] 测试：Relay 端点/校验/级联、Runner 四条语义、Headless 开关可见性与首轮推送
- [x] 文档：selfhost.md 设置同步一节 + known-limitations「旧版中继没有设置同步端点」

## Dependencies

- 既有 relay 同步链路（v0.6.0）

## Development Log

### 2026-11-23 收口

- 线协议：`SyncedSetting(Key, Value, UpdatedAt)` / `SettingsSyncPutRequest` / `SettingsSyncResponse`；
  服务端 PUT 赋 `updatedAt` 并逐 key upsert（客户端不伪造时间戳，避免设备时钟偏移决定谁赢）。
- 中继：`account_settings(account_id, key, value, updated_at)` 幂等建表；GET 返回全部行、PUT 只回显本次写入；
  key ≤64 / value ≤16KB / 单请求 ≤64；删号、换 token 退役、成员吊销三条级联清单各加一行 DELETE。
- 客户端：迁移 4 建 `settings_sync_state(key, value, synced_at)`；`SettingsSyncRunner` 拉后推，
  本地未推变更的 key 不被远端覆盖；首同步（账面为空）先采纳远端；关闭开关零出站。
  同账号关闭→再开启**保留**账面（断网期间的本地改动优先），只有换端点/token 才清空（S8 修订）。
- 设置页：独立开关默认关、仅同步已启用可见；拨开即落盘并立即跑一轮。热应用走
  `SyncStateService.SettingsApplied` → UI 线程：主题字典 / 全局语言 / 预设清单 / 设置页重投影。
- 测试账：Core 282、Relay 81、Infrastructure 845、App 552、Headless 688、Reconciliation 68 全绿。
- 明确不做：模型目录/别名/价格覆盖（计价语义，另案）；当前时间范围档位（视图状态）；逐项开关
  （v1 只有总开关）；E2EE 与设置历史。

### 2026-11-24 审查收口

- 远端 `customTimePresets` 是不可信复合值：`TryWrite` 按添加预设的封闭词表逐条校验（`preset:` 前缀、
  已知 Kind、`IsValid`、去重、上限 6），坏条目丢弃、整值无效拒绝；`ToOption` 接受 null、构造归一空清单，
  `[null]` 不再打崩启动装载。
- `SettingsSyncRunner` 改为「应用 → 先落盘 → 再记账 → 再推送」；落盘失败不推进账面，避免重启后把旧
  磁盘值当本地变更反推覆盖更新的远端；推送失败仍保留已采纳值。
- 空凭据拒绝下沉到服务层；`InitializeAsync` 遇到「已启用但凭据为空」的历史坏状态就地降为关闭并写明
  原因（状态行带出 `SyncCredentialsRequired`），配置区随关闭态恢复可见。
- 首同步冲突不再静默 adopt（审查 #3）：runner 发现远端与本机不同且用户未裁决时停下，设置页显示
  三选一面板（采用云端 / 保留本机 / 本机配置不同步）；裁决值 `SettingsSyncFirstSyncPolicy` 只存本机，
  换账号时复位。选择「本机配置不同步」只关设置同步，用量同步照常。
- 中继删库重建（同凭据、远端全空）按新桶处理：清空设置账面并全量重推本机偏好（审查 #5）。
- 测试补实：三处级联直接断言表行清空、Runner 冲突/新桶/落盘顺序用例、服务层凭据与冲突用例、
  Headless「远端语言到达后设置页选择项跟随」与冲突面板用例。
