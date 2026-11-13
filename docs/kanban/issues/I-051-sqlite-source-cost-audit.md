---
id: I-051
title: SQLite 源采集成本审计落档——五源实测矩阵 + Goose/Hermes「不改」判定
type: infra
priority: P3
size: S
status: todo
created: 2026-11-13
updated: 2026-11-13
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

- [ ] `docs/perf-investigation-<日期>-sqlite-sources.md` 入库，含上面矩阵、实测方法（命令级可复现）、
      每源判定与理由、未取证项清单
- [ ] Goose / Hermes 的"不改"有实测数字支撑（合成 5k 会话 fixture 或真机数据，注明来源）
- [ ] ZCode 的现状数字回填（它的两级探针是否仍是当前最优，顺手确认 `app.log` 里该源耗时量级）
- [ ] 五个适配器类注释各加一行指向该文档（只加一行，不复制数字）
- [ ] `dotnet test Tokenmetry.sln` 全绿（本卡不改生产行为；若写了一次性测量代码，已删除或已标注）

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
