---
id: I-004
title: Codex JSONL 适配器
type: feature
priority: P2
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

实现 Codex 会话日志适配器，与 Claude 适配器共享统一记录模型和目录扫描框架，产出 `agent=codex` 的 usage 记录。

## Background

- 多工具支持是产品的既有范围（原 PRD：Claude Code + Codex 等所有 ccusage 支持的工具）。
- Codex 日志格式与 Claude 不同（会话文件结构与 usage 事件位置不同），蓝本：`../ccusage-tauri/src-tauri/src/codex.rs`。

## Acceptance Criteria

- [ ] Codex 日志目录发现与解析，产出统一 usage 记录（agent 标记为 codex）
- [ ] 与 Claude 适配器共享扫描 / 去重 / 诊断计数框架，无复制粘贴的分叉逻辑
- [ ] 损坏行跳过并计数，不中断解析
- [ ] 对账测试覆盖 codex 样例 fixture（tokens 精确、cost 容差内）

## Subtasks

- [ ] Codex 日志目录发现
- [ ] JSONL 解析与 usage 事件抽取（复用统一记录模型）
- [ ] 接入增量扫描与诊断计数
- [ ] fixtures + 单元测试

## Dependencies

- I-001（共享框架）、I-003（定价）

## Test Cases

### TC-001: codex fixture 解析

Steps:
1. 使用 codex 样例 fixture 运行适配器。

Expected:
- 记录内容正确，agent=codex，tokens 与 fixture 预期一致。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Infrastructure/`（适配器目录）、`tests/CCUsage.Infrastructure.Tests/`
- How to run/verify: `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj`
- Results: 待执行
