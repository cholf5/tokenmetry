---
id: I-050
title: Kilo 活跃写端期全库重读——同 I-049 的探针 + 持久只读连接（第二个载荷表源）
type: infra
priority: P3
size: M
status: wontfix
created: 2026-11-13
updated: 2026-11-14（wontfix）
blocked: none
resolution: 2026-11-14 按「不做」收口——本机从未安装/使用 Kilo，无任何真实数据可测（取证见下），载荷 = 0 低于卡内任何门槛；无可测对象，探针与持久连接无从验证收益，留证待 Kilo 数据出现后重开。
---

## Goal

Kilo 源的活跃期（写端持续提交）不再每轮重读重映射整张 `message` 表：与 I-049（OpenCode）同一套
两级探针 + 持久只读连接，探针表达式按 Kilo schema 定制。

## Background（已对照源码核实）

- `KiloSqliteAdapter.cs:56`：`FileStampMemo.CaptureWithOptional(SqliteFingerprintFiles(...))`——
  指纹含 `-wal`，写端一提交就必变 → 每轮全量。与 OpenCode 同型。
- 读取面（`KiloSqliteAdapter.cs:111` 起）：每轮新开 `SqliteReadonly.Open(...)`，然后
  `SELECT id, session_id, data FROM message`（`:36` `MessageQuery`，蓝本固定列序），逐行
  lenient JSON 映射，按 message id 去重后排序。
- 与 OpenCode 同款的放大点：`-wal` 是整库共用的写入日志，**任何表的写入都会动它**——只要
  Kilo 进程在跑，指纹就一直在变，今天每一轮都会把整张 `message` 表重读重映射，哪怕一条 usage
  都没新增。（取证时顺手看 `sqlite_master` 里除 `message` 外还有哪些表，写进卡。）
- **本机无 Kilo 数据**：`~/.local/share/kilo` 不存在，SmokeScan 该源 `0/0/n/a`。故本卡的量级
  必须由执行者自己取证（见下），不能照抄 OpenCode 的数字。
- 参照实现：`ZCodeSqliteAdapter.cs:115-160`（两级探针 + 零变更短路）、`:305` `TryProbe`、
  `:331` `GetReadOnlyConnection`（`:358` `PRAGMA cache_size=-32768`）、`:415` `TryGetFileFingerprint`；
  测试 `ZCodeSqliteAdapterTests.cs:192-233`；同批 I-049 是另一份移植样例。

### 先决取证（执行者第一件事）

1. 找一台装过 Kilo 的机器（或用户机器），记录：`kilo.db` 大小、`SELECT COUNT(*), MAX(rowid) FROM message`、
   `SELECT SUM(LENGTH(data)) FROM message`（载荷总量）、以及 SmokeScan 该源首轮耗时。
2. 把三四个数字写进本卡 Background 后再动手。若实测载荷 < 2 MB 且首轮 < 20 ms，**按"不做"收口**
   并留证（在 I-051 的审计矩阵里补一行），不必为一个毫秒级优化引入持久连接。

### 取证复核（2026-11-14，执行者独立复核）

PM 于 2026-11-14 口头核实本机无 Kilo 数据；执行者当日 03:30 UTC 独立复核一遍，命令与结果：

| 检查项 | 命令 | 结果 |
| --- | --- | --- |
| 环境变量 | `env \| grep -i kilo` | 无 `KILO_DATA_DIR`，无任何 Kilo 相关变量 |
| 默认数据目录 | `ls ~/.local/share/kilo` | 不存在（No such file or directory） |
| 数据根全查 | `ls ~/.local/share/ \| grep -i kilo` | 无匹配；该目录仅有 CMakeTools / NuGet / emmylua_ls / opencode / powershell / uv |
| 应用安装 | `ls /Applications \| grep -i kilo`、`ls ~/Applications \| grep -i kilo`、`mdfind "kMDItemKind == 'Application' && kMDItemDisplayName == '*Kilo*'cd"` | 均无结果 |
| 库文件全盘 | `mdfind -name kilo.db` | 仅命中仓库自带对账 fixture（`tests/Tokenmetry.Reconciliation.Tests/fixtures/kilo/kilo.db`，8 KB 合成数据，非用户数据） |
| CLI | `which kilo` | 未安装 |
| 配置残留 | `ls ~/.kilo ~/.config/kilo ~/Library/Application Support/{Kilo,kilo}` | 均不存在 |
| SmokeScan 该源 | `dotnet run --project tools/Tokenmetry.SmokeScan -- 3` | `[Kilo] done in 0 ms — files discovered/read/changed: 0/0/0, records: 0, status: NoData` |

结论：本机从未安装/使用过 Kilo，**卡内要求的取证数字（库大小 / 行数 / 载荷字节 / 首轮耗时）无从
取得——载荷 = 0，低于"载荷 < 2 MB 且首轮 < 20 ms"门槛的极限情形**。按卡内门槛逻辑按「不做」收口，
证据留档如上；若日后该机出现真实 Kilo 数据（或拿到装过 Kilo 的机器），携带上述数字按卡内
定案方向重开（实现蓝本届时以 I-049 的 OpenCodeSqliteAdapter 为准样）。

## 定案方向

1. **与 I-049 逐字同构**（两级探针 + 持久只读连接 + 连接身份校验 + 干净指纹方向性约束），
   只把探针表达式换成本库形态：
   - `SELECT COUNT(*), COALESCE(MAX(rowid), 0) FROM message`。
   - `message` 若是 `WITHOUT ROWID` 表或纯文本主键（执行时用 `PRAGMA index_list` / 建表 SQL 确认），
     退化为 `SELECT COUNT(*)` 单值探针（count 变化 = 行集变化；`id` 是文本、非单调，不能当 max 信号）。
   - 探针不合法（表缺失 / 打不开）→ 保守全量，绝不影响正确性。
2. **口径零变化**：records 排序、`DuplicateRecords`（按 message id 折叠）、`status` 判定、
   "打开/查询失败 = 该库本轮 0 条 + 问题标记"的降级语义全部不动。
3. **不抽公共件**：本卡与 I-049 各自实现（并行安全），注释互相指向；重复若将来成为负担，
   另开一张纯 refactor 卡在做完两卡之后统一。
4. **明确不做**：不做行级增量（只读新增 rowid）——理由同 I-043 / I-049；不动 `-shm` 指纹；
   不改 UI/文案。

## Acceptance Criteria

- [ ] 取证数字（库大小 / 行数 / 载荷字节 / 首轮耗时）写进卡内；低于门槛则按"不做"收口并留证
- [ ] 空闲轮：文件指纹未动 → 零 SQL 探针（`ProbeQueryCountForTests` 不增）、`FilesRead == 0`、
      `FilesChanged == 0`、记录复用上轮实例
- [ ] 活跃轮（仅 `-wal` 变化、数据未增行）：SQL 探针实跑 1 次 → `FilesChanged == 0`、`FilesRead == 0`
      （配"仅 touch `-wal`"测试）
- [ ] 真有新消息（行数变化）→ 全量重读，记录数增加
- [ ] 探针降级路径：`message` 表缺失 / 打不开 / 无 rowid → 保守全量，`status` 按既有口径（`Partial`），不抛异常
- [ ] 持久连接身份校验：库被替换 / 删除 → 弃连重开，不残留已删库贡献
- [ ] 全量 `dotnet test Tokenmetry.sln` 0 失败；SmokeScan 三轮行为与今日一致

## 开放问题

- Kilo 是否稳定写 `message.data`（一写定稿）还是反复重写同一条 message：若**同一条 message 的
  `data` 会在原地被多次覆盖**（流式落盘），则 `COUNT + MAX(rowid)` 探针对"原地覆盖"完全无感
  ——这一点必须取证（该库写入方行为，不是我们代码），取证结果写进卡：
  - 一写定稿 → 探针可信，照 I-049 做；
  - 频繁原地覆盖 → **探针只能省"完全没写"的轮次**，本卡收益大幅缩水，应降级为
    "只做持久连接 + 页缓存"（省磁盘读，不省解析），并在卡内写明为什么没做探针。
- ZCode 已登记同一类边界（"行原地改值且 count/max 不动不会被发现，usage 行实务上只增不改"），
  Kilo 若不同，本卡不能照抄该边界。

## Dependencies

- 无硬依赖；文件面只有 `KiloSqlite/` + 其测试文件，可与 I-048 / I-049 并行。
- 实现时先读 I-049 与 `ZCodeSqliteAdapter` 的探针段（别自己发明探针状态机）。

## Related

- I-017（Kilo 适配器来源卡）、I-006（ZCode 探针先例）、I-009（OpenCode 同型）
- I-042（无变化轮次短路）、I-043（行级增量被否）
- I-049（同批同型卡）、I-051（审计矩阵：本卡的取证数字也回填到这里）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 为什么要独立一张卡而不是并进 I-049：两个适配器文件互不重叠，独立开卡能让两个智能体并行执行、
  各自独立回滚；合并则变成"一次改动两个源"，一处回归会牵连另一处。
- 与 I-049 唯一实质差异是**取证门槛**：OpenCode 在本机有真实数据（43.7 MB / 1016 行 / 132 ms），
  Kilo 没有任何本机数据，所以卡内写死"先取证、低于门槛就不做"，不允许凭"同型所以一样慢"下结论。
- 把"原地覆盖"这个开放问题显式留在卡里：它决定探针是否可信，是本卡唯一的真风险点。

### 2026-11-14 按「不做」收口

- **收口理由**：取证复核（见上方"取证复核"小节，2026-11-14 03:30 UTC）确认本机从未安装/使用
  Kilo——数据目录、环境变量、应用、CLI、配置残留全空，全盘仅有仓库自带的对账 fixture。载荷 = 0，
  低于卡内"载荷 < 2 MB 且首轮 < 20 ms"门槛的极限情形；**无可测对象**，探针正确性（原地覆盖开放
  问题）与收益（活跃轮省多少）都无从验证，为一个零载荷源引入持久连接不符合成本收益。
- **不做的事**：本卡零代码改动（`KiloSqliteAdapter` 未动）。
- **留证与重开条件**：取证命令与结果已固化在"取证复核"小节；待本机出现真实 Kilo 数据或拿到
  装过 Kilo 的机器后，按卡内定案方向重开，实现蓝本参照 I-049 落地后的 `OpenCodeSqliteAdapter`
  （两级探针 + 持久只读连接同构移植）。
- I-051 审计矩阵：本卡收口条件是"无数据"而非"实测低于门槛"，无需补实测数字行；I-051 Background
  已登记"Kilo 本机无安装（SmokeScan 0/0/n/a）"，与本卡取证一致。
