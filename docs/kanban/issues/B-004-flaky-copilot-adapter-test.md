---
id: B-004
title: flaky：CopilotSessionAdapterTests.Skips_malformed_lines_and_non_shutdown_events 间歇多计 LinesSkipped
type: bug
priority: P2
size: S
status: todo
created: 2026-10-13
updated: 2026-10-13
blocked: none
---

## Goal

定位并修复 `CopilotSessionAdapterTests.Skips_malformed_lines_and_non_shutdown_events` 的间歇性失败：`diagnostics.LinesSkipped` 期望 2、偶发实测 3，使 `dotnet test CCUsage.sln` 的全绿基线不可稳定复现。

## Background

- 2026-10-13 D-004/D-005 独立审查 agent 首次实跑全量时抓到实锤：`Expected LinesSkipped: 2, found: 3`（tests/CCUsage.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs:92），单跑该项目与第二次全量均通过。
- 同日 PM 侧也观察到 2 次 Infrastructure.Tests 间歇失败（当时未能抓到用例名，之后 13 次连跑全绿）——高概率同一用例。两次都发生在并行构建/测试的高负载窗口，但**未证实**因果。
- 已排除：临时目录非唯一（CreateTempDir 用 Guid 命名，无跨用例路径复用）。
- 待查方向：① fixture 是 4 行固定内容，"多 1 行 skipped"最可能来自适配器多扫到了文件或把某一行分类漂移（malformed ↔ skipped 的预过滤判定）；② 适配器对 home 参数的解析是否有兜底路径会扫到真实用户目录（`~/.copilot`）；③ 并行测试下文件系统可见性/写入缓冲是否可能造成半行读取；④ `WriteEvents` 的 join("\n") + 尾随换行是否在某种读取路径下被拆成空行计 skipped。
- 修复纪律：先在本地稳定复现（可加压力循环跑），找到机制再改；禁止用放宽断言的方式"修"。

## Acceptance Criteria

- [ ] 根因定位成文（Development Log：机制、触发条件）
- [ ] 修复后连续 30 次全量 `dotnet test CCUsage.sln` 无该用例失败（或引入确定性复现→修复→回归测试锁定）
- [ ] 不改变 I-008 适配器的既有解析语义（malformed/skipped 分类口径以蓝本为准）

## Subtasks

- [ ] 复现与根因定位
- [ ] 修复 + 回归保护

## Dependencies

- none

## Test Cases

### TC-001: 稳定性

Steps:
1. 循环执行 `dotnet test CCUsage.sln`（≥30 次）与该用例压力单跑。

Expected:
- 零失败；若修复引入了确定性复现手段，一并记录。

## Development Log

### 2026-10-13

- 建卡（D-004/D-005 审查轮抓到实锤 + PM 侧 2 次未命名观察，证据如 Background）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/CCUsage.Infrastructure.Tests/CopilotSession/CopilotSessionAdapterTests.cs`、`src/CCUsage.Infrastructure/CopilotSession/`
- How to run/verify: TC-001
- Results: （待执行）
