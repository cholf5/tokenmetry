---
id: I-048
title: Antigravity 指纹补 -wal——活跃写入期不再读到陈旧账
type: bug
priority: P2
size: S
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

让 Antigravity 源的零变更判定看到 SQLite 的 `-wal` 边车，避免"写端正在提交、主库文件还没被
checkpoint 回写"时复用到上一轮的旧记录（表现为界面数据滞后，直到 checkpoint 或应用退出才追上）。

## Background（已对照源码核实）

- `src/Tokenmetry.Infrastructure/Antigravity/AntigravityAdapter.cs:44`：`FileStampMemo.Capture(databases)`
  —— 只对**主库文件**取 (mtime, size)。
- 同类另外四个 SQLite 源都用了 WAL 感知的指纹：`GooseSqliteAdapter.cs:70`、`HermesSqliteAdapter.cs:70`、
  `KiloSqliteAdapter.cs:56`、`OpenCodeSqliteAdapter.cs:73`，都是
  `FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databases))`
  （`FileStampMemo.cs:114` 定义 = 主库 + `-wal`；`FileStampMemo.cs:122` 的 `CaptureWithOptional`
  对"边车本轮不存在"取哨兵戳，缺失状态本身进指纹）。
- WAL 语义：写入先追加到 `-wal`，**主库 mtime 在 checkpoint 之前不变**；SQLite 默认自动 checkpoint
  阈值是 1000 页（约 4 MB 累计），低频写入的库可以长时间不动主库。于是：
  ① 主库没动 → `TryReuse` 命中 → 本轮零读取，新提交的用量看不到（滞后）；
  ② 一旦 checkpoint 落盘 → 主库 mtime 变 → 突然重读并跳变（数据"迟到"）。
- Antigravity 解析路径（`AntigravityParser.ParseSqliteFile`）走只读连接，能读到 WAL 里已提交的数据
  —— 所以缺的只是"何时该重读"的信号，不是读不到。

### 先决取证（执行者必须先做，可能让本卡作废）

本机没有安装 Antigravity（`~` 下无对应数据目录，SmokeScan 该源 `0/0/n/a`），无法在仓库侧确认它的
journal 模式。执行者第一件事：

1. 在有 Antigravity 的机器上打开其会话库，**只读**执行 `PRAGMA journal_mode;`；
2. 同时观察数据目录里是否出现 `*-wal` 文件（存在且非 0 字节即为 WAL 模式）。

- 结论是 **WAL** → 按下面的定案方向改。
- 结论是 **rollback journal（delete/truncate）** → 主库 mtime 已经够用，本卡的 perf 前提不成立，
  **按"不做"收口**，把取证结论写进卡内 + 在 `AntigravityAdapter` 类注释里加一句"为什么不用
  `SqliteFingerprintFiles`"，防后人反复怀疑。

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

- [ ] 取证结论（journal_mode）写进本卡 Background，WAL 才实施
- [ ] 指纹改为 `SqliteFingerprintFiles` + `CaptureWithOptional`，与另外四源同口径
- [ ] 新增「仅 `-wal` 变化 → 重读」回归测试（改回旧写法必 RED）
- [ ] 新增「`-wal` 消失 → 重读」测试
- [ ] 既有零变更轮测试零改动通过；`dotnet test Tokenmetry.sln` 全绿
- [ ] 类注释写明 `-wal` 进、`-shm` 不进的理由

## 开放问题

- Antigravity 的 checkpoint 行为：它是长期驻留的应用，若退出时才 checkpoint，则本卡影响的是
  "使用中数据滞后"（明显）；若它常驻自动 checkpoint，影响只剩"每 N MB 一次迟到跳变"（轻微）。
  取证时顺手记一句，决定是否把优先级从 P2 降到 P3。

## Dependencies

- 无（文件面只有 `Antigravity/` + 其测试文件，可与同批其它卡并行）。

## Related

- I-021（Antigravity 适配器来源卡）
- I-042（无变化轮次短路 / 文件戳记忆化口径的来源卡）
- I-006（ZCode SQLite 适配器，SQLite 读取口径先例）
- I-049 / I-050（同批：OpenCode / Kilo 活跃期全量重读）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 调研结论：五个 SQLite 源里，Goose / Hermes / Kilo / OpenCode 都已把 `-wal` 纳入指纹，只有
  Antigravity 仍在 `Capture` 主库——这不是性能问题而是**正确性口径不一致**（同一批源里唯一一个
  可能读到滞后数据的），故优先级 P2、size S（一处改动 + 两条测试）。
- 本机没有 Antigravity 数据（SmokeScan 该源 0 文件），journal_mode 无法在仓库侧确认 → 卡内写死
  "先取证、可能作废"的前置步骤，不允许执行者凭假设改代码。
- 没把本卡与 I-049/I-050 合并：那两张是"活跃期全量重读"的性能改造（探针 + 持久连接），机制不同，
  合并会让一张卡同时承担正确性修复与性能改造，回滚粒度也变差。
