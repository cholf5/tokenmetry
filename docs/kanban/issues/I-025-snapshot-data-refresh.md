---
id: I-025
title: 定价快照数据版本刷新
type: chore
priority: P3
size: M
status: todo
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

把内置定价快照（`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`）与 ccusage 20.0.29 CLI 内嵌 models-dev-pricing.json 的存量价差清零：I-024 已刷新 kimi-k2.6 并补 tiers 段，本卡处理其余被非九源 fixture golden 引用、当时不便随卡改价的条目，并补归一化拼写歧义守卫。

## Background

I-024（2026-10-12 独立审查程序化对比，快照 82 条目 vs `../ccusage/rust/crates/ccusage-core/src/models-dev-pricing.json`）确认的实价差清单（快照 → CLI）：

- anthropic/claude-opus-4：15/75/1.5/18.75 → 13/64/1.3/16
- openai/gpt-4o：2.5/10 → 1.25/5
- openai/gpt-4o-mini：cache_write 0.1875 → 0.15
- openai/gpt-6-astra：cache_write 12.5 → 12
- qwen/qwen3-coder-plus：1/5 → 0.574/2.294
- openai/gpt-5.4：cache_write 3.125 → 2.5（快照旧推导 input×1.25，CLI 显式 input×1.0）
- openai/gpt-5.5：cache_write 6.25 → 5（同上）

这些条目被非 I-024 九源（Qwen / Copilot / Codex 等）的 fixture golden 引用，改价需随那些源重录 golden（重录命令见 `tests/CCUsage.Reconciliation.Tests/fixtures/golden/README.md`）。另登记一处引擎差异：蓝本 `normalized_keys`（ccusage-core pricing.rs）对同一归一化拼写挂两个键时置 None（"names neither entry"），桌面归一化趟无此守卫按 rank 取一——当前快照仅 claude-3-5-sonnet / claude-3-5-haiku 两对碰撞且四条价格逐位相同、原拼写趟必先命中，今日无影响；本卡刷新若拆开同价对，点分候选的回退落点可能二义，需一并补守卫。

## Acceptance Criteria

- [ ] 上述 7 条价差按 CLI 内嵌值刷新；随改价源重录受影响 fixture golden，对账逐位一致
- [ ] 归一化拼写歧义守卫补齐（同归一化拼写多键 → 归一化趟不命中），蓝本 pricing.rs normalized_keys 语义对齐 + 单测（构造同价对拆开场景）
- [ ] 刷新来源与录制日登记进快照文件头或卡片（可追溯）
- [ ] 全量测试绿；门控真实对账无回归

## Dependencies

- I-024（候选计价接线，快照格式与归一化趟所在处）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-12

- 由 I-024 独立审查（approve-with-nits）拆出：偏差⑧全量刷新超 I-024 范围（受影响 golden 属其他源），gpt-5.4/5.5 两条 cache_write 价差为审查补录；归一化歧义守卫差异同卡登记。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
