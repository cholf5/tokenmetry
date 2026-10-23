---
id: B-008
title: ExpandHomePath 在 Windows 产出混合分隔符路径（~/a/b → C:\Users\x\a/b）
type: bug
priority: P2
size: S
status: done
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

`JsonlHomePath.ExpandHomePath` 的 `~/前缀` 展开在本机分隔符下产出规范路径，Windows 不再残留正斜杠混合路径。

## Background

- 2026-10-23 Windows 11 冒烟（P-002 之后的提交区间 b987e4c..HEAD）实锤：`PiAgentAdapterTests.Named_store_paths_expand_home_and_dedupe_existing_directories` 失败——期望 `...\.omp\agent\sessions`，实际 `...\.omp/agent/sessions`。父提交 b227c79 的 worktree 同样复现 → 既有问题，非当日引入。
- 根因：`JsonlHomePath.cs` 旧实现对 `raw[2..]` 直接 `Path.Combine(home, remainder)`——Windows 的 `Path.Combine` 不归一化分隔符，remainder 里的 `/` 原样保留。
- 影响面：`PiLogLocator.ExistingPathList`、`CodexLogLocator`、`ClaudeCodeLogLocator` 三处无 `Path.GetFullPath` 包装，混合路径原样返回/入库口径；其余 ~12 个 locator 外层有 GetFullPath 不受影响（Directory.Exists 在 Windows 接受混合分隔符，故静默不报错）。

## Acceptance Criteria

- [x] 展开结果三平台等于 `Path.Combine(home, ".omp", "agent", "sessions")`（Windows 旧实现即红的可移植断言，无需按平台跳过）
- [x] Windows 下断言展开结果不含 `/`（`OperatingSystem.IsWindows()` 守卫）
- [x] 非 `~` 条目（含 `~user/` 他人 home）逐字直通；无 HOME 时 `~/x` 原样保留——蓝本语义不变
- [x] 全量测试绿；不改测试期望（测试是对的，修产品代码）

## Development Log

### 2026-10-23（实施与收口，分支 fix/smoke-2026-10-23 ebeb1f7）

- 修复：`~/前缀` 分支 remainder 先 `Replace('/', Path.DirectorySeparatorChar)` 再 `Path.Combine`（Unix 上分隔符本就是 `/`，替换为空操作）；共享助手一处修复覆盖全部调用点。
- 回归锁：新增 `tests/Tokenmetry.Infrastructure.Tests/Jsonl/JsonlHomePathTests.cs` 四条（前缀展开分段构造相等 + Windows 无 `/`、`~` 直展、非 `~` 直通、无 HOME 保留原文）。
- 明确不做：不做全路径 GetFullPath（超蓝本语义）；非 `~` 条目不动；去重仍为文本相等。
- 验证：Infrastructure.Tests 661 绿（含 PiAgent 原失败测试）；macOS 全量 1400 绿。**Windows 分支需实机复核**（macOS 上旧实现本就通过，红→绿证据在 Windows 侧）。

## Verification

- Related files: `src/Tokenmetry.Infrastructure/Jsonl/JsonlHomePath.cs`、`tests/Tokenmetry.Infrastructure.Tests/Jsonl/JsonlHomePathTests.cs`
- How to run/verify: `dotnet test tests/Tokenmetry.Infrastructure.Tests --filter FullyQualifiedName~PiAgentAdapterTests` + `~JsonlHomePathTests`；Windows 实机复跑冒烟命令
- Results: macOS 全绿；Windows 复核待办（修复提交后需在 Windows 11 实机重跑两条冒烟测试确认绿）
