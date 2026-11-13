---
id: I-052
title: 采集 IO 量尺——SmokeScan 每轮报进程读取字节/次数量，让"每轮读了多少"可量化
type: infra
priority: P3
size: S
status: todo
created: 2026-11-13
updated: 2026-11-13
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

- [ ] `dotnet run --project tools/Tokenmetry.SmokeScan -- 3` 在 macOS / Linux / Windows 任意一台
      都能跑完三论，输出每轮 `read_bytes` / `read_ops` 与逐轮增量（macOS 若不可得则 `n/a`）
- [ ] 三平台至少各验证一次"不可得 → `n/a` 且进程正常退出 0"（至少 macOS + Windows 实机各一次）
- [ ] 用该量尺复现一条既有事实并写进卡内 Development Log：**空转轮（无文件变动）增量接近 0**，
      以及任一"全量重扫轮"的量级（例如临时把 Cindy 缓存改回全量，或直接引用本次实测数字）
- [ ] `dotnet test Tokenmetry.sln` 零改动全绿（工具项目不在测试矩阵内，但不得破坏构建）
- [ ] 工具输出不含任何会话内容（隐私红线：只有数字与已有路径/计数）

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
