---
id: Q-001
title: 与 ccusage CLI 的对账测试
type: test
priority: P1
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

建立对账测试：同一份数据上，自主采集 + 自有定价的结果与 `ccusage --json --offline` 的输出在容差内一致。这是产品"可对账"信任锚的机制化，也是 v0.3 的验收门槛。

## Background

- 产品立场之一（见 pivot 文档第 3 节）：任何成本数字必须能与 ccusage 核对。
- 对账差异的典型来源：解析遗漏、定价未命中、cache write 倍率规则不一致。

## Acceptance Criteria

- [ ] fixture 对账测试：解析 + 定价后与 CLI 输出的 daily 汇总比对，cost 相对误差 ≤ 0.5%，tokens 精确一致
- [ ] 本机真实数据集成测试（环境门控，无 CLI / 无数据时跳过而非失败）
- [ ] 差异报告：按日期 / 模型列出差异明细，可定位到解析或定价原因
- [ ] 对账结果写入文档，作为 v0.3 exit criteria 的证据

## Subtasks

- [ ] 容差比较器（cost 相对误差、tokens 精确）
- [ ] fixture 对账测试
- [ ] 本机真实数据集成测试（env 门控）
- [ ] 差异报告格式与输出
- [ ] 对账结论文档

## Dependencies

- I-001, I-002, I-003

## Test Cases

### TC-001: fixture 对账

Steps:
1. 对仓库内 fixture 运行 CLI（offline）与自主采集两条链路。
2. 运行对账比较器。

Expected:
- tokens 完全一致；cost 相对误差 ≤ 0.5%。

### TC-002: 无 CLI 环境跳过

Steps:
1. 在 PATH 中移除 ccusage 后运行集成测试。

Expected:
- 测试标记跳过（skipped），不报失败。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/`（新增对账测试项目或目录）
- How to run/verify: `dotnet test`（集成测试需本机装有 ccusage 与真实数据）
- Results: 待执行
