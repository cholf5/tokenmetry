---
id: I-042
title: 无变化轮次短路——零数据变化时跳过计价/聚合/落库，扫描器零变更轮复用去重产物
type: infra
priority: P1
size: M
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

后台采集刷新在"本轮所有源均无数据变化"时短路：跳过补价、facts 聚合、落库、旧分解行清理，只保留 facts 读回。扫描器侧零变更轮直接复用上一轮去重产物，不再对全历史做 SelectMany + 消息级去重重建。

## Background

- 内存调查（2026-10-25 会话）实测：稳态 Private ~230–280 MB、托管堆 35 MB——本机几乎无日志数据即可达此水位；且默认刷新间隔下每分钟跑一轮完整管道，即使 19 源全部无变化（文件 mtime/大小未动、账本无新行）。
- 每轮的浪费构成：7 个 JSONL 扫描器对全历史 `SelectMany().ToList()` + 全量去重索引重建（历史数据量大时为 0.5–1 GB 级瞬时尖峰）→ 全量记录集走补价字典 → 全量 facts 聚合 → upsert。文件账面未动时这些产物逐位相同。
- 语义前提（I-037 定案"改价格即生效"）：价格/别名/目录热更新后必须全量重算——短路必须给这条路径留强制全量通道，否则改价在无变化轮被短路吞掉。

## 定案

- `JsonlScanDiagnostics.FilesChanged: int?`——本轮新增/变更/消失的文件数；**null = 该适配器不追踪变更（保守按有变化走全量）**。共享扫描器与 Copilot 自有记账填 0/N，ZCode 以探测探针填 0/1，其余适配器不填（null）。
- 数据源级判定：`FilesChanged == 0` 显式无变化；`FilesChanged == null` 且 `FilesDiscovered == 0 && Records.Count == 0` 视为无变化（本轮发现即空，上一轮也必然空——文件发现是每轮现跑的，不可能"这轮有文件但计数为 0"）。其余（含全部 null 但有数据的账本/整文件族）保守走全量。
- 全部源无变化 → 跳过补价/聚合/落库/清理，直接读 facts 返回；**facts 读回为空则落穿全量管道**（库被外部清空/迁移异常时自愈重建）。
- 扫描器零变更轮复用缓存去重产物（同实例列表），诊断重建（本轮 FilesRead/Malformed/Invalid 归零，累计四项与 Status 沿用缓存值——与现行"零变更轮重算全历史"的口径逐位一致）。
- Copilot 适配器同构：零变更轮复用上轮合并结果（跳过 Reconcile 全池重算）。
- ZCode 探针：`COUNT(*) + COALESCE(MAX(id),'')`（completed 行）按库记忆，未变 → FilesChanged=0；探针失败保守 null。已知边界：账本行**原地改值**（token 修正、count 与 max id 不动）不会被探针捕获——ZCode usage 行实务上只增不改（写入方账本语义），且下次任何新行/删除都会触发全量；设置页重扫按钮此时不构成强制（属已知接受，真需要时删 `%LOCALAPPDATA%\Tokenmetry` 重装或等下一行数据）。
- I-037 契约通道：新 `Core/Abstractions/IAggregationInvalidator`，`LocalIngestionDataSource` 实现（`InvalidateAggregation()` 置一次性强全量旗标，仅在全量管道**成功完成**后清除——中途失败保留，下轮重试全量）；`DataPricingViewModel.TriggerReprice`（价格/别名两个公共出口的共同汇点）先失效再触发刷新。models.dev 远程快照更新的生效时机从"下一轮"变为"下一次有数据变化的轮次"（远程快照 24h TTL，活跃使用下无差异；卡内如实登记）。

## Acceptance Criteria

- [x] 扫描器：第二次无变化扫描 FilesChanged=0 且去重委托只被调用一次（注入计数委托）；文件追加后 FilesChanged=1 且去重重跑；文件删除后 FilesChanged≥1
- [x] ZCode：第二次扫描（库未动）FilesChanged=0；插入新行后 FilesChanged=1
- [x] Copilot：第二次无变化扫描 Records 同实例复用（Reconcile 不重跑）、FilesChanged=0
- [x] 数据源：无变化轮不重计价（改覆盖价后不失效 → facts 维持旧价；`InvalidateAggregation()` 后一轮 → 新价生效，I-037 回归锁迁移到新语义）；store 清空后无变化轮自愈重建
- [x] 既有 `GetDailyUsage_RepricesRecordsWhenOverridesChange` 更新为显式失效语义（旧编码即"每轮全量"假设，短路后必须迁移）
- [x] 设置页数据源表快照（注册表逐源登记）在短路轮照常刷新时间戳与计数
- [x] 全量测试绿

## Dependencies

- none（原设想的后续深水区 I-043（真增量账本）已于 2026-10-30 由用户拍板作废，卡片留档；本卡不依赖任何未完成项）

## Development Log

### 2026-10-25 收口

- 改动面：`JsonlScanResult.cs`（FilesChanged 字段）、`IncrementalJsonlScanner`（零变更缓存 + 计数）、`CopilotSessionAdapter`（零变更结果缓存 + 计数）、`ZCodeSqliteAdapter`（探针记忆）、`LocalIngestionDataSource`（短路判定 + IAggregationInvalidator）、`AppServices`（接口注册）、`DataPricingViewModel`（TriggerReprice 失效钩子，可选注入兼容既有测试构造）。
- 测试 +10：扫描器 4（无变化复用/变更重跑/删除计变/空清单两连轮）、ZCode 1（三连扫描 1→0→1）、Copilot 1（Records 同实例复用）、数据源 3（短路保旧价+失效生效 / store 清空自愈 / 注册表短路轮照常登记）、App.Tests 1（别名热更新失效钩子接线）；既有 RepricesRecordsWhenOverridesChange 迁移为显式失效语义。全量 1571 Passed / 0 Failed（19 跳过）。
- 实测预期（本机）：托盘闲置态每轮成本从"19 源扫描 + 全历史去重 + 全量聚合落库"降到"文件系统 stat + 探针查询 + facts 读回"；ZCode 活跃写入期仍全量（账本真变了，必要工作）。
- 有意不做：不改变任何 facts/records 数据语义（对账锚）；不给其余 10 个无记账适配器补变更追踪（保守 null 已足够，收益属 I-043）；不动 `RefreshMinutes`（S-007 独立卡）。
