---
id: I-043
title: 采集真增量（message-grain 记录账本）——驻留内存从 O(全历史) 降到 O(本轮增量)
type: infra
priority: P2
size: L
status: todo
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

把"已解析记录"从进程内存搬进 SQLite 账本：每条消息落一行（身份 + token 计数 + 时间戳 + 日志自带成本，**无任何内容字段**，隐私红线不破），采集管道每轮只解析新/变更文件 → INSERT OR IGNORE 增量入账 → 流式读出全量记录按天/会话聚合。驻留内存与每轮瞬时分配都从 O(全历史) 降到 O(本轮增量 + 聚合粒度)。

## Background

- 内存调查（2026-10-25 会话）结论：I-042 短路解决"空转"，但**有数据变化的一轮仍要对全历史重建**——7 个 JSONL 扫描器的 `_recordsByFile`（每行 ~0.4 KB 含 MessageId/RequestId/Model 独立字符串）永久驻留，百万行历史 ≈ 400 MB+ 常驻；变更轮的 SelectMany + 去重索引再叠加 0.5–1 GB 瞬时尖峰。Claude Code 重度用户一年日志可达数百万行，这条增长曲线不改造就压不住。
- I-042 已铺好的地基：`FilesChanged` 管线、短路语义、`IAggregationInvalidator` 强制全量通道——本卡在其上把"缓存在内存"换成"缓存在磁盘"。

## 设计要点（实施前需逐条对账）

1. **表设计**：`usage_records`（dedup_key TEXT PRIMARY KEY, agent, session_id, model, provider, project_path, timestamp_ms, input, output, cache_creation, cache_read, ephemeral_5m, ephemeral_1h, is_sidechain, log_cost_usd NULL, source_file）。按 `schema_migrations` 顺序迁移新增（纯增量表，无旧语义行要转换）。
2. **去重语义迁移**：现行 ClaudeUsageRecordDeduper first-wins（按文件路径序 + 行序）→ DB `INSERT OR IGNORE` 按插入序 first-wins。跨轮插入序 = 解析序（文件排序稳定），重复记录实务上内容相等，存活副本不影响 facts——**对账测试必须锁这一点**（Reconciliation.Tests 口径逐项核对）。
3. **管道改形**：扫描器不再返回全量记录（只返回本轮增量 + 诊断）；`LocalIngestionDataSource` 增量 upsert 后流式 SELECT（`SqliteDataReader` 逐行）喂 `UsageFactsMapper`（需增流式聚合重载，会话事实按 session_id 排序分组流水化）。
4. **计价**：`log_cost_usd` 只存日志内建值；补价在聚合期现算（现行 pricedCosts 侧表语义的天然等价——价格更新每轮生效，无需失效旗标）。诊断计数（Duplicate/MissingCost/UnknownModel）从 SQL COUNT 取。
5. **扫描器瘦身后**：`_recordsByFile` 删除，只留 `_fileStates` + `_filesWithParseErrors` + 每文件记录数（注册表计数用）；Copilot 池、Pi `loaded`、Gemini/Amp 整文件读同批处理（Pi/Gemini/Amp 是瞬态峰值型，独立小步）。
6. **非 JSONL 族**：ZCode/Goose/Hermes/Kilo/OpenCode/Antigravity 维持全量重读（万级、账本自持），增量 upsert 由 DB 主键自然幂等；Copilot 池是否也搬 DB 独立评估（其合并需要跨文件中间态）。
7. **磁盘代价**：message-grain 行约为源 JSONL 体积的 10–15%（无内容），1 GB 日志 ≈ 100–150 MB 账本——内存换磁盘的显式交易，卡内明示。
8. **清理路径**：源文件删除 → `DELETE FROM usage_records WHERE source_file = ...`（现行"文件消失即从账面移除"的 DB 等价）。

## Acceptance Criteria（实施时细化）

- [ ] 百万行级 fixture 下：稳态驻留不含历史记录对象；变更轮峰值分配 O(增量)
- [ ] Reconciliation.Tests 全绿（costUSD 优先级 / UTC 分桶 / 去重口径逐项对齐）
- [ ] 既有 I-042 短路语义在其上继续成立（无变化轮连 SELECT 都省）
- [ ] 迁移后旧库可平滑升级（旧语义行不残留）

## Dependencies

- I-042（已收口，本卡的地基）

## Development Log

### 2026-10-25

- 建卡（内存调查产出的深水区设计卡，未动工）。实施前必读 `docs/reconciliation/2026-10-11-reconciliation.md`。
