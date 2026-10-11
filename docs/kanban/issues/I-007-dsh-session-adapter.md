---
id: I-007
title: DSH 会话适配器
type: feature
priority: P1
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 DSH（DeepSeek Harness）本地会话日志（~/.dsh/sessions/<encoded-cwd>/session-*/session.v*.jsonl.zstd），产出 agent=DSH 的统一 usage 记录。

## Background

DSH 不在 ccusage 支持列表内（2026-10-11 全量核实），需自行实现适配器，无上游蓝本。本机数据勘察（66MB）：zstd 压缩 JSONL，usage 记录形如 {type:"assistant/message", seq, time(epoch ms), data:{turn, step, usage:{inputTokens, outputTokens, totalTokens}, message:{model,...}}}；目录名即项目路径（--Users-theo-dev-<proj>-- 式编码）。用户另有 dsh-usage-stats 项目自带定价目录（~/.dsh/storages/usage-stats-cache.json 含 pricingCatalog），可作为模型→价格映射的参考与对账基准。

## Acceptance Criteria

- [ ] 确认 token 语义：inputTokens 是否含缓存切片、cache 字段是否存在（抽样多会话核对 dsh-usage-stats 口径）；deepseek 模型定价映射来源确定（快照扩充 or 用户覆盖）
- [ ] C# zstd 解压方案落定（托管库或 P/Invoke），流水式解压避免整文件入内存
- [ ] 目录发现（DSH_HOME 环境变量 → ~/.dsh）与增量扫描（mtime/大小记账复用引擎，zstd 解压封装为文本流）
- [ ] usage 记录抽取（data.usage 非空的 assistant/message 行），agent=DSH，项目路径从目录名还原
- [ ] 与 dsh-usage-stats 项目数字对账（tokens 精确一致）或与其缓存/账本口径互证
- [ ] fixtures + 单元测试 + 接入本地采集管道

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/(无上游蓝本，自研)/），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
