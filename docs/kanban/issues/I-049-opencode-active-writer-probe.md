---
id: I-049
title: OpenCode 活跃写端期全库重读——移植 ZCode 两级探针 + 持久只读连接
type: infra
priority: P3
size: M
status: done
created: 2026-11-13
updated: 2026-11-14（done）
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

- [x] 空闲轮（文件指纹未动）：`ProbeQueryCountForTests` 不增、`FilesRead == 0`、`FilesChanged == 0`、
      记录与上轮同实例复用（现有 `ChangeProbe_Unchanged*` 用例继续绿）
- [x] **关键收益锁**：只写与 usage 无关的表（fixture 里 `INSERT INTO kv ...` 或等价方式，使 `-wal`
      变化但 `session_message` 的行数与 rowid 不变）→ SQL 探针实跑 1 次、探针值与本轮记忆一致 →
      `FilesChanged == 0`、`FilesRead == 0`（今日行为是全库重读，本卡后为零读取）
- [x] 活跃轮（仅 `-wal` 变化，数据未变）：SQL 探针实跑 1 次，探针值与本轮记忆一致 →
      `FilesChanged == 0`、`FilesRead == 0`（**和 ZCode 一样：探针说没变就不读**）——
      这条是本卡的核心收益锁，必须配"仅 touch `-wal`"的测试
- [x] 真有新消息（`session_message` 行数变化）→ 全量重读，记录数增加，`status` 口径不变
- [x] 持久连接身份校验：库文件被替换 / 删除 → 弃连重开，删除库的贡献不残留（测试见 ZCode 同款）
- [x] 探针失败两分支（2026-11-14 审查后改写为与实现一致的口径）：**表缺失 ≠ 探针失败**——缺失表
      按 0/0 参与元组、优雅降级（本机库是 v2 形态，v1 `message` 不存在是常态）；**"打不开 / 查询
      失败"才是探针失败** → 保守全量、不抛异常，`status` 由既有读取面降级口径决定（读取也失败 →
      Partial/NoData；读取本身成功 → Ok，`ChangeProbe_ProbeFailureWithoutRowid` 断言 Ok——探针失败
      本身从不标 problems）
- [x] 全量 `dotnet test Tokenmetry.sln` 0 失败；`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`
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

### 2026-11-14 实现

- 代码（`src/Tokenmetry.Infrastructure/OpenCodeSqlite/OpenCodeSqliteAdapter.cs`）：照抄
  `ZCodeSqliteAdapter` 的两级探针 + 持久只读连接（卡内定案 1-3，注释互相指向、未抽公共件）。
  ① 一级文件指纹快路径：db + `-wal` 的 (size, mtime) 与"干净指纹"一致 → 复用记忆探针值，零 SQL；
  ② 二级 SQL 探针 `SELECT COUNT(*), COALESCE(MAX(rowid), 0)`：v2 `session_message` 恒探、
  v1 `message` 表存在才探，缺失表按 0/0 参与探针元组（`DatabaseProbe` record struct）——本机库
  v2 形态下 v1 不存在是稳定态而非探针失败（开放问题落地）；探针失败（打不开 / 查询抛错，如
  WITHOUT ROWID 表无 rowid 列）返回 null → 保守全量。持久只读连接 + `PRAGMA cache_size=-32768`，
  每轮复用前校验 db 文件身份 (size, mtime)，不符弃旧开新；读取面（v1/v2 扫描、fork 剔除、
  按 message id 去重、稳定排序、status 判定、FilesChanged 口径、零变更轮诊断）逐位未动。
- **与 ZCode 的唯一刻意差异（正确性 > 优化）**：`ProbesUnchanged` 加覆盖率守卫——探针集 ≠ 本轮
  库集即判变化、保守全量。ZCode 无此守卫仍正确，因其探针失败 ⇒ 同库读取必失败 ⇒ 不产出记录；
  OpenCode 存在"探针失败但读取成功"的形态（WITHOUT ROWID 表），无守卫会让这类库首轮之后永驻
  缓存、新增行永远不可见。fixture 测试锁定该路径：探针失败每轮 → 每轮保守全量（与 FileStampMemo
  时代"打不开的库不落账"同口径）。
- 测试（`tests/Tokenmetry.Infrastructure.Tests/OpenCodeSqlite/OpenCodeSqliteAdapterTests.cs`）新增 5 例，
  全部临时目录 fixture、未触碰真实 `~/.local/share/opencode`：
  `FileFingerprint_UnchangedSecondScan_SkipsProbeQueryEntirely`（空闲轮零 SQL + 记录同实例）、
  `FileFingerprint_WalTouchedByUnrelatedTable_RunsProbeOnceAndSkipsReread`（核心收益锁：WAL 模式
  真实 -wal，写端保持连接 + 写与 usage 无关的 kv 表 → 探针实跑 1 次 → 零读取，下轮快路径恢复）、
  `ChangeProbe_InsertedSessionMessageRow_TriggersFullReread`（新 assistant 行 → 全量、status 不变）、
  `PersistentConnection_ReplacedDatabaseFile_IsReopenedAndReread`（同路径替换库 → 身份校验弃连重开、
  旧库贡献不残留；显式错开 mtime 防粗粒度文件系统）、
  `ChangeProbe_ProbeFailureWithoutRowid_FallsBackToFullRereadEveryRound`（探针失败 → 每轮保守全量、
  不抛异常、status 不标问题）。
- 实测：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 0 失败
  （Infrastructure 833 passed / 3 skipped 均为既有跳过；Core 271、App 506、Headless 666、
  Reconciliation 49、Relay 50 全绿）。
- SmokeScan 真机三轮（`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`，零副作用）：
  首轮 `[OpenCode] done in 47 ms — files discovered/read/changed: 1/1/1, records: 1013, status: Ok`；
  空闲轮 `1/0/0, records: 1013`（0–1 ms，零读取零 SQL，记录复用）——首轮记录数 1013 与卡内
  Background 基线一致，空闲轮行为与今日逐位一致。

### 2026-11-14 审查修复（PASS-with-notes 落地）

- **P1（部分读取 × 下轮删库漏洞，必修）**：审查者推演确认——轮 k+1 某库探针失败/值变化 → 全量
  读取 → 该库读取中途抛异常 → records 留下半截行、readMemory 只收健康库 → 轮 k+2 用户删掉坏库 →
  库集收缩后 `probes.Count == discoveredCount == _lastProbes.Count` 且逐值相等 → 判零变更，复用
  混有已删库残行的记录集——违反 `ProbesUnchanged` 声明的"库集收缩必判变化"不变式（HEAD
  FileStampMemo 全或无准入在同序列下必然全量重读，本卡初版的逐库准入引入保守性回退）。
  **修复（按审查建议落地）**：记忆落账改回**全或无准入**——仅当 readMemory 覆盖本轮库集（全部库
  探针成功且读取成功）才整体落账四件套（`_lastProbes` / `_cleanFileFingerprints` / `_lastRecords` /
  `_lastDiagnostics`）；部分读取轮（readMemory.Count < 库集，读失败与探针失败同属部分轮）整体作废
  记忆，下轮必全量重读；健康路径（全库读成功）行为不变。覆盖率守卫保留（新增库探针失败时库集
  增长的场景仍靠它拦截）。回归测试
  `ChangeProbe_PartialRound_ThenFailingDatabaseRemoved_DoesNotServeStaleCache`：fixture 用"探针失败
  （WITHOUT ROWID）但读取成功"的库 B 构造部分轮（B 的行全进 records、B 不进记忆；真"读取中途抛
  异常"需物理页破坏，实测 writable_schema rootpage 改指向会让整个库 schema 加载失败、无法产生半截
  行，故取探针失败构造——两者在准入分支同属部分轮），删库轮断言 FilesChanged=1 / FilesRead=1 /
  B 残行不出现。**已实证修复前该测试失败**（临时恒真模拟准入：删库轮判零变更 FilesChanged=0）。
  **ZCodeSqliteAdapter 有同款漏洞（本卡移植源头），登记待另卡处理、本卡不越界顺手修（是否开卡由
  用户决定）。**
- **P2-1**：`PersistentConnection_ReplacedDatabaseFile_IsReopenedAndReread` 与上述 P1 回归测试加
  Windows 跳过（适配器持有未关闭持久句柄时 File.Delete 撞共享锁；POSIX unlink 允许带 fd 删除）。
  既有 `ChangeProbe_RemovedDatabase_DoesNotServeStaleCache` 在 Windows 下有同款暴露（与 ZCode
  `ChangeProbe_RemovedDatabase_DetectedAsChangeAndRescanned` 的既有暴露一致），未顺手改。
- **P2-2**：`_readOnlyConnections` 字段注释补生命周期说明：被删库的缓存连接不主动清理（库从发现
  清单除名无 Drop 路径），依赖同路径重现（身份不符弃连）或进程退出回收；DI 单例语义下句柄数
  ≤ 历史库路径数，可接受；不补 IDisposable（ZCode 同款缺口，审查登记不修）。
- **P2-3a**：AC-6 改写为两分支口径（表缺失 ≠ 探针失败；打不开/查询失败才是探针失败，status 由
  既有读取面降级口径决定）。
- **P2-3b**：登记——**"usage 行只增不改"对 OpenCode 是假设而非取证**：探针对行原地改值（count 与
  max(rowid) 不动，如 fork 边界元数据原地修正且无新拷贝行）失明，与 ZCodeSqliteAdapter 登记的
  同款边界；存量数据口径已由 Reconciliation.Tests 的 golden JSON 隐式覆盖。验证途径：真机对账
  （`npx ccusage@20.0.29`，tokens 精确、cost ≤0.5%）+ I-052 读取量可观测落地后的日志核对；若日后
  发现 OpenCode 存在行原地覆盖写行为，重开卡评估探针表达式。
- 实测：`dotnet test Tokenmetry.sln` 2376 passed / 0 failed（Infrastructure 834 = 审查前 833 + 本
  回归测试）；SmokeScan 三轮 OpenCode：首轮 124 ms `1/1/1, records: 1013, status Ok`、空闲轮 0–1 ms
  `1/0/0, records: 1013`，与审查前行为一致。
