---
id: I-024
title: 候选计价端到端接线
type: feature
priority: P2
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

消除四源适配器（OpenCode / Goose / Hermes / Kilo）的 UnpricedModels 欠账：把已移植为纯函数的"候选计价模型"机制接到定价调用点，使别名模型（如 OpenCode k2p6）、provider 限定键模型（如 `deepseek/deepseek-v4-flash`）能按蓝本口径命中价格，而不是记 0（fail-loud）。

## Background

四张 SQLite 卡（I-009/I-012/I-016/I-017）实现时把蓝本的候选计价机制逐条移植为**纯函数**（附移植测试），但自研定价引擎按 record.Model（裸模型名）定价，候选机制未接到调用点——登记为各卡偏差①/⑤并拆出本卡：

- **OpenCode**（`OpenCodeMessageParser`）：`ResolveModelName`（别名 k2p6→kimi-k2.6、gemini-3-pro-high→gemini-3-pro-preview）+ `NormalizeModelName`（claude-{haiku,opus,sonnet}- 点分/两位数字归一）+ `BuildModelCandidates`（[原名, 归一化名] + provider '-'→'_' 前缀变体、相邻去重；蓝本 open_code_model_candidates 首个正成本生效）；蓝本 `.../rust/adapters/opencode/src/parser.rs`。
- **Goose**（`GooseSessionMapper`）：`NormalizeProvider`（显式 '-'→'_'；缺省 claude-→anthropic / gpt-·chatgpt-·o*→openai / gemini-→google / qwen*→openrouter / 其余 goose）+ 蓝本 calculate_goose_cost 的 `{provider}/{model}` 候选（provider=goose 兜底不尝试）；蓝本 `.../rust/adapters/goose/src/parser.rs`。
- **Hermes**（`HermesSessionParser`）：`BuildModelCandidates`（provider 限定名仅 find_exact_with_fallback 命中时插入——exact 或 '.'/'@'→'-' 键归一化 `HasExactNormalizedPrice`；provider=hermes 兜底不限定）+ `NormalizeProvider` / `InferProviderFromModel`；蓝本 `.../rust/adapters/hermes/src/parser.rs` model_candidates。
- **Kilo**（`KiloMessageParser`）：`BuildModelCandidates`（provider 限定名仅 find_exact 命中时插入——无点分归一化；unknown/kilo 兜底不限定）；蓝本 `.../rust/adapters/kilo/src/parser.rs` model_candidates。

引擎侧现状：`PricingModelMatcher.GetCandidates` 是**反向**剥前缀方向（"provider/model"→剥出 model），与四源的"正向拼限定键"方向不同，不能直接复用替代表达。

真实影响示例：本机 OpenCode k2p6 行——record.Model="k2p6"，快照键是 moonshot/kimi-k2.6，按裸名不命中 → CostUsd 记 0（聚合侧）+ UnpricedModels 列出（fail-loud，非静默漂移）。

## Acceptance Criteria

- [ ] 方案选型（开工时定，两案对比后择一，记录选型理由）：
  - 方案 A：UsageRecord 增加可选"计价用模型名"侧字段（如 PricingModel），适配器在映射时填主候选，引擎优先按它定价、显示/分组仍用 record.Model；
  - 方案 B：引擎按源 resolver（注册 per-agent 候选函数，引擎逐候选尝试、首个命中生效），记录不变。
- [ ] 四源接线：OpenCode（别名 + 归一化 + provider 限定）、Goose（provider 限定，goose 兜底跳过）、Hermes（qualified 仅 exact-with-fallback、点分归一化）、Kilo（qualified 仅 find_exact），逐条对齐蓝本语义与既有移植测试
- [ ] 验收口径：别名/限定键模型在内置快照离线口径下命中价格（UnpricedModels 消失，或仅剩真实未知模型）；候选机制未命中时行为与现状一致（记 0 + UnpricedModels fail-loud）
- [ ] fixture 对账回归：四源 fixture golden 逐位不回归（对账测试全绿）；蓝本内联测试锚点（各源 parser.rs 的候选/计价测试）继续逐条对齐

## Dependencies

- I-009（OpenCode 适配器，含移植的候选纯函数）
- I-012（Goose 适配器，同）
- I-016（Hermes 适配器，同）
- I-017（Kilo 适配器，同）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填：方案选型记录 + 各源别名/限定键/未命中三场景单测 + fixture 对账回归运行）

Expected:
- （依实现回填）

## Development Log

### 2026-10-12

- 由 I-009/I-012/I-016/I-017 四卡独立审查（approve-with-nits）拆出：四源的候选计价机制已逐条移植为纯函数并有移植测试（候选清单、exact 门槛、键归一化逐位对齐蓝本内联测试），但未接到定价调用点——别名/限定键模型当前记 0（UnpricedModels fail-loud）。本卡消除该欠账；方案选型（记录侧字段 vs 引擎 resolver）开工时定。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
