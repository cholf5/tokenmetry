---
id: Q-001
title: 与 ccusage CLI 的对账测试
type: test
priority: P1
size: M
status: done
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

- [x] fixture 对账测试：解析 + 定价后与 CLI 输出的 daily 汇总比对，cost 相对误差 ≤ 0.5%，tokens 精确一致（实现补充：绝对误差 < $0.01 时豁免相对误差，避免小额日期噪声——审查确认的设计选择，见 docs/reconciliation §1.1）
- [x] 本机真实数据集成测试（环境门控，无 CLI / 无数据时跳过而非失败）
- [x] 差异报告：按日期 / 模型列出差异明细，可定位到解析或定价原因
- [x] 对账结果写入文档，作为 v0.3 exit criteria 的证据（docs/reconciliation/2026-10-11-reconciliation.md）

## Subtasks

- [x] 容差比较器（cost 相对误差、tokens 精确）
- [x] fixture 对账测试
- [x] 本机真实数据集成测试（env 门控）
- [x] 差异报告格式与输出
- [x] 对账结论文档

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
- 实现 `tests/TokenPulse.Reconciliation.Tests/`（新测试项目并入 sln）：容差比较器（tokens 精确、cost ≤0.5% 或 <$0.01、差异按 日期×模型×字段 定位并标注 解析/定价 原因）、CLI golden DTO、自研链路聚合器、比较器单测 13、fixture 对账测试 4、真实数据集成测试 2（TOKENPULSE_RECONCILE_REAL=1 门控，xunit.skippablefact 动态 Skip）。
- 用探针逐项确认 ccusage 20.0.29 行为规则（costUSD 优先、显式 costUSD:null 与 message.id 缺失整条丢弃、5m/1h 倍率、-fast、synthetic、去重、Codex 累计快照守卫、--timezone UTC 分桶），两侧口径全部对齐；录制 golden（claude/codex fixture 各一份，录制命令与版本见 fixtures/golden/README.md）。
- fixture 对账：per-model 与每日/全期总计全部零差异（tokens 精确一致，cost 一致）。
- 真实数据对账（本机 ~/.codex，19 文件 / 2931 记录）：tokens 级全等（input 12,348,058 / cacheRead 353,026,048 / output 1,273,689）；首跑 cost 两日超容差（+96.5% / +99.8%）——定位为 gpt-5.6-terra（本机默认模型）被内置快照的 "gpt-5.6"（sol，2× 价）家族兜底吞并，而 models.dev 实有 terra 价（2/12/0.2/2.5）与 CLI（LiteLLM）一致 → 归类为快照数据漏收（真实 bug）并修复：model-pricing-snapshot.json 补 gpt-5.6-terra 条目 + PricingEngineTests 回归测试（Snapshot_PricesGpt56TerraAtOwnRateNotSolFamily）。修复后真实数据全期 cost $101.3856438 与 CLI 逐分钱一致。
- 测试结果：dotnet build 0 警告 0 错误；dotnet test 全绿（门控关 227 通过 / 5 跳过，门控开 228 通过 / 4 跳过——Claude 真实数据因本机无 ~/.claude/projects 按设计 Skip）。
- 对账结论文档落盘：docs/reconciliation/2026-10-11-reconciliation.md（方法、golden 录制命令、fixture/真实数据逐项数字、差异归类、已知分歧清单）。

### 2026-10-11（独立审查修复）

- 独立审查结论 PASS with minor issues。审查员独立复核：golden 以 README 命令重录制**逐字节一致**；全量 10 天 CLI 实跑与文档 §3 及我们链路逐项一致（tokens 全等、cost $101.3856438 一致）；RECON-1（gpt-5.6-terra 漏收）确认是真 bug 且修复合法（临时删条目复现 2× 价后还原）。修复：
  - [P1] 门控开启但 npx 二进制缺失时测试 FAIL 而非 Skip（Win32Exception 未被捕获）：catch 过滤器补 Win32Exception，任何环境不具备均 Skip，符合 TC-002。
  - [P2] 定价差异 hint 前提过强（token 与 cost 差异可同时出现）：措辞改为"先解决解析差异再复核成本"。
  - [P2] AC1 补注绝对豁免口径；status 翻 done。
  - 观察项（记录不修）：快照无 tier 支持，gpt-5.6-terra 的 >272k context tier 价未录（本数据集无影响）；时区口径两侧均为 UTC 分桶（真实数据含 UTC 16-24 点窗口仍全对齐）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| RECON-1 | Medium | 内置价格快照漏收 gpt-5.6-terra，被 "gpt-5.6"（gpt-5.6-sol）家族模式以 2× 价兜底（真实数据对账发现，models.dev 2026-10-11 实有 terra 条目 2/12/0.2/2.5） | Fixed | 快照补条目 + `Snapshot_PricesGpt56TerraAtOwnRateNotSolFamily` / `BuiltinSnapshotIsEmbeddedAndCoversMainstreamModels` 回归；修复后真实数据对账 cost 与 ccusage 逐分钱一致 |

## Verification

- Related files: `tests/TokenPulse.Reconciliation.Tests/`（比较器 / golden DTO / 聚合器 / 测试 / fixtures+golden）、`src/TokenPulse.Core/Pricing/model-pricing-snapshot.json`（gpt-5.6-terra 条目）、`tests/TokenPulse.Core.Tests/Services/PricingEngineTests.cs`（回归）、`docs/reconciliation/2026-10-11-reconciliation.md`（证据）
- How to run/verify: `dotnet test TokenPulse.sln`（离线部分）；本机真实数据对账 `TOKENPULSE_RECONCILE_REAL=1 dotnet test tests/TokenPulse.Reconciliation.Tests`（需 npx 与真实数据，不满足时自动 Skip）
- Results: build 0 警告 0 错误；门控关 227 通过 / 5 跳过 / 0 失败，门控开 228 通过 / 4 跳过 / 0 失败；真实 Codex 数据 tokens 级全等、全期 cost $101.3856438 与 CLI 一致（详见对账文档 §3/§4）
