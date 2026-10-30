---
id: I-043
title: 采集真增量（message-grain 记录账本）——驻留内存从 O(全历史) 降到 O(本轮增量)
type: infra
priority: P2
size: L
status: wontfix
resolution: 作废（用户拍板不做，2026-10-30；卡片留档不退库）
created: 2026-10-25
updated: 2026-10-30
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

### 2026-10-30 作废（用户拍板不做，卡片留档）

用户评估后认为“边界问题很多、可能引入 Bug”，拍板**不再实施本卡**，作为历史档案留在仓库（不删卡、不进 Done 的功能列表语义）。卡片状态记为 `wontfix`。

作废依据（风险集中处，接手前先读完这些）：

1. **去重口径就是信任锚，且它仍在演进。** 现在是内存 first-wins（文件路径序 + 行序），搬进 DB 就变成主键 `INSERT OR IGNORE` 按插入序 first-wins——单轮内等价，**跨轮不等价**（文件失败 / 删除 / 后到，赢家副本会换人）。对账要求 tokens 精确、cost ≤ 0.5%，赢家一变就是差异。且去重键本身 2026-10-30 还在改（`98718c3` claude 消息级键补会话+时间戳、`c6e6931` 跟进）；键一旦变成持久表主键，之后每次键语义变更都是“写迁移 + 洗百万行历史”。`SqliteUsageStore` 的迁移纪律是为聚合表设计的，用在派生账本上是另一件事。
2. **失败语义从“整轮放弃”变成“已入账要回滚”。** 现在源不可信就整源降级 Partial、facts 不动，成本极低；有了跨轮持久账本，每个降级分支都要回答“上一轮入账的行要不要撑”。而这类降级是常态（`97479af` codex 压缩 rollout 损坏、`76ad97c` antigravity 根误读、`edecddc` kimi 计价链）。配套边界面很宽且每条都要 fixture：日志截断、轮转、同大小不同内容的文件替换、mtime 回退、时钟回退、同一 message id 出现在两个文件、文件删除后重建。
3. **计价搬到聚合期现算，要重新证明逐分钱一致。** `log_cost_usd` 优先级、`-fast`/`flex` 倍率、kimi 时间切点都依赖“记录产生时刻 + 档位”；而整套倍率表 2026-10-30 刚落地（`5a9bd6d`…`03c7102`），上面再叠一层“先落账、聚合期再算”等于把刚验证过的口径再证一遍。
4. **可能反而更慢。** 现在零变更轮 12–15 ms 靠内存引用；账本是 message-grain，行数比 `usage_facts` 高一个数量级，每轮要全表流式读 + 反序列化才能聚合。而 perf 调查已证明全表读就是主成本（规模 B 下 facts 全表读 339 ms）——这个改造是“内存省了、每轮 CPU 更贵”，收益为正需要真到那个规模。
5. **隐私口径要改措辞。** 账本行带 `project_path`、会话级信息，颗粒度比“聚合事实”细。数据本身不含 prompt/代码（红线不破），但 AGENTS.md / README 里“SQLite 只存聚合事实”的措辞得跟着改。

其他事实：改动面是横切的——`_recordsByFile`（`IncrementalJsonlScanner.cs:17`）是单一字典被 7 个 JSONL 扫描器共用，口径错一次全源错。

**若日后真需要这个收益（重开条件见 `docs/perf-investigation-2026-10-28.md`：记录数上万 **且** 频繁出现“单源变化触发全量轮”；当前实测 1–2k 记录、全量重算 < 50 ms），实施顺序约束：第一步只做“驻留记忆窗口化”（只留最近 N 天 + 冷源惰性），口径不变、可逆、不碰去重键；那步也不够，再考虑全量账本。**
