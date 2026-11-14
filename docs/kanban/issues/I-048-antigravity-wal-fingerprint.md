---
id: I-048
title: Antigravity 指纹补 -wal——活跃写入期不再读到陈旧账
type: bug
priority: P2
size: S
status: done
created: 2026-11-13
updated: 2026-11-14（done）
blocked: none
---

## Goal

让 Antigravity 源的零变更判定看到 SQLite 的 `-wal` 边车，避免"写端正在提交、主库文件还没被
checkpoint 回写"时复用到上一轮的旧记录（表现为界面数据滞后，直到 checkpoint 或应用退出才追上）。

## Background（已对照源码核实）

- `src/Tokenmetry.Infrastructure/Antigravity/AntigravityAdapter.cs:44`：`FileStampMemo.Capture(databases)`
  —— 只对**主库文件**取 (mtime, size)。
- 同类另外四个 SQLite 源都用了 WAL 感知的指纹：`GooseSqliteAdapter.cs:70`、`HermesSqliteAdapter.cs:70`、
  `KiloSqliteAdapter.cs:56`、`OpenCodeSqliteAdapter.cs:73`（勘误：I-049 后 OpenCode 已改逐库两级探针，
  行号与写法均为建卡时点快照），都是
  `FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databases))`
  （`FileStampMemo.cs:114` 定义 = 主库 + `-wal`；`FileStampMemo.cs:122` 的 `CaptureWithOptional`
  对"边车本轮不存在"取哨兵戳，缺失状态本身进指纹）。
- WAL 语义：写入先追加到 `-wal`，**主库 mtime 在 checkpoint 之前不变**；SQLite 默认自动 checkpoint
  阈值是 1000 页（约 4 MB 累计），低频写入的库可以长时间不动主库。于是：
  ① 主库没动 → `TryReuse` 命中 → 本轮零读取，新提交的用量看不到（滞后）；
  ② 一旦 checkpoint 落盘 → 主库 mtime 变 → 突然重读并跳变（数据"迟到"）。
- Antigravity 解析路径（`AntigravityParser.ParseSqliteFile`）走只读连接，能读到 WAL 里已提交的数据
  —— 所以缺的只是"何时该重读"的信号，不是读不到。

### 先决取证（已执行，2026-11-14：结论 = **WAL**，证据等级 A）

本机没有安装 Antigravity（复核记录见 Development Log），无法直接 `PRAGMA journal_mode`，改按多源
拼证据执行，三路取证如下：

**① 公开一手实测（两份独立来源、互相印证）：**

- `google-antigravity/antigravity-cli` issue
  [#1486](https://github.com/google-antigravity/antigravity-cli/issues/1486)（open，2026-11-09，
  CLI 1.5.3 / Windows 11，ETW 内核文件写入追踪）：原文摘录——"130.8 MB was written to
  `<id>.db-wal` in about 10 minutes, and only 6.2 MB to `<id>.db`"、"`PRAGMA page_size` = 32768
  and `wal_autocheckpoint` = 1000, so the WAL reaches 32 MB before reset. Leftover WALs are
  32.8-33.1 MB (related: #683)"。路径 `~/.gemini/antigravity-cli/conversations/` 恰为本适配器
  六默认根之一；关联 #683（运行后遗留 WAL/SHM 文件）、#1447（Antigravity 自己的 hub 读循环
  "re-scans on WAL writes"——官方也拿 WAL 写当变更信号）。
- Google AI Developers Forum
  [主题 187421](https://discuss.ai.google.dev/t/antigravity-is-quietly-writing-30-gib-day-to-my-ssd-a-wal-level-breakdown-of-where-it-goes/187421)
  （2026-10-08，IDE 2.19.1 / Windows 11）：作者对 `~/.gemini/antigravity/conversations/` 的
  90 MB 会话库"walked its WAL frame by frame — validating salt, rolling checksums and commit
  boundaries (1,188/1,188 frames valid)"——WAL 帧结构（salt / 滚动 checksum / commit 边界）
  只有真 `-wal` 文件才有，rollback journal 库无此物；并按 WAL 帧归属统计 gen_metadata 48.3% /
  steps 17.2% / steps 索引 27.5%。作者诉求之一"Bound the WAL with periodic checkpointing"侧面
  证实官方未做主动 checkpoint。

**② 蓝本仓核对（本地 `/Users/theo/dev/cholf5/token-usage/ccusage/`）：**

- `rust/adapters/antigravity/` 全目录 grep `-wal` / `journal_mode` / `pragma`：零 WAL 提及，
  唯一 PRAGMA 是 `parser.rs:286` 的 `PRAGMA page_count`（空库检查，I-021 已移植）；打开方式
  `parser.rs:146` `Connection::open_with_flags(path, OpenFlags::new().with_read_only())`（真只读，
  与我们 `SqliteReadonly.Open` 同款，兼容运行中的 WAL）。
- 旁证两条（间接）：ccusage git 历史 #1774 "skip zero-byte conversation databases"（commit
  d41bf3d4，零字节主库 = WAL 未 checkpoint 的经典形态）与 #1992 "skip unreadable conversation
  databases"（commit 65c58374，并发写期读失败跳库）；另 zcode 适配器有 WAL 读取测试
  `rust/adapters/zcode/src/loader.rs:467 reads_from_wal_database_without_changing_journal_mode`，
  说明蓝本对 SQLite 源 WAL 形态有意识，只是 antigravity 侧没有留文字记录。

**③ 本机复核（2026-11-14）：** `~/.gemini/antigravity*`、`~/.config/antigravity`、
`~/Library/Application Support/Antigravity`、`~/Library/Caches/Antigravity`、/Applications 均不存在；
`mdfind -name antigravity` 只命中 Python 彩蛋 `antigravity.py` 等无关文件；SmokeScan 该源
`0/0/n/a`。维持"无法直接 PRAGMA"的判断。

**裁决：证据等级 A（两份独立作者的一手 WAL 实测互证 + 蓝本仓两条间接旁证），按定案方向实施。**

## 定案方向

1. `AntigravityAdapter.ScanAsync` 的指纹改为
   `FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databases))`——
   与另外四个 SQLite 源逐字同口径，不自创第二种指纹。
2. 类注释同步：说明 `-wal` 进指纹的理由（写入先落边车）、`-shm` 为什么不进（读写共享的派生索引，
   读端也会触碰，进指纹等于自激重扫）。
3. 测试（`tests/Tokenmetry.Infrastructure.Tests/Antigravity/AntigravityAdapterTests.cs`，沿用现有
   `ChangeProbe_*` 家族命名）：
   - **仅 `-wal` 变化必须重读**：库内容一字不动，只把 `<db>-wal` 的 mtime（必要时加上长度）改掉
     → `FilesChanged != 0` 且 `FilesRead == 1`。这条是回归锁：改回 `Capture` 必 RED。
   - **`-wal` 消失（checkpoint 收尾）也是账面变化**：删掉边车 → 必须重读一轮（`CaptureWithOptional`
     的哨兵戳语义），不能因为"主库也没动"就复用。
   - 既有 `ChangeProbe_UnchangedSecondScan_SkipsRereadAndReusesCachedRecords` 必须继续绿
     （两轮之间文件完全不动 → 仍然零读取）。
4. 明确不做：不引入探针（COUNT/MAX 查询）、不引入持久只读连接——本卡只是把指纹口径补齐；
   Antigravity 的活跃期全量重读成本若值得优化，另开卡（参考 I-049 的形态）。

## Acceptance Criteria

- [x] 取证结论（journal_mode）写进本卡 Background，WAL 才实施
- [x] 指纹改为 `SqliteFingerprintFiles` + `CaptureWithOptional`，与另外四源同口径
- [x] 新增「仅 `-wal` 变化 → 重读」回归测试（改回旧写法必 RED）
- [x] 新增「`-wal` 消失 → 重读」测试
- [x] 既有零变更轮测试零改动通过；`dotnet test Tokenmetry.sln` 全绿
- [x] 类注释写明 `-wal` 进、`-shm` 不进的理由

## 开放问题

- Antigravity 的 checkpoint 行为：它是长期驻留的应用，若退出时才 checkpoint，则本卡影响的是
  "使用中数据滞后"（明显）；若它常驻自动 checkpoint，影响只剩"每 N MB 一次迟到跳变"（轻微）。
  取证时顺手记一句，决定是否把优先级从 P2 降到 P3。
  → **取证已答（2026-11-14，优先级维持 P2 不降）**：antigravity-cli#1486 实测
  `wal_autocheckpoint` = 1000 页 × 32 KB 页 = **32 MB 累计才触发一次自动 checkpoint**（远超卡内
  假设的"1000 页 ≈ 4 MB"——页大小 32 KB 是 Antigravity 自设的），且 run 结束后遗留 32.8–33.1 MB
  的 WAL 不清（#683），论坛帖（IDE 侧）作者直接以"Bound the WAL with periodic checkpointing"为
  诉求、官方未内建主动 checkpoint。即活跃会话期间主库可以长时间完全不动（#1486 实测 10 分钟主库
  仅 6.2 MB 而边车 130.8 MB），"使用中数据滞后"是主要影响面，P2 成立。

## Dependencies

- 无（文件面只有 `Antigravity/` + 其测试文件，可与同批其它卡并行）。

## Related

- I-021（Antigravity 适配器来源卡）
- I-042（无变化轮次短路 / 文件戳记忆化口径的来源卡）
- I-006（ZCode SQLite 适配器，SQLite 读取口径先例）
- I-049 / I-050（同批：OpenCode / Kilo 活跃期全量重读）

## Development Log

### 2026-11-14 取证 + 实施（证据等级 A → 按定案方向落地）

- **取证过程**见上方「先决取证」小节：蓝本仓 grep/git 历史 → 公开一手实测两源（antigravity-cli#1486、
  Google AI Dev Forum 187421）→ 本机复核。裁决 WAL、等级 A，触发分支一。
- **实施**：`src/Tokenmetry.Infrastructure/Antigravity/AntigravityAdapter.cs` 指纹行改为
  `FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databases))`（与其余三个
  FileStampMemo 系 SQLite 源（Goose/Hermes/Kilo）逐字同口径；OpenCode（I-049）为语义同口径的逐库
  两级探针——I-049 提交 9c31970 时序上晚于本卡措辞，特此勘误）；类注释补 `-wal` 进（WAL 先落边车、
  主库 mtime 在 checkpoint 前不动，引 #1486 实测
  32 MB 阈值）与 `-shm` 不进（读写共享派生索引，进指纹自激重扫）的理由。
- **fixture 先行验证**（临时 scratch 工程，未入库）：Microsoft.Data.Sqlite 10.0.13 下
  `journal_mode=WAL` + `BEGIN IMMEDIATE`/`ROLLBACK` 会落出一个真实存在的空 `-wal` 且主库戳不动；
  只读连接（`SqliteOpenMode.ReadOnly`）在"边车在"与"边车消失"两态都可读——据此设计 WAL fixture。
- **新测试**（`tests/Tokenmetry.Infrastructure.Tests/Antigravity/AntigravityAdapterTests.cs`，
  沿用 `ChangeProbe_*` 家族命名）：
  - `ChangeProbe_WalSidecarTouched_DetectedAsChange`——库内容一字不动、仅 `-wal` mtime 改动 →
    `FilesChanged=1` 且 `FilesRead=1`，并断言主库戳未变（证明重读只能由边车触发）；
  - `ChangeProbe_WalSidecarRemoved_TriggersFullRescan`——边车消失（checkpoint 收尾形态，主库戳
    不动）→ 必须重读一轮，哨兵戳语义。
  - 回归锁已实测：把适配器改回 `Capture` 后两条同 RED，恢复后同绿。
  - fixture 造真实 WAL 库（`OpenWalSidecarWriter`：WAL 切换 + 写事务回滚落出空边车，写连接由
    调用方持住防收尾 checkpoint 删边车）。
- **全量验证**：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 全绿
  （Core 271 / Reconciliation 49+19 skipped / App 506 / Relay 50 / Infrastructure 836+3 skipped /
  App.Headless 666，合计 2378 passed 0 failed）。既有
  `ChangeProbe_UnchangedSecondScan_SkipsRereadAndReusesCachedRecords` 零改动通过。
- **SmokeScan 该源**（本机无 Antigravity）：首轮 `0/0/n/a`、后续轮 `0/0/0`，与改动前一致，零副作用。
- 开放问题（checkpoint 行为）已答：`wal_autocheckpoint`=1000 页 × 32 KB = 32 MB 阈值 + 运行后
  遗留 WAL（#683）→ 活跃期主库可长期不动，"使用中数据滞后"是主要影响面，**优先级维持 P2**。
- 明确不做（照卡）：不引入探针、不引入持久只读连接；`status:` 不动（交 PM 收口），未 commit。

- **审查修复（同日，独立审查 PASS-with-notes）**：P2-1 措辞勘误——I-049（9c31970）后 OpenCode 已改
  逐库两级探针（`FileFingerprint(DbLength, DbWriteUtc, WalLength, WalWriteUtc)`），"与其余四个
  SQLite 源逐字同口径"改为"与其余三个 FileStampMemo 系源（Goose/Hermes/Kilo）逐字同口径；OpenCode
  为语义同口径的逐库两级探针"（适配器行内注释 + 本 Dev Log 两处；Background/定案方向为建卡日快照
  保留原样，Background 行号引用加括号勘误）；P2-2——`ChangeProbe_WalSidecarRemoved` fixture 注释
  补 SQLite ≥3.22 只读重开依赖与响亮失败模式说明。P2-3（结束未清连接池）审查判不必修，未动。

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 调研结论：五个 SQLite 源里，Goose / Hermes / Kilo / OpenCode 都已把 `-wal` 纳入指纹，只有
  Antigravity 仍在 `Capture` 主库——这不是性能问题而是**正确性口径不一致**（同一批源里唯一一个
  可能读到滞后数据的），故优先级 P2、size S（一处改动 + 两条测试）。
- 本机没有 Antigravity 数据（SmokeScan 该源 0 文件），journal_mode 无法在仓库侧确认 → 卡内写死
  "先取证、可能作废"的前置步骤，不允许执行者凭假设改代码。
- 没把本卡与 I-049/I-050 合并：那两张是"活跃期全量重读"的性能改造（探针 + 持久连接），机制不同，
  合并会让一张卡同时承担正确性修复与性能改造，回滚粒度也变差。
