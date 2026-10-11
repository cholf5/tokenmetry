---
id: I-001
title: Claude Code JSONL 直读适配器
type: feature
priority: P1
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

在 `CCUsage.Infrastructure` 新增 Claude Code JSONL 直读适配器，把本地会话日志解析为统一 usage 记录，摆脱对 `ccusage` CLI 的运行时依赖。

## Background

- 自主采集是 v0.3 的第一步：CLI 输出格式受制于人，且默认联网刷新极慢（依据见 `docs/chore/why-ccusage-so-slow.md`）。
- 解析规则参照 `../ccusage-tauri/src-tauri/src/claude.rs` 逐条移植（多根目录发现、cache_creation 5m/1h token 细分、损坏行诊断计数）。
- 产出的统一记录模型是 I-002 facts 表和 I-003 定价引擎的输入。

## Acceptance Criteria

- [ ] 能从默认路径及 `CLAUDE_CONFIG_DIR` 自定义路径发现并解析 JSONL，产出统一 usage 记录（model、agent、时间戳、input / output / cache_creation / cache_read tokens）
- [ ] 增量扫描：只解析新增或变更的文件，重复运行不产生重复记录
- [ ] 损坏行 / 未知行跳过并计入诊断计数，不中断整体解析
- [ ] 单测覆盖：样例 JSONL fixture、多根目录、空目录、损坏行

## Subtasks

- [ ] 日志目录发现（默认路径 + `CLAUDE_CONFIG_DIR` + 多 profile）
- [ ] JSONL 行解析与 usage 事件抽取（定义统一记录模型）
- [ ] cache_creation 5m / 1h token 细分与计价标记
- [ ] 增量扫描（文件 mtime / offset 记账）与消息级去重
- [ ] 诊断计数（malformed / duplicate / unknown-model）
- [ ] fixtures + 单元测试

## Dependencies

- none

## Test Cases

### TC-001: 样例 fixture 解析

Steps:
1. 使用仓库内样例 JSONL fixture 运行适配器。

Expected:
- 记录数量与内容与 fixture 预期一致，tokens 字段正确。

### TC-002: 重复运行幂等

Steps:
1. 对同一目录连续运行两次适配器。

Expected:
- 第二次运行不产生重复记录（增量或去重生效）。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/`（新增适配器目录）、`tests/CCUsage.Infrastructure.Tests/`
- How to run/verify: `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj`
- Results: 待执行
