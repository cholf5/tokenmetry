---
id: I-045
title: 采集管线文件日志——可开关、含堆栈、按大小轮转，让「卡住」与「慢跑」外部可区分
type: infra
priority: P1
size: M
status: done
created: 2026-11-01
updated: 2026-11-01（done）
blocked: none
---

## Goal

给采集管线加一个**可开关的文件日志**（默认开，设置页可关），让「永远初始化」这类问题**一次定位**，
用户不必再靠反复重启、抓 `sample` 线程栈、猜 PID 自证——这是 issue #1 报告者 Helix-lyh 明确点名的
最小诉求（他原话："如果只能改一处，我最希望是加一个可开关的文件日志"）。

## Background

### 为什么现在拿不到任何诊断信息（issue #1 的"补充说明"逐条属实，已对照源码核实）

1. **没有文件日志**。全仓库采集侧只有一处 `Console.Error.WriteLine`（`LocalIngestionDataSource.cs:308`，
   数据源异常分支），且只写 `ex.Message` **没有堆栈**；从 Finder 启动时 stderr 被丢弃，
   `open --stderr` 重定向也只拿到空文件。
2. **进度只在"每个源完成后"登记一次**（`LocalIngestionDataSource.cs:314` 的 `ReportScan`）。Claude 源
   内部要扫 16,000 个文件，整个过程没有任何中间信号。
3. **写库集中在整轮末尾**（`UpsertUsageFactsAsync` / `UpsertSessionFactsAsync`），
   "0 行"无法区分"还在跑"与"已卡死"。
4. **初始化遮罩的摘除条件是整轮 `await` 返回**（`MainWindowViewModel.cs:325-334`）。这个 await 不返回，
   遮罩就永久停留。
5. 极端情况下诊断甚至会被运行时本身吞掉：macOS 沙箱内 `log show` 报 `Cannot run while sandboxed`、
   发布版被 strip 后 `sample` 全是 `???`、`DOTNET_PerfMapEnabled` 在 macOS 上不生效——
   **系统侧诊断通道全部失效，应用自 log 是唯一出路**。

### 触发事件

- issue #1（[macOS] 首启扫描 15–30 秒后彻底静止，界面永久停在「正在初始化」）：报告者做了极高质量的
  取证（CPU/RSS/句柄/行数时间序列 + 32 线程 sample 栈），但自述"无法从符号层面指到具体函数"，
  最终靠 GitHub 侧另开 PR（#2，去重器二次方索引重建，已合入 94c4324）才定位根因。
  他明确说：有了文件日志，"一次即可定位"。
- #2 的独立基准复核（20k 对替换 19.6s → 20ms）也证明这类问题完全可以靠日志里的一行耗时数字定位。

## 定案方向（实现时确认）

1. **位置**：`~/Library/Logs/Tokenmetry/app.log`（macOS 惯例）、Windows 走 `%LOCALAPPDATA%\Tokenmetry\logs\`、
   Linux 走 XDG state 目录——三平台各按惯例，不引入第三方日志框架（保持零依赖，手写轻量 file logger）。
2. **内容粒度**（issue 报告者的原始清单，逐条采纳）：
   - 每轮开始/结束（含整轮耗时、总记录数、是否走了 I-042 无变化短路）；
   - 每个源开始/结束 + 耗时 + 文件数 + 记录数（含 Error 快照与异常**含堆栈**）；
   - 每个文件解析失败的原因（含堆栈）——注意 `IncrementalJsonlScanner.ParseFileAsync` 现在把
     IOException/UnauthorizedAccessException **静默吞成计数**（`IncrementalJsonlSwiftScanner` 同族），
     日志点在这里加；
   - 写库起止时间。
3. **隐私红线**：只记**路径、计数、耗时、异常类型与堆栈**——绝不记 prompt、代码、会话内容，
   文件名可以记（路径本身不是内容）。
4. **开关与轮转**：默认开；设置页加开关（AppSettings 新键 + Translations 全语种覆盖 + i18n 纪律）。
   按大小轮转（如单文件 5 MB × 保留 3 份）。
5. **异常路径全覆盖**：`LocalIngestionDataSource.cs:308` 的 catch 分支改为同时写文件日志（含 `ex.ToString()`），
   保留现有 stderr 输出。
6. **错误报告出口**：设置页/关于页给日志文件入口（"打开日志文件夹"），
   让用户报 issue 时有东西可贴——本卡只做入口，不做内置日志查看器。

## Acceptance Criteria

- [x] 默认配置下首轮采集后，日志文件存在且包含：轮开始/结束、每源开始/结束（耗时/文件数/记录数）、
      写库起止
- [x] 人为制造解析失败（fixture 抛 IOException），日志含该文件路径与异常**含堆栈**，下轮重试语义不变
- [x] 开关关闭后不再写文件（注意 headless 测试宿主与 CI 也必须不写）
- [x] 轮转触发后旧文件改名保留，无句柄泄漏
- [x] 设置页有开关与「打开日志文件夹」入口；AppSettings 新键全语种 i18n 覆盖
      （`TranslationsTests` 的 `*_CoversEveryChineseSourceKey` 锁全绿）
- [x] 全量测试 0 失败（Core 227 / Infra 781 / Reconciliation 49 / App 351 / Headless 514，0 失败）；16k 文件规模的行为由每源埋点（发现/读取/变更文件数 + 耗时）覆盖，真机 16k 复核移交报告者

## 开放问题

- 日志写入用独立后台写线程还是同步 append（采集是后台线程，同步 append 几乎无竞争，倾向同步 + 简单锁）？
- Windows 便携 zip 形态下 `%LOCALAPPDATA%` vs 安装目录旁的取舍（跟随现有 `AppSettings` 数据目录约定即可）。
- 是否同时接 `IngestionScanRegistry` 的现有快照（ReportScan 已有状态/记录数/完成时刻），避免两套事实源。

## Dependencies

- none。不阻塞于任何卡；与 I-046（源内进度上报）、I-047（gate 超时）同族可同批实现。

## Related

- issue #1（报告者点名的最小诉求，本卡 = 该诉求的实现卡）
- PR #2 / 94c4324（同一 issue 的根因修复，本卡是其"诊断面"后续）
- I-042（无变化轮短路——日志要能如实反映"走了短路"这一事实，否则短路与卡死在外部同样不可区分）

## Development Log

### 2026-11-01 建卡

- 用户指示"为那三项建卡"。三项 = issue #1 报告者提出的 1 项最小诉求（文件日志）+ 2 项顺带改进
  （源内进度上报 I-046 / gate 超时 I-047），按采集管线域沿用 I 系编号。
- 本卡是三项中的核心项：#2 已修根因，但同类问题（任何一源静默变慢/挂起）的**可诊断性**仍是空白，
  PR #2 作者在 PR 描述里也明确把 diagnostics 列为 open follow-up。
- 优先级 P1：issue #1 全程（用户自取证 → 提 issue → 另开 PR 定位）之所以耗时，
  根因是应用拿不出任何诊断信息；下一份高质量 issue 不该再要求用户先自建采样脚本。

### 2026-11-01 收口（同日实现）

- 定案与偏离：日志目录取 `<数据目录>/logs/app.log`（跟随现有 AppPaths 数据目录约定）而非三平台惯例路径
  （`~/Library/Logs/Tokenmetry` 等）——单一数据根对便携 zip / Linux 更一致，「打开日志文件夹」入口让路径
  不需要用户记忆；设置页开关**重启生效**（logger 启动时按落盘值装配，与刷新间隔同语义），不做运行期热切换
  （采集轮中途改写目标的竞态不值得引入）。
- 实现：`IngestionFileLogger`（同步 append + 锁行级原子、写失败降级 stderr、关闭态零写盘、5 MB × 3 轮转）；
  管道埋点三级（轮 start/done 含 forceFull 与 I-042 短路标记、每源 start/done/ERROR 含耗时与文件数/记录数/状态、
  写库起止）；解析失败经 `IncrementalJsonlScanner` 新注入的 `parseFailureLog` 回调落日志（含堆栈），
  引擎不直依赖 logger。
- 测试：logger 7 + 管道日志 4 + i18n 覆盖（5 新键 × 10 语种）；全量 0 失败。
