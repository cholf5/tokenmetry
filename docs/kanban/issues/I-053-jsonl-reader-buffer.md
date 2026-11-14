---
id: I-053
title: JSONL 读取缓冲 4 KB → 64 KB——大文件解析的系统调用数降一个量级（先量后改）
type: infra
priority: P3
size: S
status: wontfix
created: 2026-11-13
updated: 2026-11-14（wontfix）
blocked: none
resolution: 2026-11-14 按「不做」收口（先量后改的既定退出条件）——端到端中位数改善 8.8% < 10% 门槛；真实收益 ~4% 且噪声 ±25%+；DSH（占整轮 ~55%）因逐帧解压流完全量不到差别。改动已全部回滚，实测数字留卡内，未来重开应只对 Claude/Codex 类直读源做、跳过 DSH。
---

## Goal

把 JSONL 直读路径上的 `StreamReader` 缓冲从默认值提到 64 KB，减少大文件解析时的读取系统调用
次数（IOPS）；改前先量，量不出差别就按"不做"收口。

## Background（已对照源码核实）

- 现有构造点（默认缓冲 = `StreamReader` 的 1024 char，底层 `FileStream` 再自带 4 KB 缓冲，
  合起来每次读取约 4 KB）：
  - `src/Tokenmetry.Infrastructure/Jsonl/IncrementalJsonlScanner.cs:45`
    —— **九个 JSONL 源共用**（Claude Code / Codex / Qwen / Kimi / Copilot / Grok / …）：
    `new StreamReader(AgentLogReader.OpenRead(path), Encoding.UTF8)`；
  - `src/Tokenmetry.Infrastructure/CopilotSession/CopilotSessionAdapter.cs:236`（自己的 reader 工厂）；
  - `src/Tokenmetry.Infrastructure/DshSession/DshSessionAdapter.cs:110`；
  - `src/Tokenmetry.Infrastructure/CodexJsonl/CodexJsonlAdapter.cs:50-51`（zstd 解压流外套 StreamReader）。
- 为什么可能有收益：逐行 `ReadLine` 的实现是"填满缓冲 → 扫描"，缓冲越大系统调用越少：
  一份 100 MB 的会话文件按 4 KB 计 ≈ 25k 次读取，按 64 KB 计 ≈ 1.6k 次（−94%）。
  本次调研在 Cindy 源上实测过同一条算术：`read_ops` 51k/s 与 "4 KB 缓冲 × 205 MB/s" 完全吻合。
- 为什么优先级只给 P3：解析成本大头是 JSON 反序列化与映射，不是 syscall。本机实测
  Claude Code 首轮 14 个文件 165 ms、Codex 19 个文件 248 ms——即使 syscall 省掉 90%，
  端到端也只有几个百分点的可能收益。**因此本卡的验收里有一条是"量不出差别就关掉"。**

## 定案方向

1. **先量**：用 `tools/Tokenmetry.SmokeScan` 记录改动前 Claude Code / Codex 源的首轮耗时与
   `read_ops`（若 I-052 已落地则直接看它；否则用 `pr`/`GetProcessIoCounters` 手工采一次）。
2. **再改**：把上述四处统一改为 64 KB 缓冲（常量集中在一处，例如
   `AgentLogReader.BufferSize = 64 * 1024`，注释写取值依据与"为什么不是 1 MB"——
   每个打开的文件多占 64 KB 常驻，峰值 = 并发打开数 × 缓冲，1 MB 会让多文件场景白白吃内存）。
3. **再量**：同一台机器同一份数据复测，把两组数字写进卡内 Development Log。
4. 明确不做：不改解析算法、不改缓冲外的任何路径（比如 `File.ReadAllText` 的那批单文件 JSON 源
   ——它们读整文件，缓冲无意义）；不为不同文件大小做自适应缓冲（过早优化）。

## Acceptance Criteria

- [x] 改动前基线数字（Claude Code / Codex 首轮耗时 + read_ops 或等价指标）记录在卡内
      （macOS 量尺 n/a（I-052），按执行指示以端到端耗时 + IngestionFileLogger 逐源 ms 为等价指标）
- [x] 四处统一为同一个 64 KB 常量（不散落魔数），注释写明取值依据
      （已实现并实测，后按上一条退出条件回滚——实现形态与注释文本留档于 Development Log）
- [x] 改动后同条件复测数字记录在卡内
- [x] 若端到端耗时改善 < 噪声（建议口径：两组各跑 3 次取中位数，差值 < 10%）→ **回滚并关闭本卡**，
      卡内写清"为什么不值得改"（这条是有意的退出条件，不是失败）
      （实测中位数差 8.8% < 10%，已触发：全部改动回滚，裁决理由见 Development Log）
- [x] `dotnet test Tokenmetry.sln` 0 失败；`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`
      记录数/状态与改动前逐位一致（回滚后 2026-11-14 复验：6 测试项目全绿；记录数 2650/2931/5811、
      daily 49、status Ok 与改动前逐位一致）

## 开放问题

- Codex 的 zstd 流：加大 StreamReader 缓冲是否真的减少底层读取，取决于解压流的实现（可能已经在
  内部做大块读取）。执行时可对 Codex 单独判断：若量不到差别，只改其它三处并在卡内注明。

## Dependencies

- 无硬依赖。文件面：`Jsonl/` + `CopilotSession/` + `DshSession/` + `CodexJsonl/`，
  与 I-048 / I-049 / I-050 / I-052 并行安全。

## Related

- I-004 / I-001（Codex / Claude Code JSONL 适配器）、I-005（共享扫描器接线）
- I-052（IO 量尺：本卡的"先量后改"最好用它的输出）
- I-051（审计文档：本卡的结论应回填一行）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 本卡不属于 SQLite 批次，但同属"采集管道 IO"这一族，且是同一轮调研里顺带坐实的事实
  （Cindy 源的 4 KB 读放大算术），故一并建卡、一并交给执行者，但优先级排在本批最后。
- 特意设了"量不出来就回滚关掉"的退出条件：这类"看起来应该有收益"的微优化最容易变成
  无人验证的代码噪音。

### 2026-11-14 先量后改 → 量不出端到端差别 → 回滚（按「不做」收口）

- **测量方法**：macOS arm64 本机（本机无 docker，I-052 量尺在 macOS 记 n/a，read_ops 不可得），
  按执行指示以端到端耗时 + `IngestionFileLogger` 逐源 ms 为等价指标。口径：每组 3 次
  `dotnet run --project tools/Tokenmetry.SmokeScan -- 1`（每次全新进程 = 独立全量首轮），
  **页缓存热态**（macOS 无法免 sudo 清缓存，两组同条件可对比、绝对值偏乐观）。
  除卡点名的 Claude Code / Codex 外加测 DSH——它是本机最大的 JSONL 读取源（69 文件），
  且 `DshSessionAdapter` 本就是四处改动点之一。
- **基线（改动前，3 次）**：

  | 首轮指标 | run 1 | run 2 | run 3 | 中位数 |
  |---|---|---|---|---|
  | 端到端 total | 3447 ms | 2668 ms | 2996 ms | **2996 ms** |
  | Claude Code（14 文件全读） | 167 ms | 167 ms | 167 ms | **167 ms** |
  | Codex（19 文件全读） | 256 ms | 252 ms | 251 ms | **252 ms** |
  | DSH（69 文件全读） | 1239 ms | 1648 ms | 1681 ms | **1648 ms** |

- **改动**（实测后已回滚，形态留档）：`AgentLogReader.BufferSize = 64 * 1024` 常量一处，
  注释写明取值依据（100 MB 文件 4 KB ≈ 25k 次 read → 64 KB ≈ 1.6k 次，−94%；1 MB 档 syscall
  收益趋零但每打开文件多占 ~1 MB 常驻、峰值 = 并发打开数 × 缓冲）与"为什么不是 1 MB"。
  四处 StreamReader 构造统一传 `detectEncodingFromByteOrderMarks: true`（与原行为一致）+
  `bufferSize: AgentLogReader.BufferSize`：`IncrementalJsonlScanner` 默认 reader 工厂（九源共用）、
  `CopilotSessionAdapter`、`DshSessionAdapter`（直读/解压流同参）、`CodexJsonlAdapter`
  （zstd 流与直读流同参，两分支都改）。`File.ReadAllText` 批与自适应缓冲未动（卡内明确不做）。
  构建零警告零错误。
- **复测（64 KB 缓冲，同条件 3 次）**：

  | 首轮指标 | run 1 | run 2 | run 3 | 中位数 | vs 基线 |
  |---|---|---|---|---|---|
  | 端到端 total | 4424 ms | 2575 ms | 2733 ms | **2733 ms** | **−8.8%** |
  | Claude Code | 155 ms | 125 ms | 126 ms | **126 ms** | −24.6% |
  | Codex | 199 ms | 173 ms | 173 ms | **173 ms** | −31.3% |
  | DSH | 1814 ms | 1648 ms | 1249 ms | **1648 ms** | 0% |

- **裁决：回滚**。端到端中位数差 8.8% < 10% 阈值，触发卡内退出条件。为什么不值得改：
  - 受益源的真实量级太小：Claude+Codex 合计稳定省 ~120 ms（167+252=419 → 126+173=299，
    两源基线跑分极稳、改善可信），但那只占全轮 ~3 s 的 4%——实测端到端 8.8% 的"改善"里
    一半以上是 DSH 轮间噪声（基线 1239–1681 ms、复测 1249–1814 ms，单源波动 ±27%）凑出来的。
  - DSH 完全量不到差别（0%）：它的 zstd 逐帧解压流（`DshZstdLogStream`）内部缓冲决定 syscall
    粒度，StreamReader 缓冲加多大都传导不到盘上——印证卡内"解压流可能已在内部做大块读取"
    的怀疑，且 DSH 占本机整轮读取耗时 ~55%（只算 JSONL 三源则 ~80%），它不动，
    端到端天花板就只有几个百分点。
  - Copilot 适配器本机无数据，四处改动有一处不可测。
  - 收益 ~4% 且噪声 ±25%+ 的环境下，这个改动的可验证收益撑不起它的常驻成本
    （每打开文件 +60 KB）与代码噪音——正是建卡时点名的"看起来应该有收益"型微优化。
- **卡内开放问题（Codex zstd 流）的回答**：实测方向与设问相反——Codex（.jsonl 直读 + .jsonl.zst
  解压混跑）量到了 −31%，不需要"只改其它三处"的分支；真正量不到差别的是 DSH 的解压路径。
  两类解压流行为不同（ZstdSharp DecompressionStream 会吃 StreamReader 的大块读请求，
  DSH 的逐帧流不会），如未来重开此卡，只对 Claude/Codex 类直读源做、跳过 DSH 是更准的切口。
- **一致性**：六轮实测（基线 3 + 复测 3）记录数逐位一致（Claude 2650 / Codex 2931 / DSH 5811 /
  daily summaries 49 / facts 151，status 全 Ok 无 Malformed/Invalid 变化）。回滚后
  `dotnet test Tokenmetry.sln` 0 失败（271+49+506+50+836+666 全绿），SmokeScan 三轮
  daily 49、逐源记录数与改动前逐位一致，退出码 0。
- 不 commit（按执行指示）；status/board 留给 PM 收口。
