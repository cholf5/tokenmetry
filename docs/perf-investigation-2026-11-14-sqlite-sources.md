# SQLite 源采集成本审计（2026-11-14）

日期：2026-11-14
范围：六个 SQLite 直读源的采集成本与零变更判定一次性落档——ZCode / OpenCode / Kilo / Goose / Hermes / Antigravity
来源卡：I-051（本卡）＋同批四张：I-049（OpenCode 已落地）、I-048（Antigravity 已落地）、I-050（Kilo 不做收口）、I-052（SmokeScan 量尺）；附录一行：I-053（JSONL 缓冲，不做收口）
相关提交：`45f1ff1`（ZCode 两级探针）、`9c31970`（OpenCode 两级探针）、`fc65e65`（Antigravity 指纹补 -wal）
计数勘误：I-051 卡标题写"五源"，卡内 Goal 与矩阵实际列了六个源——**按六源落档**（建卡时把 ZCode 当"参照实现"没算进改动面，但审计矩阵必须含它）。

## 1. 结论摘要

| 源 | 零变更判定（现在时，按 2026-11-14 代码） | 活跃写端期行为 | 载荷表 | 本机量级（2026-11-14） | 判定 |
|---|---|---|---|---|---|
| ZCode | 两级：文件指纹快路径（db + `-wal`）→ `COUNT + MAX(id)` 探针；持久只读连接 + 32 MB 页缓存 | 指纹变 → 探针；探针不变 → 零读取 | `model_usage`（每会话聚合账本） | 库 1.27 GiB / 35,872 行 / 表 b-tree ≈26 MB | **不改**（参照实现） |
| OpenCode | 两级探针（v2 `session_message` 恒探 + v1 `message` 存在才探）+ 持久只读连接 | 无关表写 → 探针 1 次 → 零读取；新增行 → 全量 | `session_message`（v2）/ `message`（v1） | 库 43.8 MB / assistant 1016 行 / data 载荷 ≈28 MB | **改过**（I-049） |
| Kilo | 文件指纹（db + `-wal`） | 指纹变 → 全库重读 | `message` | 无数据（从未安装） | **不改**（无数据，I-050 留证待重开） |
| Goose | 文件指纹（db + `-wal`） | 指纹变 → 全量重读 `sessions` | `sessions`（每会话一行聚合） | 无数据；合成 5k 会话全量重读 ≈20 ms | **不改**（合成实测坐实） |
| Hermes | 文件指纹（db + `-wal`） | 指纹变 → 全量重读 `sessions` | `sessions`（每会话一行聚合） | 无数据；合成 5k 会话全量重读 ≈20 ms | **不改**（合成实测坐实） |
| Antigravity | 文件指纹（db + `-wal`，I-048 后） | 主库 checkpoint 前 `-wal` 必变 → 重读 | `gen_metadata` / `steps` / `trajectory_metadata_blob` | 无数据（公开取证：10 分钟 130.8 MB 落 `-wal`） | **改过**（I-048，正确性修复） |

一句话：**六个源里两个"改过"（OpenCode 性能、Antigravity 正确性）均已落地；三个"不改"有了实测/取证支撑；
一个"不改"是零数据的留证收口**。后续任何人不必再调研一遍——重开条件见第 6 节。

## 2. 本机规模取证（2026-11-14，决定量级的前提）

| 源 | 库文件 | 关键表 | 数字 |
|---|---|---|---|
| ZCode | `~/.zcode/cli/db/db.sqlite` | `model_usage` | 1,367,371,776 B（1.27 GiB）/ 35,872 行（completed 35,684）/ `dbstat` 表页合计 26.3 MB；`turn_usage` 仅 1,042 行；WAL 模式（page_size 4096） |
| OpenCode | `~/.local/share/opencode/opencode.db` | `session_message` | 43,757,568 B（43.8 MB）/ 1,094 行，其中 assistant 1,016 行；assistant `data` 载荷 `SUM(LENGTH(data))` = 26.6 MiB（≈27.9 MB）；WAL 模式 |
| Kilo / Goose / Hermes / Antigravity | — | — | 本机无安装（SmokeScan 该源 `0/0/n/a`） |

- 建卡日（2026-11-13）口径：ZCode 1.24 GiB / 34,578 行——账本随使用持续增长（活跃期每分钟数行），**引用本节数字必须带日期**。
- I-049 卡内 assistant 载荷 28.9 MB 是导出实测口径；本文 27.9 MB 是 `SUM(LENGTH(data))` 口径——同一库、两种量法差约 3%，量级一致。
- 结构性事实：ZCode 与 OpenCode 是仅有的两个**载荷表**源（行内嵌大 JSON / 大 blob）；Goose / Hermes 读的是每会话一行的标量聚合行（行宽 ≈85 B），Kilo 同 OpenCode 形态但无本机数据。

## 3. 测量方法（命令级可复现）

### 3.1 行数 / 载荷取证（sqlite3 CLI）

```bash
# ZCode：行数与库规模
sqlite3 ~/.zcode/cli/db/db.sqlite "SELECT COUNT(*), COUNT(*) FILTER (WHERE status='completed') FROM model_usage;"
sqlite3 ~/.zcode/cli/db/db.sqlite "SELECT page_count*page_size FROM pragma_page_count(), pragma_page_size;"
sqlite3 ~/.zcode/cli/db/db.sqlite "SELECT SUM(pgsize)/1048576.0 FROM dbstat WHERE name='model_usage';"  # 表 b-tree 实际页

# OpenCode：assistant 行数与载荷
sqlite3 ~/.local/share/opencode/opencode.db \
  "SELECT COUNT(*), SUM(LENGTH(data))/1048576.0 FROM session_message WHERE type='assistant';"
```

### 3.2 管道级行为（SmokeScan，零副作用：facts 落临时库、结束自删）

```bash
dotnet run --project tools/Tokenmetry.SmokeScan -- 3
```

读数口径（I-052 量尺落地后的输出）：
- 首轮 = 全量读取（适配器记忆为空）；`round 1: total X ms` 含 20 源首轮与临时库初始化；
- 逐源行 `source [X] done in N ms — files discovered/read/changed: a/b/c, records: r, status: s`
  来自生产同款 `IngestionFileLogger`：`changed=0` 且耗时 0–1 ms = 零变更短路命中（零读取）；
  `changed=1` = 本轮有账面变化（全量重读）；
- 空闲轮判定就看它：指纹快路径命中时连 SQL 探针都不发（ZCode/OpenCode 两级探针的第二级不跑）；
- `read_bytes / read_ops` 段（I-052）：Windows `GetProcessIoCounters` / Linux `/proc/self/io`；
  **macOS 记 `n/a`**——本机拿不到字节口径，字节级结论只能引用建卡调研的手工采样。

### 3.3 生产逐轮耗时（app.log 分位统计）

```bash
cd ~/Library/Application\ Support/Tokenmetry/logs
grep -h "source \[ZCode\] done" app.log* \
  | awk -F'done in ' '{split($2,a," ms"); print a[1]}' | sort -n \
  | awk '{v[NR]=$1} END {print "n="NR, "p50="v[int(NR*0.5+0.5)], "p90="v[int(NR*0.9)], "p99="v[int(NR*0.99)], "max="v[NR]}'
# 空闲/活跃分桶：追加 grep "changed: 1/0/0"（空闲）或 grep "changed: 1/1/1"（活跃）
```

### 3.4 Goose / Hermes 合成规模（一次性测量，代码已删）

真 SQLite（WAL 模式）fixture，按两源真实读取路径（适配器原入口 `ScanAsync`，含定位 → 指纹 →
只读连接 → 固定列查询 → 行映射 → 去重 → 排序全管线）：

- fixture：N=5000 会话行，schema 照抄两源 `SessionQuery` 的固定列（Goose 十列含
  `model_config_json = '{"model_name":"…"}'`；Hermes 十二列含五桶 tokens + cost）；行全部合法
  （有用量、可解析时间戳），实测 records == 5000、status Ok；
- 耗时：每轮**新建适配器实例**（空记忆 → 必全量读取）跑 `ScanAsync`，`Stopwatch` 计时，
  7 轮取中位数；整个程序连跑 3 次（3 × 7 = 21 轮/源）看稳定性；
- 载荷：对 SELECT 固定列做 `SUM(LENGTH(列))`（行文本宽度合计，近似每轮传输量）；
- 对照：同实例第二扫（指纹未动 → 记忆复用）量空闲轮成本。

条件：macOS 25.5 arm64（Darwin 25.5.0）/ .NET 10.0.14 / Release；页缓存热态（fixture 刚写完）；
测量代码是 perf 断言会 flaky 的典型（单轮 12–31 ms，对中位 ≈ ±50%），故**只取数字不进测试套件**，
工程已删除、可按本节复建。

## 4. 实测结果

### 4.1 Goose / Hermes 合成 5k 会话（判定"不改"的核心证据）

| 指标 | Goose | Hermes |
|---|---|---|
| 全量重读中位（3 次运行的中位） | 20.4 / 17.8 / 19.4 ms → **≈18–20 ms** | 20.7 / 20.4 / 20.9 ms → **≈20–21 ms** |
| 单轮波动 | 12.3–31.0 ms（前两轮 JIT 暖机偏高） | 14.1–30.9 ms（同上） |
| 载荷（SELECT 列 LENGTH 求和） | 446,900 B ≈ 0.45 MB（≈89 B/行） | 417,200 B ≈ 0.42 MB（≈83 B/行） |
| 库文件 | 598,016 B（584 KB） | 598,016 B（584 KB） |
| 空闲轮（同实例第二扫） | 0.54 / 0.40 / 0.42 ms | 0.10 / 0.09 / 0.08 ms |

读法：5k 会话全量重读 ≈20 ms ≈ **4 µs/行**，成本随会话数线性——千级会话是个位数 ms，
即便重度用户 5 万会话也只 ≈200 ms / 5 分钟一跳，且这是**上限情形**（活跃写端期每轮全量）；
空闲轮已被文件指纹短路到 0.1–0.5 ms（两次 stat 的量级）。

对照 I-049 卡内"若 < 10 ms 量级 → 判定不改"：实测 ≈20 ms，**不在 10 ms 以内但在同一量级**
（毫秒–几十毫秒），且判定依据本就不只绝对耗时：载荷比 OpenCode 小 60 倍、空闲轮近零、
活跃轮收益上限 = 每 5 分钟省一次 20 ms 的重读——引入持久连接 + 探针状态机的复杂度不值。
**判定不改成立**，措辞如实记录为"合成 5k 会话实测 ≈20 ms"。

### 4.2 OpenCode 改造前/后（I-049，本机唯一有真机前后对比的源）

| 阶段 | 首轮全量 | 空闲轮 | 活跃写端期（无关表写入） |
|---|---|---|---|
| 改前（I-049 卡基线，2026-11-13：指纹变即全库重读重映射） | 132 ms / 1013 记录 | 0 ms | 每轮全量：29 MB 载荷读取 + 1,016 次 JSON 解析 + 全量映射（`-wal` 是整库共用日志，写端一提交指纹必变） |
| 改后（`9c31970`，2026-11-14） | 124 ms / 1013 记录（卡内审查后复测）；**今日复测 86 ms / 1013 记录** | 0–1 ms（零 SQL） | 探针 1 次（`COUNT+MAX(rowid)`）→ 零读取，下轮快路径恢复（fixture 测试锁定，`FileFingerprint_WalTouchedByUnrelatedTable_RunsProbeOnceAndSkipsReread`） |

- 今日复测命令与条件：`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`（macOS arm64，
  Release 构建产物，页缓存热态），记录数 1013 与卡内基线逐位一致；首轮 86 ms vs 改前 132 ms
  的差值主要是映射管线 JIT 暖机与页缓存状态的轮间波动，**不改后判定的关键是行为**：
  空闲轮 0–1 ms、活跃期无关表写入零读取——两者在改前都不存在（改前活跃期每轮全量）。
- 收益口径（为什么改）：默认刷新间隔 5 分钟下，改前活跃写端每天 ≈ 288 次 × 29 MB 载荷重读
  且随会话数线性增长；改后该成本只在"真有新 assistant 行"时发生。

### 4.3 ZCode 生产逐轮耗时（app.log 回填，判定"参照实现已到位"）

`~/Library/Application Support/Tokenmetry/logs/`（app.log + 3 个轮转，覆盖 2026-11-01 → 11-11 16:54，
此后 app 未再运行；**全部轮次都属于 `45f1ff1` 两级探针修复之前的版本**，修复 11-13 才落地）：

| 分桶（ZCode 源） | 轮数 | p50 | p90 | max |
|---|---|---|---|---|
| 全部轮 | 5,087 | 1,415 ms | 2,146 ms | 63.1 s |
| 空闲轮（changed=0，n=3,729） | — | 1,401 ms | 1,738 ms | 63.1 s |
| 活跃轮（changed=1，n=1,358） | — | 1,588 ms | 2,436 ms | 61.2 s |

这正是两级探针修复的动机数字：探针查询 `WHERE status='completed'` 在 `model_usage` 上无索引可走，
每轮全表 SCAN ≈26 MB b-tree——**连"账面没变"的空闲轮都要 p50 1.4 秒**，OS 页缓存被逐出后重读盘
即出现 34 秒级毛刺。修复后（指纹快路径 + 持久连接 32 MB 页缓存）生产行为本机尚无样本
（app 11-11 后未重启），用 SmokeScan 代表修复后量级：

```
round 1: ZCode 首轮 1575 ms（全量 35,690 记录）
round 2: ZCode 13 ms — 1/0/0（指纹变 → 探针跑 1 次 → 行集未变 → 零读取）
round 3: ZCode 0 ms — 1/0/0（指纹未动 → 快路径 → 零 SQL）
```

**适用条件（2026-11-14 独立审查实测补充）**：round 2/3 的 13/0 ms 要求**写端在轮间安静**。
ZCode CLI 持续提交期间（`-wal` 钉在 ~4 MB autocheckpoint 阈值、mtime 不停跳、completed 行数在涨），
SmokeScan 行为标记逐位一致（1/1/1 → 1/0/0）但量级完全不同：空闲轮 1,263–1,522 ms 而非 13/0 ms——
写端风暴不断重置 WAL、使连接级 32 MB 页缓存失效，探针退化为 ~1.4 s 全表扫描（与修复前空闲
p50 1,401 ms 统计上无差别）。审查者用隔离连接三次计时 1.377 s → 0.788 s → 0.012 s：暖连接探针
~12 ms 是真的，但只在写端停歇窗成立。**行为锚点不变：changed=0、零读取——修复保证的是
"账面没动就不重读数据"，不是"探针恒快"**。

结论：两级探针仍是当前最优（空闲轮两次 stat、活跃轮一次探针 + 暖页缓存；页缓存收益是瞬态的，
以行为而非耗时为判定锚点），判定**不改**；正确性面上 I-049 审查发现的"部分读取 × 下轮删库"
漏洞 ZCode 同款，已登记待另卡（非本审计范围）。

## 5. 每源判定与理由

### 5.1 ZCode —— 不改（参照实现）

两级探针 + 持久只读连接 + 32 MB 页缓存（`45f1ff1`，I-042 延伸）已是六个源里最强的零变更口径：
空闲轮零 SQL、活跃轮一次轻量探针、探针失败保守全量。它同时是 I-049 的移植蓝本。已知边界
（行原地改值且 count/max(id) 不动不会被发现）登记在类注释——usage 行实务上只增不改（账本写入方语义）。
本批不动它的原因：唯一仍有载荷重读的场景（活跃期新增行）本来就是必须发生的全量读取，
进一步只能做行级增量——那是 I-043 被否的方向（跨轮拼接破坏去重口径，信任锚不容许）。

### 5.2 OpenCode —— 改过（I-049 已落地）

唯一有本机真机量级支撑的性能卡：载荷表 + WAL 库 + 写端活跃期"指纹必变 → 全量重读 29 MB"。
改后行为见 4.2。明确不做（卡内已划）：不做行级增量、不引 `-shm` 指纹（读端触碰会自激重扫）、
不抽公共探针组件（ZCode/OpenCode 重复实现，OpenCode 注释指向 ZCode 蓝本、无回指）。

### 5.3 Kilo —— 不改（无数据，留证待重开）

本机从未安装/使用 Kilo：数据目录、环境变量、应用、CLI、配置残留、全盘 `mdfind` 均无
（I-050 卡"取证复核"小节有逐条命令与结果，2026-11-14 03:30 UTC）。载荷 = 0，低于
"载荷 < 2 MB 且首轮 < 20 ms"门槛的极限情形——**无可测对象**，探针正确性（原地覆盖开放问题）
与收益都无从验证。按 I-050 wontfix 收口；重开条件 = 本机出现真实 Kilo 数据或拿到装过 Kilo 的机器，
实现蓝本届时照 I-049 落地后的 `OpenCodeSqliteAdapter` 同构移植。

### 5.4 Goose —— 不改（合成 5k 会话实测 ≈20 ms）

读 `FROM sessions` 每会话一行的标量聚合行（固定十列，行宽 ≈89 B）：没有行内大 JSON 载荷，
行数 = 会话数（现实量级百～千，重度用户千级），全量重读成本 = 4 µs/行 × 行数 ≈ 个位数到 20 ms。
活跃写端期（`-wal` 变动触发重读）的收益上限 = 每 5 分钟省一次这个量级的重读，不足以引入
持久连接与新状态机。空闲轮已由文件指纹短路（0.4 ms）。数据出处 = 合成 fixture（本机无真机数据），
实测方法与数字见 3.4 / 4.1。

### 5.5 Hermes —— 不改（合成 5k 会话实测 ≈20 ms）

与 Goose 同构：`sessions` 每会话一行（固定十二列，行宽 ≈83 B），合成实测全量重读 ≈20 ms、
空闲轮 0.1 ms。判定理由同 5.4。

### 5.6 Antigravity —— 改过（I-048 已落地，正确性修复）

改前指纹只看主库，而 Antigravity 会话库是 WAL 模式（证据等级 A，两份独立一手实测互证）：
- antigravity-cli [#1486](https://github.com/google-antigravity/antigravity-cli/issues/1486)
  （CLI 1.5.3 / Windows 11，ETW 内核追踪）：**10 分钟写 130.8 MB 进 `-wal`、仅 6.2 MB 进主库**；
  `page_size`=32768 × `wal_autocheckpoint`=1000 页 → **32 MB 累计才触发一次自动 checkpoint**；
  run 结束遗留 32.8–33.1 MB WAL（[#683](https://github.com/google-antigravity/antigravity-cli/issues/683)）。
- Google AI Developers Forum [主题 187421](https://discuss.ai.google.dev/t/antigravity-is-quietly-writing-30-gib-day-to-my-ssd-a-wal-level-breakdown-of-where-it-goes/187421)
  （IDE 2.19.1 / Windows 11）：作者对 90 MB 会话库**逐帧校验 WAL**（salt / 滚动 checksum / commit
  边界，1,188/1,188 帧合法——WAL 帧结构只有真 `-wal` 才有），按帧归属统计
  gen_metadata 48.3% / steps 17.2% / steps 索引 27.5%。
- 修复（`fc65e65`）：指纹改 `SqliteFingerprintFiles`（主库 + `-wal`）+ `CaptureWithOptional`
  （边车消失 = checkpoint 收尾也是账面变化，哨兵戳语义）；`-shm` 不进指纹（读写共享派生索引，
  进指纹自激重扫）。回归锁：仅动 `-wal` mtime → 必重读（改回旧写法测试 RED）。
- 性能面（活跃期全量重读的成本）未优化也**未取证**（本机无数据）；若日后有数据且量级值得，
  参考 I-049 形态另开卡。

## 6. 未取证项清单与重开条件

| # | 未取证项 | 现状与重开条件 |
|---|---|---|
| 1 | Goose / Hermes 真机数据 | 本机无安装（SmokeScan `0/0/n/a`）；判定基于合成 5k 会话。真实用户 sessions 行数分布未知——真实库达**数万行级**（全量重读进入百毫秒级）时按 3.4 复测并重开 |
| 2 | Kilo 一切数字 | 零数据（I-050 留证）；拿到装过 Kilo 的机器按 I-049 蓝本重开 |
| 3 | Antigravity 本机量级 | 本机无安装；"会话库行数量级 / 全量重读成本"未取证。checkpoint 行为已由 #1486 回答（32 MB 阈值 + 遗留 WAL），活跃期主库可长期不动 |
| 4 | OpenCode "usage 行只增不改" | 是假设而非取证（I-049 P2-3b 登记）：行原地改值且 count/max(rowid) 不动探针失明。验证途径 = 真机对账（`npx ccusage@20.0.29`，tokens 精确、cost ≤0.5%）+ I-052 量尺日志核对 |
| 5 | ZCode 同款边界与漏洞 | 行原地改值边界登记在类注释；"部分读取 × 下轮删库"漏洞（I-049 审查发现的 ZCode 同款）已登记待另卡 |
| 6 | 字节级采集量 | I-052 量尺 macOS 记 `n/a`（`getrusage` 无字节字段）——首轮 4,109 ms（2026-11-14 本机 SmokeScan 自跑；I-052 卡内自跑为 4,274 ms，同为整轮含临时库初始化）的字节构成只能靠 Windows/Linux 实机或建卡调研的手工采样（Cindy 全量轮 read_ops 41.8k→56.1k / read_bytes 162.7→217.4 MB） |
| 7 | 两级探针修复后的 ZCode 生产行为 | app.log 无 11-13 之后的样本（app 未重启）；现以 SmokeScan 三轮行为（1575 ms → 13 ms → 0 ms）为代表，**13/0 ms 只在写端安静窗成立**（写端持续提交期探针仍 ~1.4 s，适用条件见 4.3），日后有生产样本值得复核一遍 p50 |

## 7. 判定的边界（明确不做）

- **本文数字是特定机器、特定库状态、特定日期的量级，不构成性能承诺。** ZCode/OpenCode 账本
  随使用持续增长，引用必须带日期；判定锚点是**行为**（空闲轮零读取、活跃期无关表写入零读取、
  写端活跃期不再读陈旧账），不是绝对耗时。
- 合成 fixture 的 4 µs/行是本机 Release 页缓存热态的数字，不同磁盘/内存压力下会漂移
  （同机单轮波动即 ±50%），只用于量级论证。
- 不做行级增量（I-043 同源风险）、不做 `-shm` 指纹、不抽公共探针组件——三个"不做"的理由
  已在 I-049/I-050/I-051 卡内，此处不重复展开。
- 附录（I-053，JSONL 读取缓冲 4 KB → 64 KB）：同批量尺收口，端到端中位数改善 8.8% < 10% 门槛、
  真实收益 ≈4% 且噪声 ±25%+，按「不做」回滚——SQLite 六源与本卡无关，仅登记族谱。

## 8. 数字速查

```
ZCode       生产（修复前，11-01→11-11，5087 轮）   空闲轮 p50 1401 ms / max 63 s（探针全表 SCAN）
            库                                     1.27 GiB / model_usage 35,872 行 / 表 26 MB
            SmokeScan（修复后）                    首轮 1575 ms → 活跃轮 13 ms（零读取）→ 空闲 0 ms（零 SQL）
OpenCode    改前活跃轮                             每轮全量 29 MB 载荷 + 1016 次 JSON 解析（132 ms 首轮）
            改后                                   首轮 86–124 ms；空闲 0–1 ms；无关表写入探针 1 次零读取
Goose       合成 5k 会话                           全量重读 ≈18–20 ms / 载荷 0.45 MB / 空闲 0.4 ms（4 µs/行）
Hermes      合成 5k 会话                           全量重读 ≈20–21 ms / 载荷 0.42 MB / 空闲 0.1 ms
Kilo        —                                      无数据（载荷 0，留证待重开）
Antigravity 公开取证（#1486）                      10 分钟 130.8 MB 落 -wal vs 6.2 MB 落主库；checkpoint 阈值 32 MB
```
