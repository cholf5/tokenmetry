---
id: I-054
title: 文件型源逐文件记账——六个源不再随单文件变化整批重读
type: infra
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13（done）
blocked: none
---

## Goal

把六个"整批戳集全等才复用"的文件型数据源改成**逐文件记账**：一个会话文件变了，只重读重解析
那一个文件，其余取用上轮产出。

## Background

### 病灶（已对照源码核实）

`FileStampMemo.TryReuse`（`JsonSource/FileStampMemo.cs:159` 起）的口径是**全有全无**：对整批文件
取戳后逐位比对，任一位不等即整体放弃缓存、全量重读。

```csharp
for (var index = 0; index < current.Length; index++)
    if (current[index] != last[index]) return false;   // 一个不匹配 → 整批作废
```

用户用 Agent 时**当前会话文件每回合都在长** → 每个刷新周期必然 miss → 每个周期全量重读整个
sessions 目录。受影响的六个源（当时仍是整批口径）：

| 源 | 适配器 | 数据形态 |
|---|---|---|
| Cindy | `CindyPiAdapter` | pi 会话 JSONL（扁平 sessions 目录） |
| pi-agent | `PiAgentAdapter` | pi 会话 JSONL（默认存储 + 命名存储多作用域） |
| Amp | `AmpAdapter` | 单层 JSON 线程文件 |
| Codebuff | `CodebuffAdapter` | `chat-messages.json` 数组 |
| Droid | `DroidAdapter` | `*.settings.json` 会话快照 |
| Gemini CLI | `GeminiCliAdapter` | 整文件 JSON + JSONL 混合 |

对照：`Jsonl/IncrementalJsonlScanner`（Claude Code / Codex / Qwen / Kimi / Copilot / Grok 等走的路径）
从 I-001 起就是逐文件记账（`_fileStates` + `_recordsByFile`），没有这个问题。

### 量级（两个来源，分别标注）

- **用户侧取证（另一会话，2026-11-13）**：Cindy 的 sessions 目录 338 文件 / 399 MB；进程 IO 采样
  （`GetProcessIoCounters`，1 秒粒度）抓到单次爆发 ≈110 000 次读操作 / 430 MB / 约 3 秒 CPU，
  摊到 1 分钟刷新即 ≈26 GB/小时。每次读均值 4 KB = `StreamReader` 缓冲，205 MB/s ÷ 4 KB ≈ 51k IOPS
  与该算术吻合。**本机无法复现这个量级**（见下），故此段按报告值记录，未由本卡作者重新测量。
- **本卡作者实测（macOS，本机）**：Cindy 源只有 11 个会话文件 / 451 条记录 →
  首轮 `11/11/11` 58 ms，改后第 2、3 轮 `11/0/0` 0 ms；改前第 2、3 轮恒为 `11/11/1`（整批重读）。
  量级虽小，但**机制与报告同源**，是同一处代码。
- 其它五个源在本机均为 0 文件（未安装），只有单测覆盖。

## 定案方向（已实现）

1. **六个源各自持逐文件记忆**（`路径 → (文件戳, 解析产出)`）：
   - Cindy / pi-agent：缓存条目带解析期配置（pi-agent 是 `PiStoreContext`，作用域根影响 `ProjectPath`
     故随条目校验；Cindy 用扫描根清单签名，变了即整体清空）；
   - Amp / Codebuff / Droid / Gemini CLI：共用新增的 `JsonSource/PerFileParseCache.cs`
     （`Probe / Store / Forget / RemoveMissing / TryGetStored`），四源的差异只剩"怎么从产出折叠记录"。
2. **零变更轮不再重建**：`FilesChanged == 0` 时直接复用上一轮的折叠产物（同实例，消费方只读），
   连排序/去重都不跑——与 `IncrementalJsonlScanner` 的零变更轮口径一致。
3. **记账准入与共享扫描器同口径**：stat 失败 / 打不开 / 读取中途失败的文件不落账（`Forget`），
   下轮重试整个文件；`_filesWithParseErrors` 按文件记"存在解析错误"，让坏行文件在**未被重解析**的
   轮次里也让 `Status` 保持 `Partial`（否则第二轮就被误报 `Ok`）。
4. **`FilesChanged` 语义收紧**：从"有变化就填 1"改为"本轮账面变化的**文件数**"（消失 + 新增/变更），
   与 `IncrementalJsonlScanner` 对齐。管道的无变化判定只看 `== 0` / `null`，不受影响；
   四处旧测试断言 `1`（删一个 + 加一个）改为 `2` 并在注释里写明"计数口径与共享扫描器一致"。
5. **`FileStampMemo.TryCapture`**：把单文件取戳从 `Capture` 里抽出来共用（`Capture` 改为调用它），
   逐文件记账的适配器不再各写一份 stat 逻辑。

## Acceptance Criteria

- [x] 六个源都改为逐文件记账；单文件变化时 `FilesRead == 1`（而非全部文件数）
- [x] 每个源都有「只改一个文件时其余文件不重解析」的回归测试（改回整批口径必 RED）
- [x] 文件消失 → `FilesChanged` 计 1、其余文件不重读；文件集变化后其余文件的记录仍在
- [x] 坏行文件在未重解析的轮次里 `Status` 仍为 `Partial`（Cindy / pi-agent 各一条测试）
- [x] 零变更轮：`FilesRead == 0`、`FilesChanged == 0`、记录复用上轮实例
- [x] 多作用域源（pi-agent）在 store 改名 / 重叠根迁移时清空记忆并重新归属（既有测试继续绿）
- [x] 全量 `dotnet test Tokenmetry.sln` 0 失败
- [x] `tools/Tokenmetry.SmokeScan` 三轮真机冒烟：首轮全量，后两轮零读取

## 已知边界 / 明确不做

- **不做行级增量**（message-grain 账本）：与 I-043 被否同源（去重口径跨轮不等价、tokens 要精确）。
- **不动 SQLite 源**（ZCode / OpenCode / Kilo / Goose / Hermes / Antigravity）：它们的"指纹整批比对"
  只有 1–2 个库文件，且活跃期全量重读是另一套机制 → 另开批次 I-048～I-053。
- **不动 `StreamReader` 缓冲**（4 KB 读放大仍在）：见 I-053。
- **不抽跨适配器的字段级记忆**：六个源各自持有记忆（作用域/去重语义差别大），共用件只到
  "按路径 + 戳记产出"这一层。
- 文件戳口径未变：同 mtime 粒度内的原地等长改写仍不可见（`FileStamp` 既有边界）。

## Related

- I-042（无变化轮次短路：`FilesChanged` 与零变更轮口径的来源）
- I-043（行级增量被否：本卡"不做"的依据）
- I-001 / I-004（`IncrementalJsonlScanner` 逐文件记账的先例实现）
- I-019（pi-agent）、I-021 之外的 Cindy 源、I-014 / I-015 / I-018 / I-010（Amp / Droid / Codebuff / Gemini CLI）
- I-048～I-053（同轮调研产生的 SQLite 批次与 IO 量尺）
- I-053（`StreamReader` 缓冲：本卡明确未动的读放大）

## Development Log

### 2026-11-13 建卡（补卡，事后记）

- 用户先指示"改"（实现），后指示"补卡，然后提交"。本卡按代码与测试现状回填，不是先有卡再实现。
- 优先级 P1：与 I-042 同级——它同样是"后台采集常态空转/重读"的直接修复，且有用户侧实测的
  量级支撑（另一会话报告 399 MB/周期）。
- 实现顺序：Cindy → pi-agent（同 `PiSessionParser`，缓存键要带解析期上下文）→ 抽
  `PerFileParseCache<T>` → Amp / Codebuff / Droid / Gemini CLI。

### 2026-11-13 收口（实现与验证）

- **测试**：新增 8 条（Cindy / pi-agent / Amp / Codebuff / Droid / Gemini CLI 各一条逐文件回归；
  Cindy 与 pi-agent 各一条"坏行文件在未重解析轮次仍 Partial"）；4 处 `FilesChanged` 期望
  `1 → 2`（删一个 + 增一个 = 两个文件，计数口径与共享扫描器对齐）。
- **全量**：Core 271 / Relay 50 / Reconciliation 49（19 跳过）/ Infrastructure 828（3 跳过）/
  App 506 / App.Headless 666，0 失败；`dotnet build Tokenmetry.sln` 0 warning。
- **真机冒烟**（`tools/Tokenmetry.SmokeScan -- 3`，只读数据源、facts 落临时库）：
  Cindy 首轮 `11/11/11` 58 ms → 第 2 轮 `11/0/0` 0 ms → 第 3 轮 `11/0/0` 0 ms。
  pi-agent / Amp / Droid / Codebuff / Gemini CLI 本机 0 文件，只有单测覆盖。
- **未验证的推测已如实标注**：用户报告的 338 文件 / 399 MB / 430 MB 每分钟未能在本机复现
  （Cindy 数据只有 11 文件），卡内按"报告值"记录，不冒充实测。
