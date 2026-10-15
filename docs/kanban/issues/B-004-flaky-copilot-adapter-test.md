---
id: B-004
title: flaky：CopilotSessionAdapterTests.Skips_malformed_lines_and_non_shutdown_events 间歇多计 LinesSkipped
type: bug
priority: P2
size: S
status: done
created: 2026-10-13
updated: 2026-10-13
blocked: none
---

## Goal

定位并修复 `CopilotSessionAdapterTests.Skips_malformed_lines_and_non_shutdown_events` 的间歇性失败：`diagnostics.LinesSkipped` 期望 2、偶发实测 3，使 `dotnet test Tokenmetry.sln` 的全绿基线不可稳定复现。

## Background

- 2026-10-13 D-004/D-005 独立审查 agent 首次实跑全量时抓到实锤：`Expected LinesSkipped: 2, found: 3`（tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs:92），单跑该项目与第二次全量均通过。
- 同日 PM 侧也观察到 2 次 Infrastructure.Tests 间歇失败（当时未能抓到用例名，之后 13 次连跑全绿）——高概率同一用例。两次都发生在并行构建/测试的高负载窗口，但**未证实**因果。
- 已排除：临时目录非唯一（CreateTempDir 用 Guid 命名，无跨用例路径复用）。
- 待查方向：① fixture 是 4 行固定内容，"多 1 行 skipped"最可能来自适配器多扫到了文件或把某一行分类漂移（malformed ↔ skipped 的预过滤判定）；② 适配器对 home 参数的解析是否有兜底路径会扫到真实用户目录（`~/.copilot`）；③ 并行测试下文件系统可见性/写入缓冲是否可能造成半行读取；④ `WriteEvents` 的 join("\n") + 尾随换行是否在某种读取路径下被拆成空行计 skipped。
- 修复纪律：先在本地稳定复现（可加压力循环跑），找到机制再改；禁止用放宽断言的方式"修"。

## Acceptance Criteria

- [x] 根因定位成文（Development Log：机制、触发条件）
- [x] 修复后连续 30 次全量 `dotnet test Tokenmetry.sln` 无该用例失败（或引入确定性复现→修复→回归测试锁定）
- [x] 不改变 I-008 适配器的既有解析语义（malformed/skipped 分类口径以蓝本为准）

## Subtasks

- [x] 复现与根因定位
- [x] 修复 + 回归保护

## Dependencies

- none

## Test Cases

### TC-001: 稳定性

Steps:
1. 循环执行 `dotnet test Tokenmetry.sln`（≥30 次）与该用例压力单跑。

Expected:
- 零失败；若修复引入了确定性复现手段，一并记录。

Results:
- PASS。修复后连续 30 次全量 `dotnet test Tokenmetry.sln` 全绿（每轮独立重建，5 个测试项目 0 失败），另加清理后终跑 1 次全绿。
- 确定性复现手段已落地并记录于 Development Log（shell 预设 `COPILOT_OTEL_FILE_EXPORTER_PATH` 后单跑该用例即 100% 复现失败签名）；修复后同一复现命令转为全绿，构成回归演示。

## Development Log

### 2026-10-13

- 建卡（D-004/D-005 审查轮抓到实锤 + PM 侧 2 次未命名观察，证据如 Background）。
- 追加（D-008 验证轮新实锤，同家族另一用例）：`CopilotSessionAdapterTests.Discovers_session_state_files_without_nested_session_files` 间歇失败——`FilesDiscovered` 期望 1 实得 2。**新候选根因（机制明确，待复现锁定）**：`CopilotOtelMergeTests` 多处以 `Environment.SetEnvironmentVariable(CopilotLogLocator.OtelExporterPathEnvVariable, …)` 修改**进程级** `COPILOT_OTEL_FILE_EXPORTER_PATH`（tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotOtelMergeTests.cs:711/742/801，finally 清空于 725/751/820），而 xunit 按集合并行跑测试类——`CopilotLogLocator.CollectSourceFiles` 对该环境变量"非空且指向既有文件即纳入发现"（CopilotLogLocator.cs 的 exporter-path 分支），并行窗口内 `CopilotSessionAdapterTests` 的扫描会多计入 merge 测试设置的导出文件。此机制可同时解释本卡 Background 里 `LinesSkipped` 型漂移（多出的文件行被计入 skipped）。修复方向：merge 测试改注入假 `environmentAccessor`（`CopilotLogLocator`/适配器已支持注入）而非动进程环境，或环境变量用例与目录发现用例归入非并行集合。D-008 不动 Infrastructure（超卡范围），仅记录证据。

### 2026-10-13（收口）

- **根因锁定（与候选机制一致，已复现实锤）**：跨测试类进程环境变量污染。`CopilotOtelMergeTests` 三个用例（两处文件发现 + 一处不可读导出文件）与 `CopilotSessionAdapterTests.Copilot_home_environment_variable_is_respected` 各自直接改进程级环境变量；xunit 默认按类并行，窗口重叠时并行扫描的默认定位器（无注入 accessor，读进程环境）会把污染方设置的导出文件计入发现清单——`{}` 行计 1 skipped（LinesSkipped 2→3），文件计数 +1（FilesDiscovered 1→2），与两个实锤签名逐位一致。此前"高负载窗口"只是并行重叠概率的表象。
- **确定性复现**：shell 预设 `COPILOT_OTEL_FILE_EXPORTER_PATH=<含 "{}" 行的文件>` 后单跑目标用例，`Expected LinesSkipped to be 2, but found 3` 100% 复现；同法复现 `FilesDiscovered to be 1, but found 2`。修复后同命令全绿（环境不再被读），构成回归演示。
- **修复（仅测试代码，零 src/ 改动）**：Copilot 测试族全部环境交互改为注入 `environmentAccessor`（生产读环境的同一缝隙，语义覆盖不变）：
  - `CopilotOtelMergeTests`：两处文件发现用例与不可读导出用例改注入假 accessor（`CreateAdapter` 增加 locator 可选参数），删除全部 `Environment.SetEnvironmentVariable` + try/finally；
  - `CopilotSessionAdapterTests`：`Copilot_home_environment_variable_is_respected` 改注入 accessor（用例名与语义保留——仍验证 COPILOT_HOME 分支被尊重）；
  - 收敛防御：两个测试类的 `CreateAdapter` 默认注入 `_ => null` 的 hermetic 定位器（附注释），使全族 ~54 个用例对 shell 继承环境（真实 Copilot CLI 用户可能带着 `COPILOT_OTEL_FILE_EXPORTER_PATH`/`COPILOT_HOME` 跑 `dotnet test`）也免疫；环境语义由上述专用注入用例继续覆盖。修复过程实测：只修污染方时，对抗环境下受害侧仍有 6 个精确计数用例失败（含两个原实锤）——hermetic 默认补齐后 54/54 全绿。
- **验证**：TC-001 执行 30 次全量循环全绿（结果记录于 Test Cases）；对抗场景（shell 预设导出变量 / `COPILOT_HOME`）下 Copilot 族 54/54 全绿；AC-3 满足——`git status` 仅两个测试文件变更，未触碰适配器与定位器语义。
- **家族性隐患记录（未修，超本卡范围）**：同构的进程级环境变量改动遍布其他适配器测试（Droid/Kimi/Gemini/Qwen/Amp/Pi/Codebuff/Grok/OpenClaw/Kilo/Goose/Hermes/OpenCode 等，其中 `GrokAdapterTests` 直接改 `USERPROFILE`/`HOME`，可影响所有走 `JsonlHomePath` 兜底的并行扫描）；本次观察到的实锤仅在 Copilot 族（其 exporter-path 分支无视 configuredHome、无条件读环境，故最易击穿）。建议后续以同模式（注入 accessor / hermetic 默认）逐族收敛，可开独立 Q 卡。
- 收卡：AC 全过，TC-001 PASS，无遗留 P0/P1 bug。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs`、`tests/Tokenmetry.Infrastructure.Tests/CopilotSession/CopilotOtelMergeTests.cs`（未改 `src/Tokenmetry.Infrastructure/CopilotSession/`）
- How to run/verify: TC-001；确定性复现 `COPILOT_OTEL_FILE_EXPORTER_PATH=<含 "{}" 行的文件> dotnet test Tokenmetry.sln --filter FullyQualifiedName~CopilotSessionAdapterTests`（修复前必失败、修复后必通过）
- Results: 复现实锤通过；修复后 30 次全量 + 终跑 1 次全绿；对抗场景 Copilot 族 54/54；grep 确认 Copilot 测试目录零 `Environment.SetEnvironmentVariable`。
