---
id: I-052
title: 采集 IO 量尺——SmokeScan 每轮报进程读取字节/次数量，让"每轮读了多少"可量化
type: infra
priority: P3
size: S
status: done
created: 2026-11-13
updated: 2026-11-14（done，三平台实机验证齐）
blocked: none
---

## Goal

给 `tools/Tokenmetry.SmokeScan` 补一条**每轮进程级读取量**输出（读取字节数 + 读取次数），
让"某个源在某个刷新轮里到底从盘上读了多少"变成一条可复现的命令，而不是靠临时采样脚本。

## Background（已对照源码核实 / 本次实测）

- 现状：`tools/Tokenmetry.SmokeScan/Program.cs` 只打印每轮总耗时 + 记录数，逐源明细来自
  `IngestionFileLogger` 的 `app.log`（`source [X] done in N ms — files discovered/read/changed: a/b/c,
  records: r, status: s`）。**没有任何字节口径。**
- 今天发现的两类真实问题都必须靠字节量级才能说清：
  ① Cindy 源的"全有全无"缓存 → 每个刷新周期整目录重读 399 MB（已修，见 Development Log）；
  ② SQLite 源的"活跃写端期全量重读" → 每轮读多少载荷（I-049：OpenCode 实测 28.9 MB/活跃轮）。
  没有量尺时，这两件事都要靠临时的 `GetProcessIoCounters` 采样脚本才能看见（本次调研就是这么做的）。
- 同一台机器上"读取字节"会受页缓存影响（命中缓存也计入 read 调用但不一定有真实磁盘 IO），
  所以指标必须**同时**给 `read_bytes` 与 `read_ops`：`read_ops` 稳定反映"我们发起了多少次读取"，
  `read_bytes` 反映"请求了多少数据"。前者不受页缓存噪声影响，更适合做趋势护栏。
- 参照实测（本次调研手写采样，100 秒窗口，Cindy 全量重扫轮）：`read_ops` 41.8k→56.1k、
  `read_bytes` 162.7→217.4 MB，与"4 KB 缓冲 × 205 MB/s ≈ 51k IOPS"的算术一致。

## 定案方向

1. **只在工具里做，不动生产代码**：`tools/Tokenmetry.SmokeScan` 新增一个 sampler，
   每轮扫描前后各取一次进程读取计数，打印
   `round N: total X ms | read_bytes A (Δ +a) | read_ops B (Δ +b)`。
2. **平台实现按可得性分级**（不要引第三方包、不要 P/Invoke 满天飞）：
   - Windows：`GetProcessIoCounters`（kernel32，`ReadOperationCount` / `ReadTransferCount`）；
   - Linux：`/proc/self/io` 的 `syscr` / `rchar`；
   - macOS：`proc_pid_rusage` 不可得时**直接打印 `n/a`** 并继续跑（本机是 macOS，绝不因为量尺
     缺失让冒烟失败）。
   三处实现放在工具项目内的一个 `ProcessIo.cs`，失败一律降级 `n/a` + 一句 stderr 说明。
3. **零行为影响**：不改扫描顺序、不改日志格式、不新增依赖、不影响 `SmokeScan` 的"零副作用"
   契约（facts 仍落临时库、结束自删）。
4. 明确不做：不做逐源字节归因（要动 `LocalIngestionDataSource` 的源循环 + 全部适配器，
   与本批其它卡冲突，且逐源归因用"行数/文件数"已有近似口径）；不设自动阈值断言
   （页缓存与 GC 噪声会让阈值测试变成 flaky 源）。

## Acceptance Criteria

- [x] `dotnet run --project tools/Tokenmetry.SmokeScan -- 3` 在 macOS / Linux / Windows 任意一台
      都能跑完三论，输出每轮 `read_bytes` / `read_ops` 与逐轮增量（macOS 若不可得则 `n/a`）
      （2026-11-14 macOS 实机验证通过，见 Development Log）
- [x] 三平台各验证一次（可行则打印真实计数，不可得则 `n/a` 且进程正常退出 0）
      （2026-11-14 三平台齐：macOS `n/a` + 退出 0 ✅、Windows `GetProcessIoCounters` 真实计数 + 退出 0 ✅、
      Linux 真实内核 `/proc/self/io` 真实计数 + 退出 0 ✅，见 Development Log「2026-11-14 Windows + Linux 实机验证」）
- [x] 用该量尺复现一条既有事实并写进卡内 Development Log：**空转轮（无文件变动）增量接近 0**，
      以及任一"全量重扫轮"的量级（例如临时把 Cindy 缓存改回全量，或直接引用本次实测数字）
      （macOS 量尺 n/a，字节级新数字拿不出：按允许口径以文件级等价证据 + 建卡日引用数字落地，见 Development Log）
- [x] `dotnet test Tokenmetry.sln` 零改动全绿（工具项目不在测试矩阵内，但不得破坏构建）
      （2026-11-14 全量测试 0 失败，工具构建 0 警告 0 错误）
- [x] 工具输出不含任何会话内容（隐私红线：只有数字与已有路径/计数）

## 开放问题

- 是否值得把量尺接进 `app.log`（生产可观测）？——本卡**不做**：生产日志是给用户看诊断的，
  每轮写两条 IO 计数的收益低于噪声成本；工具口径足够支撑"改动前后对比"。
- `read_bytes` 在 macOS 上的替代：`getrusage(RUSAGE_SELF)` 没有字节字段；`libproc` 的
  `proc_pid_rusage` 需要 P/Invoke。执行者可以先只做 Windows + Linux，macOS 留 `n/a`（卡内已允许）。

## Dependencies

- 无。文件面只有 `tools/Tokenmetry.SmokeScan/`，与 I-048 / I-049 / I-050 / I-053 全部并行安全。

## Related

- I-045（采集管线文件日志：逐源 ms/计数的既有可观测面）
- I-049 / I-050（本批改动卡：收益验证用本卡的量尺）
- I-051（审计文档：本卡的输出应被引用为"测量方法"）
- `docs/perf-investigation-2026-10-28.md`（历史测量方法的出处）

## Development Log

### 2026-11-13 建卡

- 用户指示："剩下的别的 SQLite 源一次性调研清楚，然后开一批看板卡"，供其他智能体执行。
- 建卡动因是本次调研的痛点：为了判断 Cindy 源"每个周期读 430 MB"是否真存在，只能临时写
  `GetProcessIoCounters` 采样；同一件事这个仓库已经发生过两次（另一次是 2026-10-28 的 perf 批次）。
  量尺做完，后续任何"这个源是否在偷读盘"的问题都变成一条命令。
- 刻意把范围压到工具项目：生产侧逐源字节归因会横穿 `LocalIngestionDataSource` 与全部适配器，
  与同批 I-049 / I-050 撞文件，收益也不成比例。

### 2026-11-14 实施

- **落地**（`tools/Tokenmetry.SmokeScan/`，生产代码零改动）：
  - 新文件 `ProcessIo.cs`：平台分级实现。Windows = kernel32 `GetProcessIoCounters`
    （`ReadTransferCount` / `ReadOperationCount`，用 `GetCurrentProcess()` 伪句柄 `(HANDLE)-1`，
    P/Invoke 全工具仅此一处）；Linux = `/proc/self/io` 的 `rchar` / `syscr`；
    macOS = 直接不可得（`getrusage` 无字节字段，`proc_pid_rusage` 需额外 P/Invoke，
    按卡内开放问题留空）。任何失败降级 n/a + stderr **一次性**说明（不刷屏）。
  - `Program.cs`：轮循环前后各取一次计数，既有行尾追加
    ` | read_bytes A (Δ +a) | read_ops B (Δ +b)`（`total` / `daily summaries` 原样保留——
    I-053 的记录数逐位比对依赖它）；不可得时该段为 ` | read_bytes n/a | read_ops n/a`。
- **macOS 实测**（本机 arm64，`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`，退出码 0）：

  ```
  round 1: total 4274 ms, daily summaries 49 | read_bytes n/a | read_ops n/a
  round 2: total 33 ms, daily summaries 49 | read_bytes n/a | read_ops n/a
  round 3: total 18 ms, daily summaries 49 | read_bytes n/a | read_ops n/a
  ```

  stderr 一次性说明：`[I-052] 读取量尺不可得，read_bytes/read_ops 记 n/a（不影响扫描）：当前平台无进程级读取计数实现（macOS 暂留 n/a，见 I-052）`。
- **既有事实复现（AC 3）**：macOS 上量尺本身 n/a，字节级新数字本机拿不出，按允许口径降级落地：
  - 空转轮增量接近 0 → 以文件级等价证据落地：上面 round 2/3 即空转轮（无文件变动），
    全部源 `files ... changed: */0/0`、逐源 0-1 ms、I-042 unchanged shortcut 接管，
    总耗时 4274→33/18 ms——空转轮不重读任何文件，字节增量按同口径应近似 0。
  - 全量重扫轮量级 → 引用建卡日手工采样数字（read_bytes 162.7→217.4 MB、read_ops 41.8k→56.1k，
    100 秒窗口 Cindy 全量重扫轮；来源 = 本卡 Background 2026-11-13 调研，本机未重新实测）。
  - 2026-11-14 Windows 实机跑出**字节级新数字**（见下方验证节）：首轮全量
    `read_bytes +979.6 MB / read_ops +239,919`，随后两个空转轮各 `Δ +16 bytes / Δ +1 op`——
    "空转轮增量≈0" 在 Windows 侧从文件级等价证据升级为字节级实测；Linux 侧同样拿到
    `Δ +124 bytes / Δ +3 ops`（即审查补记①的自读噪声底）。
- **平台验证状态（2026-11-14 更新）**：macOS ✅（n/a + 退出 0）；Windows ✅（真实计数 + 退出 0）；
  Linux ✅（真内核真实计数 + 退出 0）。AC 2 已勾，详见下节。
- 隐私红线自查：stdout/stderr 只有计数、耗时与既有临时目录路径，无会话内容。
- **审查补记（2026-11-14 收口时）**：① Δ 口径含第二次采样自身的少量自读字节
  （Linux `/proc/self/io` 自读约几百字节），极小 Δ 是已知噪声底而非 0；
  ② AC 3 属降级落地（文件级等价证据 + 建卡日引用数字，非本机新产字节级数字），
  证据等级如上方"既有事实复现"条所注；③ 负 Δ 未做防御（进程计数单调，理论风险），
  首次 Linux 真机跑时可顺手加 `Math.Max(0, …)`（2026-11-14 Linux 实机未出现负 Δ，未加）。

### 2026-11-14 Windows + Linux 实机验证（补 AC 2）

- **Windows（HLPC01550-ZY / Win10 19045 / .NET 10）**：`dotnet run --project tools/Tokenmetry.SmokeScan -- 3`

  ```
  round 1: total 13008 ms, daily summaries 98 | read_bytes 979598593 (Δ +979567787) | read_ops 239951 (Δ +239925)
  round 2: total 86 ms, daily summaries 98 | read_bytes 979598609 (Δ +16) | read_ops 239952 (Δ +1)
  round 3: total 82 ms, daily summaries 98 | read_bytes 979598625 (Δ +16) | read_ops 239953 (Δ +1)
  ```

  stderr 无输出（走真实计数分支，不触发 n/a 说明），退出码 0。首轮 979.6 MB / 24 万次读与本机
  JSONL + DSH 全量首扫量级一致（DSH 单源 8.9 s 占大头）；空转轮 Δ 16 bytes / 1 op。
- **首验即抓到 Windows 专属崩溃（已修，仅工具侧）**：macOS 首验没暴露的坑——逐源明细回显用
  `File.ReadLines(logPath)`（默认 `FileShare.Read`），而生产 `IngestionFileLogger` 以
  `FileShare.Read` 持有写句柄；Windows 严格执行共享模式，读取方 share=Read 不容许写句柄共存 →
  `IOException: being used by another process` 未捕获上抛，**退出码非 0 且临时目录没清理**。
  Unix 不校验共享，所以 macOS/Linux 无感。修法：显式
  `new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)`，回显失败降级 stderr
  一句且不影响冒烟结论（生产 logger 零改动）。
- **Linux（腾讯云 VPS，Ubuntu / 内核 7.0.0-43-generic；自包含单文件发布到 /tmp 直接运行，机上未装 SDK）**：

  ```
  round 1: total 188 ms, daily summaries 1 | read_bytes 110427 (Δ +71765) | read_ops 140 (Δ +54)
  round 2: total 4 ms, daily summaries 1 | read_bytes 110659 (Δ +124) | read_ops 145 (Δ +3)
  round 3: total 0 ms, daily summaries 1 | read_bytes 110891 (Δ +124) | read_ops 150 (Δ +3)
  ```

  stderr 空、退出码 0：`/proc/self/io` 的 `rchar`/`syscr` 解析按预期工作。空转轮 Δ +124 bytes / +3 ops
  正是审查补记 ① 说的"采样自读噪声底"（每轮两次读 `/proc/self/io`）。该机无 agent 数据
  （20 源皆 NoData）故绝对值小，但计数单调性与 Δ 语义已坐实；验证产物已从 `/tmp` 删除。
- **WSL2 反例（顺带结论，比 docker 路线更早给出答案）**：本机 `Ubuntu-24.04`（内核
  6.18.40.1-microsoft-standard-WSL2）**没有** `/proc/self/io`（内核未开 `TASK_IO_ACCOUNTING`），
  跑出 `[I-052] 读取量尺不可得 … /proc/self/io 读取异常 FileNotFoundException` + 两轮 `n/a` + 退出 0
  ——Linux 侧降级分支同样验过。**推论：WSL2 / Docker Desktop 容器与本机 WSL 同内核，Linux 量尺
  恒为 n/a**；以后要 Linux 真实计数只能用真内核（VPS / 物理机），本机 docker 路线不必再试。
