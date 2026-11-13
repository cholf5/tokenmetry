---
id: I-049
title: OpenCode 活跃写端期全库重读——移植 ZCode 两级探针 + 持久只读连接
type: infra
priority: P3
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

OpenCode 在**被用户使用期间**（写端活跃）不再每轮把整个会话库重读重映射一遍：空闲轮保持现状
（文件指纹快路径，零 SQL），活跃轮把成本压到"轻量聚合查询 + 常驻页缓存"，并让
"本轮到底读了多少行"可观测。

## Background（已对照源码核实）

- `OpenCodeSqliteAdapter.cs:73`：`FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(...))`
  —— 指纹 = 主库 + `-wal` 的 (mtime, size)。写端每提交一次消息，`-wal` 必变 → 指纹必变 →
  走全量读取。**这是设计上的"活跃期必然全量"，不是漏判**：指纹只能回答"变没变"，不能回答"变了多少"。
- 活跃轮的成本（`ReadDatabase`，`OpenCodeSqliteAdapter.cs:163` 起）：每轮 `using var connection =
  SqliteReadonly.Open(...)` 新开连接（`SqliteSource/SqliteReadonly.cs:13`，无 `cache_size` 设置），
  然后 `SELECT id, session_id, data FROM message`（v1）+ `SELECT ... FROM session_message`
  （v2，含 `type='assistant'` 过滤）+ fork 边界查询，逐行 lenient JSON 映射。
- **本机实测（macOS，真实数据）**：
  - `~/.local/share/opencode/opencode.db` = 43.7 MB；`session_message` 1094 行，其中
    `type='assistant'` 1016 行；这 1016 行的 `data` 载荷合计 **28.9 MB**（`SELECT ... ` 导出实测）；
  - CLI 侧全量导出 53 ms，`COUNT(*) WHERE type='assistant'` 4 ms；
  - 仓库 `tools/Tokenmetry.SmokeScan` 首轮该源 **132 ms / 1013 records / 1 库**，空闲轮 0 ms。
  - 结论：活跃期每个刷新 tick（默认 5 分钟，S-007 后）× 一轮 ≈ 一次 29 MB 载荷读取 + 1016 次
    JSON 解析 + 一轮全量映射。绝对值不吓人，但它是空闲成本的 100 倍量级，且随会话线性增长。
- 参照实现（本仓已有，勿另起炉灶）：`ZCodeSqliteAdapter` 的两级探针 + 持久只读连接——
  `ZCodeSqliteAdapter.cs:115-160`（指纹快路径 → SQL 探针 → 零变更短路）、`:305` `TryProbe`、
  `:331` `GetReadOnlyConnection`（`:358` `PRAGMA cache_size=-32768`）、`:415` `TryGetFileFingerprint`、
  `_cleanFileFingerprints` 的**方向性约束**（只许记"早于或等于探针所见数据态"的指纹）、
  `_connectionIdentities` 的建连身份校验（POSIX 下已删除文件的 fd 仍可读，不校验会"永远探针成功"）。
  测试参照 `tests/Tokenmetry.Infrastructure.Tests/ZCodeSqlite/ZCodeSqliteAdapterTests.cs:192-233`
  （`ProbeQueryCountForTests` 断言"指纹未动 → 零 SQL"）。

## 定案方向

1. **照抄 ZCode 的两级结构，探针表达式按 OpenCode schema 定制**（不自创第二套算法）：
   - 一级：文件指纹快路径（主库 + `-wal` 的 size/mtime）与"干净指纹"一致 → 复用记忆探针值，零 SQL；
   - 二级：`SELECT COUNT(*), COALESCE(MAX(rowid), 0) FROM session_message`（必要时对 v1 `message`
     表同样一条，两值拼进探针元组）。选 `rowid` 而不是业务 `id`：`id` 是文本、非单调，作变更信号不可靠。
   - 探针值与本轮库集完全一致 → 复用上轮记录（`FilesChanged = 0`，管道短路），本轮 `FilesRead = 0`。
2. **持久只读连接 + 连接级页缓存**：库路径 → `SqliteConnection` 缓存，打开时
   `PRAGMA cache_size=-32768`；每轮复用前校验库文件身份 (size, mtime)，不符即弃旧开新。
   这是把"活跃轮 29 MB 载荷重读"变成"页缓存命中后再解析"的关键一步（省的是磁盘读，不是解析）。
3. **正确性口径不变**：探针只是优化信号，打不开 / 查询失败 → 保守全量，绝不影响扫描结果。
   记录集与诊断口径与今日逐位一致（`records` 排序、`DuplicateRecords`、`status` 判定不动）。
4. **可观测**：探针实跑次数做成 internal 计数（`ProbeQueryCountForTests` 同名先例），
   供测试断言快路径；若 I-052 落地，活跃轮的"读取行数"应能在 `app.log` 里看到。
5. **不抽公共件**：不把 ZCode 的探针抽成共享组件（会让本卡改动面扩到最大源，风险与收益不匹配；
   且同批 I-050 要动另一个适配器，抽公共件会造成并行卡撞文件）。重复代码以注释互相指向。
6. **明确不做**（重要，别越界）：
   - **不做行级增量**（只读 `rowid > 上次最大值` 的新行）——去重口径会从"整轮 first-wins"变成
     "跨轮拼接"，与 I-043 被否的理由同源（信任锚：tokens 必须精确、键仍在演进）；
   - 不引入 `-shm` 指纹（读端也会触碰，会自激重扫）；
   - 不改文案/UI，不动其它适配器。

## Acceptance Criteria

- [ ] 空闲轮（文件指纹未动）：`ProbeQueryCountForTests` 不增、`FilesRead == 0`、`FilesChanged == 0`、
      记录与上轮同实例复用（现有 `ChangeProbe_Unchanged*` 用例继续绿）
- [ ] **关键收益锁**：只写与 usage 无关的表（fixture 里 `INSERT INTO kv ...` 或等价方式，使 `-wal`
      变化但 `session_message` 的行数与 rowid 不变）→ SQL 探针实跑 1 次、探针值与本轮记忆一致 →
      `FilesChanged == 0`、`FilesRead == 0`（今日行为是全库重读，本卡后为零读取）
- [ ] 活跃轮（仅 `-wal` 变化，数据未变）：SQL 探针实跑 1 次，探针值与本轮记忆一致 →
      `FilesChanged == 0`、`FilesRead == 0`（**和 ZCode 一样：探针说没变就不读**）——
      这条是本卡的核心收益锁，必须配"仅 touch `-wal`"的测试
- [ ] 真有新消息（`session_message` 行数变化）→ 全量重读，记录数增加，`status` 口径不变
- [ ] 持久连接身份校验：库文件被替换 / 删除 → 弃连重开，删除库的贡献不残留（测试见 ZCode 同款）
- [ ] 探针失败（表缺失 / 打不开）→ 保守全量，`status` 按既有降级口径（`Partial`），不抛异常
- [ ] 全量 `dotnet test Tokenmetry.sln` 0 失败；`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`
      该源首轮/空闲轮行为与今日一致

## 开放问题

- v1 `message` 表在本机库上不存在（该库是 v2 形态）。探针必须对"表不存在"优雅降级：
  只探存在的表，缺失表按 0/0 参与元组（否则老库每轮都被判"探针失败 → 全量"）。
- 持久连接会不会拖住写端 checkpoint：SQLite 只在**读事务**未结束时阻塞 checkpoint，空闲连接不持有
  快照。ZCode 已用同一手法运行（`:331` 注释），本卡沿用即可；若真机观察到 OpenCode 的 `-wal`
  无限增长，先弃持久连接再排查（卡内留此回退路径）。

## Dependencies

- 无硬依赖。文件面只有 `OpenCodeSqlite/` + 其测试文件，可与 I-048 / I-050 并行。
- 建议阅读 `ZCodeSqliteAdapter.cs` 相关段落（`_cleanFileFingerprints` 的方向性约束必须先读懂）。

## Related

- I-009（OpenCode 适配器来源卡）、I-006（ZCode SQLite 适配器 + 探针先例）
- I-042（无变化轮次短路的来源卡）、I-043（行级增量被否的记录——本卡"不做"的依据）
- I-049 / I-050 同批：Kilo 同型适配器
- I-052（读取量可观测，验证本卡收益的量尺）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 调研结论：五个 SQLite 源里，Goose / Hermes 读的是**每会话一行的聚合表**（`FROM sessions`），
  单轮全量重读是毫秒级，不值得改（见 I-051 落档）；真正有载荷表的只有 OpenCode 与 Kilo。
  OpenCode 在本机有真实数据（43.7 MB / 1016 条 assistant / 29 MB 载荷 / 132 ms），故本卡是
  这一批里唯一有真机量级支撑的性能卡。
- 优先级 P3 而非 P2：默认刷新已降到 5 分钟（S-007），活跃期增量成本 ≈ 132 ms / 5 分钟，
  用户可感知度低；本卡的价值是"让增长曲线不回来"（会话数线性增长）与顺手补上读取量可观测。
- 明确划了三条边界进卡：不做行级增量（I-043 同源风险）、不抽公共件（并行卡撞文件）、
  `-shm` 不进指纹（自激重扫）。
