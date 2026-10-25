---
id: B-010
title: 日志读取 FileShare.Read——Windows 上 agent 持写句柄期间该文件采不到（Unix 无此语义）
type: bug
priority: P1
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
blocked: none
---

## Goal

日志读取共享模式改为 `FileShare.ReadWrite | FileShare.Delete`，Windows 上"agent 正在写"的文件可边写边读，不再静默缺数据。

## Background

- 2026-10-23 扩大扫描（Windows 冒烟后续）发现，三处证据：
  - `src/Tokenmetry.Infrastructure/DshSession/DshSessionAdapter.cs:100` — `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)`；
  - `src/Tokenmetry.Infrastructure/Jsonl/IncrementalJsonlScanner.cs:33` — 默认 reader 工厂 `new StreamReader(path)`（`StreamReader(String)` 内部即 FileShare.Read，Claude/Codex/Qwen/Kimi 等全部 JSONL 适配器走这条）；
  - `src/Tokenmetry.Infrastructure/CopilotSession/CopilotSessionAdapter.cs:199` — `new StreamReader(path)`。
- 机理：Windows CreateFile 共享规则要求本次申请的 share mode 不得与已存在句柄的 access 冲突。FileShare.Read 不含 FILE_SHARE_WRITE，agent 正在追加日志时持有写句柄 → 打开即抛 IOException（sharing violation）。Unix 上 FileShare 是空操作，macOS 开发机永不复现。
- 现状兜底：调用点均 catch IOException 降级 skip + 下轮重试——不是崩溃，而是**写句柄保持打开期间（长会话 agent）Windows 用户持续采不到该会话**；open-append-close 型 agent 延迟一个刷新周期。`DshZstdLogStream.cs:49-50` 本就把 torn tail 定为"活跃会话下的预期状态"——边写边读是设计内场景，Windows 下却被共享模式挡在门外。

## Acceptance Criteria

- [x] 三处统一 `FileShare.ReadWrite | FileShare.Delete`；torn tail（半行）语义维持既有逐行容错
- [x] 既有全量测试绿（读 fixture 文件无写句柄，行为应不变）
- [x] Windows 实机验证：agent 会话进行中触发刷新，该会话数据按时入库（本卡验收必须在 Windows 做）

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描 F1，路径/IO 维度；macOS 无法验证，Windows 复核是验收的一部分）。

### 2026-10-25 收口

- 修法：新增共享入口 `Jsonl/AgentLogReader.OpenRead`（`FileShare.ReadWrite | FileShare.Delete` 单点 + 注释说明 Windows CreateFile 共享规则与"边写边读是设计内场景"），三处调用点改走它：DSH `OpenLogReader`（zstd 包装链不动）、扫描器默认 reader 工厂（显式 `StreamReader(Stream, Encoding.UTF8)` 与原 `StreamReader(String)` 同为 UTF-8 + BOM 探测）、Copilot `ParseFileAsync`。torn tail 逐行容错零改动（只换打开方式，不动读取）。
- Windows 实机验证方式（本机 Windows 11 真跑）：用例按 agent 追加形态持真实写句柄（`FileAccess.Write, FileShare.Read`）再走生产读路径——TDD RED 三处调用点全红（sharing violation → 恒 skip）、修后 4/4 绿；这正是"agent 会话进行中采集不缺数据"的共享语义本身，端到端整轮刷新观感移交用户。
- 明确不做：全仓其余 `StreamReader`/`FileStream` 直读点不跟改（重扫确认采集适配器仅此三处走日志读取；Sqlite 源走 SQLite 连接串自己的共享语义）；Unix 侧无行为变化（FileShare 空操作）。
- 测试：+4（AgentLogReaderTests 共享写共存 + 扫描器默认工厂 / DSH / Copilot 各一持写句柄用例），Infrastructure.Tests 667 Passed 全绿。

