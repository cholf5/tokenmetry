---
id: I-051
title: SQLite 源采集成本审计落档——五源实测矩阵 + Goose/Hermes「不改」判定
type: infra
priority: P3
size: S
status: done
created: 2026-11-13
updated: 2026-11-14（done）
blocked: none
---

## Goal

把五个 SQLite 直读源（ZCode / OpenCode / Kilo / Goose / Hermes / Antigravity）的采集成本与判定
**一次性落到一张可引用的文档里**，让后续任何人（包括以后的智能体）不用重跑一遍调研就能回答：

- 每个源的零变更判定靠什么（文件指纹 / 探针 / 两者），边界在哪；
- 活跃写端期每轮的真实成本（行数、字节、耗时）；
- 哪些源**经实测确认不需要改**，理由是什么（防"反复怀疑、反复调研"）。

## Background（本次调研已完成，结论如下）

### 指纹/探针现状（已对照源码核实）

| 源 | 零变更判定 | 活跃写端期行为 | 载荷表 |
|---|---|---|---|
| ZCode | 两级：文件指纹快路径（db + `-wal`）→ `COUNT + MAX(id)` 探针；持久只读连接 + 32 MB 页缓存 | 指纹变 → 探针；探针不变 → 零读取 | `model_usage`（本机实测 34,578 行 / 库文件 1.24 GiB） |
| OpenCode | 文件指纹（db + `-wal`） | 指纹变 → **全库重读重映射** | `session_message`（v2）/ `message`（v1） |
| Kilo | 文件指纹（db + `-wal`） | 同上 | `message` |
| Goose | 文件指纹（db + `-wal`） | 同上，但表是**每会话一行**的聚合表 | `sessions` |
| Hermes | 文件指纹（db + `-wal`） | 同上，聚合表 | `sessions` |
| Antigravity | 文件指纹**仅主库**（无 `-wal`） | 主库 checkpoint 前不变 → **可能读到滞后数据** | `gen_metadata` / `steps` / `trajectory_metadata_blob` |

源码锚点：`FileStampMemo.cs:114`（`SqliteFingerprintFiles` = 主库 + `-wal`）、`:122`（`CaptureWithOptional`，
边车缺失取哨兵戳）；各源调用点 `OpenCodeSqliteAdapter.cs:73`、`KiloSqliteAdapter.cs:56`、
`GooseSqliteAdapter.cs:70`、`HermesSqliteAdapter.cs:70`、`AntigravityAdapter.cs:44`；
ZCode 参照实现 `ZCodeSqliteAdapter.cs:115-160 / :305 / :331 / :358 / :415`。

### 本机实测（macOS，2026-11-13）

- **OpenCode**：`opencode.db` 43.7 MB；`session_message` 1094 行 / 其中 assistant 1016 行；
  assistant 载荷合计 28.9 MB；`COUNT(*) WHERE type='assistant'` 4 ms；SmokeScan 首轮 132 ms / 1013 记录，
  空闲轮 0 ms。→ 值得做，见 I-049。
- **Kilo / Goose / Hermes / Antigravity**：本机均无安装（SmokeScan 全部 `0/0/n/a`）→ 量级只能由
  执行者在有数据的机器上取证，已分别写进 I-048 / I-050 的前置步骤。
- **Goose / Hermes 的“不改”初判**（需本卡用合成规模坐实，不许当结论直接引用）：两者读的是
  `FROM sessions` 的每会话聚合行，行数 = 会话数（百～千级），单轮全量重读是毫秒级；
  `-wal` 变动导致的重读没有载荷解析成本，收益不足以引入持久连接与新状态机。
- **ZCode**：本机库 1.24 GiB / `model_usage` 34,578 行，两级探针已落地（I-042 延伸），
  不在本批改动范围，仅作为参照实现；执行本卡时顺手从 `app.log` 回填该源的逐轮耗时量级。

## 定案方向

1. 新建 `docs/perf-investigation-<日期>-sqlite-sources.md`（放在既有
   `docs/perf-investigation-2026-10-28.md` 旁，同样入库、同样结构：方法 → 数据 → 判定 → 明确不做），
   内容 = 上面矩阵 + 每源的"为什么这么判" + 未取证项清单。
2. **用合成规模坐实 Goose / Hermes 的"不改"**（这是本卡唯一需要写代码的部分，测试侧即可）：
   用 fixture 造 N=5000 会话的 `sessions` 表，量一轮全量读取耗时与载荷字节；若 < 10 ms 量级，
   在文档里写"经合成 5k 会话实测 = X ms，判定不改"，并把该 fixture 断言留成普通单测
   （或只作为一次性测量，删掉测量代码但把数字留在文档——二者择一，在卡内注明）。
3. **判定的传播**：把"五源指纹/探针口径 + 各自边界"补进各适配器类注释的一行交叉引用，
   指向本卡的文档（避免注释与文档两处漂移：注释只写"为什么"，数字留在文档）。
4. 明确不做：不在本卡改任何适配器行为；不把审计文档写成"性能承诺"（数字是特定机器的量级）。

## Acceptance Criteria

- [x] `docs/perf-investigation-<日期>-sqlite-sources.md` 入库，含上面矩阵、实测方法（命令级可复现）、
      每源判定与理由、未取证项清单
      （`docs/perf-investigation-2026-11-14-sqlite-sources.md`，与 2026-10-28 范本同构：方法 → 数据 → 判定 → 明确不做）
- [x] Goose / Hermes 的"不改"有实测数字支撑（合成 5k 会话 fixture 或真机数据，注明来源）
      （合成 fixture：真 SQLite/WAL、按两源真实读取路径；Goose 中位 ≈18–20 ms / Hermes ≈20–21 ms，
      载荷 0.45 / 0.42 MB，空闲轮 0.1–0.5 ms——数字与量法在文档 3.4/4.1 节；测量代码已删，
      选择"一次性测量"路线，理由见 Development Log）
- [x] ZCode 的现状数字回填（它的两级探针是否仍是当前最优，顺手确认 `app.log` 里该源耗时量级）
      （app.log 修复前 5,087 轮：空闲轮 p50 1,401 ms / max 63 s——正是两级探针的动机数字；修复后
      生产样本本机没有，SmokeScan 代表 1,575 → 13 → 0 ms；两级探针仍是当前最优，判定不改。
      文档 4.3 节）
- [x] 五个适配器类注释各加一行指向该文档（只加一行，不复制数字）
      （勘误：矩阵实为六源——六个适配器类注释各加一行，含参照实现 ZCodeSqliteAdapter）
- [x] `dotnet test Tokenmetry.sln` 全绿（本卡不改生产行为；若写了一次性测量代码，已删除或已标注）
      （一次性测量工程已删除；全量测试见 Development Log）

## 开放问题

- 文档放 `docs/` 根还是 `docs/perf/`：跟既有 `docs/perf-investigation-2026-10-28.md` 保持一致
  （根目录、日期前缀命名），不新造目录层级。
- 与 I-049 / I-050 的先后：本卡记录的是**今日基线**；若那两张卡先落地，执行本卡时应按落地后复测
  并在文档里同时保留"改造前/后"两组数字（这才是审计该有的样子）。

## Dependencies

- 无硬依赖。文件面只有 `docs/` + 五个适配器的**注释行**（若与 I-048/I-049/I-050 并行执行，
  注释行有极小概率撞同一文件 → 各卡落地后再做本卡的注释补充最省事，卡内已写建议顺序）。

## Related

- I-006（ZCode SQLite 适配器）、I-009（OpenCode）、I-017（Kilo）、I-012（Goose）、I-016（Hermes）、I-021（Antigravity）
- I-042（无变化轮次短路：指纹口径的来源）
- I-043（行级增量被否：本批"不做行级增量"的公共依据）
- I-048 / I-049 / I-050（本批三张改动卡）、I-052（读取量量尺）
- `docs/perf-investigation-2026-10-28.md`（同族文档的结构范本）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 调研已完成一半（本卡 Background 的矩阵与本机数字就是调研产物），剩下的缺口只有三个：
  Kilo / Goose / Hermes / Antigravity 的真机量级、Goose/Hermes 的"不改"实测坐实、ZCode 现状复核。
- 之所以要一张独立卡而不是把结论散在各改动卡里：将来唯一会被反复问到的问题是
  "Goose/Hermes 那么小的表为什么也要定期重读"，如果没有一份写着实测数字的文档，
  下一批智能体会再调研一遍——这已经在本次会话里发生过一次。

### 2026-11-14 落档

- **计数勘误**：卡标题写"五源"、Goal 与矩阵列了六个源（建卡时把 ZCode 当参照实现没算进改动面）。
  按六源落档（ZCode / OpenCode / Kilo / Goose / Hermes / Antigravity），文档头部与 AC-4 均已注明。
- **审计文档入库**：`docs/perf-investigation-2026-11-14-sqlite-sources.md`（与
  `perf-investigation-2026-10-28.md` 同目录同构：结论摘要矩阵 → 本机规模取证 → 测量方法 →
  实测结果 → 每源判定与理由 → 未取证项与重开条件 → 判定边界 → 数字速查）。矩阵全部六源用
  **现在时**描述（按今日代码），同批四张卡的结论以"改前/改后"两组数字呈现。
- **Goose / Hermes 合成 5k 会话实测**（判定"不改"的核心证据）：
  - 量法：真 SQLite（WAL）fixture × 5,000 会话行，schema 照抄两源 `SessionQuery` 固定列、行全部
    合法（实测 records == 5000、status Ok）；每轮**新建适配器实例**走真实 `ScanAsync` 全管线，
    Stopwatch 7 轮取中位，整个程序连跑 3 次验证稳定；载荷 = SELECT 固定列 `SUM(LENGTH(列))`；
    同实例第二扫作空闲轮对照。
  - 数字：Goose 全量重读中位 20.4 / 17.8 / 19.4 ms（≈18–20 ms，单轮 12–31 ms，前两轮 JIT 暖机）；
    Hermes 20.7 / 20.4 / 20.9 ms（≈20–21 ms）；载荷 Goose 446,900 B（≈89 B/行）、Hermes 417,200 B
    （≈83 B/行）；库文件 598,016 B；空闲轮 Goose 0.40–0.54 ms、Hermes 0.08–0.10 ms。
    条件：macOS 25.5 arm64 / .NET 10.0.14 / Release / 页缓存热态。
  - 与卡内"< 10 ms 量级"预期的关系：实测 ≈20 ms（≈4 µs/行、随会话数线性），**不在 10 ms 以内
    但同一量级**；判定仍为不改，依据不只是绝对耗时——载荷比 OpenCode 小 60 倍、空闲轮近零、
    活跃轮收益上限 = 每 5 分钟省一次 20 ms，撑不起持久连接 + 探针状态机。措辞如实写进文档 4.1。
  - **二选一的选择：一次性测量，测量代码已删除**（scratch 工程在 gitignore 的 `dist/bench/` 下，
    取完数字即删）。理由：单轮波动 ±50%（12–31 ms）正是 perf 断言 flaky 的典型形态，留成普通
    单测只能写"上限护栏"式断言（如 < 500 ms），护栏一松就失去意义、一紧就 CI 抖；数字与量法
    （fixture schema、轮数、口径）已完整留在文档 3.4/4.1 节，需要时可按节复建。
- **ZCode 现状回填**：本机库今日 1,367,371,776 B（1.27 GiB）/ `model_usage` 35,872 行
  （completed 35,684）/ `dbstat` 表页 26.3 MB（建卡日 1.24 GiB / 34,578 行，账本在涨，引用须带日期）。
  `app.log`（`~/Library/Application Support/Tokenmetry/logs/`，覆盖 2026-11-01 → 11-11，5,087 轮
  ZCode 记录，**全部为 `45f1ff1` 修复前版本**，此后 app 未再运行）：空闲轮（changed=0，3,729 轮）
  p50 1,401 ms / max 63.1 s，活跃轮（1,358 轮）p50 1,588 ms / max 61.2 s——正是两级探针修复的
  动机数字（无索引探针每轮全表 SCAN ≈26 MB）。修复后生产样本本机没有，SmokeScan 代表：
  首轮 1,575 ms（全量 35,690 记录）→ 活跃轮 13 ms（指纹变、探针 1 次、零读取）→ 空闲 0 ms
  （零 SQL）。结论：两级探针仍是当前最优，判定不改；正确性面（"部分读取 × 下轮删库"漏洞）由
  I-049 审查发现、已登记待另卡，不在本审计范围。
- **OpenCode 改后今日复测**：`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`——首轮
  86 ms / 1013 记录（卡内审查后复测为 124 ms，JIT/页缓存轮间波动，行为一致），空闲轮 0–1 ms；
  文档 4.2 以"改前 132 ms / 每活跃轮 29 MB 载荷 → 改后零读取"呈现两组数字。
- **六个适配器类注释各加一行交叉引用**（指向审计文档、不复制数字）：
  `ZCodeSqliteAdapter` / `OpenCodeSqliteAdapter` / `KiloSqliteAdapter` / `GooseSqliteAdapter` /
  `HermesSqliteAdapter` / `AntigravityAdapter`。
- **全量验证**：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln`
  2,378 passed / 0 failed（Core 271 / Reconciliation 49+19 skipped / App 506 / Relay 50 /
  Infrastructure 836+3 skipped / App.Headless 666）——本卡零生产行为改动，注释行不影响任何断言。
- 不 commit；`status:` 不动（交 PM 收口）；`docs/plans/` 未碰；文档未新造目录层级。

### 2026-11-14 审查修复（PASS-with-notes 落地）

- **P1（必修，发现本身值得留档）**：§4.3 的 SmokeScan"修复后代表数字"（13/0 ms）缺**写入端状态
  条件**。审查者实测发现：ZCode CLI **持续提交期间**（`-wal` 钉在 ~4 MB autocheckpoint 阈值、
  mtime 不停跳、completed 行数在涨），`dotnet run --project tools/Tokenmetry.SmokeScan -- 3` 行为
  标记逐位一致（1/1/1 → 1/0/0）但量级完全不同——空闲轮 **1,263–1,522 ms** 而非 13/0 ms；机理 =
  写端风暴不断重置 WAL、使连接级 32 MB 页缓存失效，探针退化为 ~1.4 s 全表扫描（与修复前空闲
  p50 1,401 ms 统计上无差别）；审查者隔离连接三次计时 1.377 s → 0.788 s → 0.012 s 证明暖连接
  探针 ~12 ms 为真、但只在写端停歇窗成立。**修复**：§4.3 代表数字块后补"适用条件"段 +
  §6 未取证项第 7 行同款补注；判定结论补一句"以行为而非耗时为锚点"（行为锚点 changed=0 /
  零读取不变——修复保证"账面没动就不重读数据"，不保证"探针恒快"）。
- **P2**：① §3.4 波动幅度自相矛盾（±25% vs 实际 12–31 ms ≈ ±50%）→ 统一为 ±50%；② §6 第 6 行
  首轮 4,109 ms 无出处 → 补"2026-11-14 本机 SmokeScan 自跑（I-052 卡内自跑 4,274 ms）"标注。
- **P3**：① §5.2 "注释互相指向"实为单向（OpenCode 指向 ZCode，无回指）→ 改"OpenCode 注释指向
  ZCode 蓝本、无回指"，未给 ZCode 加回指（保持最小改动面）；② Goose/Hermes 适配器注释行
  "合成 5k 会话实测坐实"→"合成规模实测坐实"（fixture 规模数字不进注释，与"注释不复制数字"
  同源；ZCode 注释行无同类措辞）。P3-3（"各加一行" vs 每文件 2 物理折行）审查判不修。
- **验证**：`dotnet test Tokenmetry.sln` 0 失败（Core 271 / Reconciliation 49 / Relay 50 / App 506 /
  Infrastructure 836 与修复前逐位一致；Headless 一轮被超时转入后台后计数显示 615——**同代码
  单独复跑 666/666、0 失败、时长恢复正常（4m04s vs 异常轮 15m12s）**，判定为后台迁移干扰的
  计数异常而非测试丢失；全部改动仍仅 `///` 注释行与文档）。不 commit。
