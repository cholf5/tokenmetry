---
id: I-053
title: JSONL 读取缓冲 4 KB → 64 KB——大文件解析的系统调用数降一个量级（先量后改）
type: infra
priority: P3
size: S
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
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

- [ ] 改动前基线数字（Claude Code / Codex 首轮耗时 + read_ops 或等价指标）记录在卡内
- [ ] 四处统一为同一个 64 KB 常量（不散落魔数），注释写明取值依据
- [ ] 改动后同条件复测数字记录在卡内
- [ ] 若端到端耗时改善 < 噪声（建议口径：两组各跑 3 次取中位数，差值 < 10%）→ **回滚并关闭本卡**，
      卡内写清"为什么不值得改"（这条是有意的退出条件，不是失败）
- [ ] `dotnet test Tokenmetry.sln` 0 失败；`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`
      记录数/状态与改动前逐位一致

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
