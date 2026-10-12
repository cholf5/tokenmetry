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

消除适配器的 UnpricedModels 欠账：把已移植为纯函数的"候选计价模型"机制接到定价调用点，使别名模型（如 OpenCode k2p6）、provider 限定键模型（如 `deepseek/deepseek-v4-flash`）能按蓝本口径命中价格，而不是记 0（fail-loud）。首批范围：四张 SQLite 卡（OpenCode / Goose / Hermes / Kilo）；I-018 / I-019 / I-022 / I-020 各源实现时各自登记了同类欠账，随本卡一并接线（见 Background 末段）。

## Background

四张 SQLite 卡（I-009/I-012/I-016/I-017）实现时把蓝本的候选计价机制逐条移植为**纯函数**（附移植测试），但自研定价引擎按 record.Model（裸模型名）定价，候选机制未接到调用点——登记为各卡偏差①/⑤并拆出本卡：

- **OpenCode**（`OpenCodeMessageParser`）：`ResolveModelName`（别名 k2p6→kimi-k2.6、gemini-3-pro-high→gemini-3-pro-preview）+ `NormalizeModelName`（claude-{haiku,opus,sonnet}- 点分/两位数字归一）+ `BuildModelCandidates`（[原名, 归一化名] + provider '-'→'_' 前缀变体、相邻去重；蓝本 open_code_model_candidates 首个正成本生效）；蓝本 `.../rust/adapters/opencode/src/parser.rs`。
- **Goose**（`GooseSessionMapper`）：`NormalizeProvider`（显式 '-'→'_'；缺省 claude-→anthropic / gpt-·chatgpt-·o*→openai / gemini-→google / qwen*→openrouter / 其余 goose）+ 蓝本 calculate_goose_cost 的 `{provider}/{model}` 候选（provider=goose 兜底不尝试）；蓝本 `.../rust/adapters/goose/src/parser.rs`。
- **Hermes**（`HermesSessionParser`）：`BuildModelCandidates`（provider 限定名仅 find_exact_with_fallback 命中时插入——exact 或 '.'/'@'→'-' 键归一化 `HasExactNormalizedPrice`；provider=hermes 兜底不限定）+ `NormalizeProvider` / `InferProviderFromModel`；蓝本 `.../rust/adapters/hermes/src/parser.rs` model_candidates。
- **Kilo**（`KiloMessageParser`）：`BuildModelCandidates`（provider 限定名仅 find_exact 命中时插入——无点分归一化；unknown/kilo 兜底不限定）；蓝本 `.../rust/adapters/kilo/src/parser.rs` model_candidates。

引擎侧现状：`PricingModelMatcher.GetCandidates` 是**反向**剥前缀方向（"provider/model"→剥出 model），与四源的"正向拼限定键"方向不同，不能直接复用替代表达。

真实影响示例：本机 OpenCode k2p6 行——record.Model="k2p6"，快照键是 moonshot/kimi-k2.6，按裸名不命中 → CostUsd 记 0（聚合侧）+ UnpricedModels 列出（fail-loud，非静默漂移）。

**三源欠账（I-018 / I-019 / I-022 实现登记，2026-10-12 审查补录，随本卡一并接线）**：

- **Codebuff**（I-018，蓝本 `.../rust/adapters/codebuff/src/parser.rs` calculate_codebuff_cost /
  missing_codebuff_pricing）：裸模型名未命中时按 `infer_provider`（claude-·anthropic/·anthropic. →
  anthropic；gpt-·o1·o3·o4·openai/ → openai；gemini·google/ → google；grok·xai/ → xai；openrouter/ →
  openrouter；其余 unknown）拼 "{provider}/{model}" 重试计价 + 按候选集报告缺失定价；另有
  **CB-2**：快照 openai/gpt-5 带 cache_write 1.5625、蓝本内嵌 models-dev 数据无 cache_write（计 0）
  ——gpt-5 + cache_creation 的真实数据成本 fail-loud（快照数据版本刷新观察项同源）。
- **pi-agent**（I-019，**PI-3**；蓝本 parser.rs identity cost 链 store_pricing_at / calculate_store_cost_from_tokens）：
  重放签名与复合 entry id 的"有效成本"现算走 CLI 全量定价表（litellm + models.dev + 别名 + deepseek
  时间表），桌面用内置快照——无存储成本的重复行在两侧的抑制判定可能不同；接线时 identity cost
  resolver 需与定价引擎同一套候选/别名口径。
- **Grok**（I-022，**GK-1/GK-2**；蓝本 `.../rust/adapters/grok/src/parser.rs` pricing_candidates +
  calculate_grok_cost_at）：候选链 = 剥 "[grok] " 前缀（**GK-2**，桌面引擎候选仅剥 "[pi] x" 形态）、
  剥 "-build" 后缀、补 xai/ 与 x-ai/ 前缀；exact 命中**全候选优先**、再 fuzzy（防首候选模糊命中遮蔽
  后续候选的精确条目）；missing_grok_pricing 按候选集报告。另有 **200K 长上下文分档**（xai/grok-4.5
  ·4.6 在 models-dev 带 &gt;200K context 双倍档、CLI find_at 按用量分档）——即上文 tiers 观察项的
  Grok 实例，无 ticks 的可计价行成本 fail-loud。
- **OpenClaw**（I-020，**OC-1**；蓝本 `.../rust/adapters/openclaw/src/parser.rs` openclaw_entry_to_loaded_inner
  pricing_model 判定）：候选链 = 装饰名 "[openclaw] m" 先 find_exact（命中按装饰名计价），未命中**回退裸
  模型名**（raw pricing identity；蓝本内联测试 calculates_decorated_deepseek_models_with_the_raw_pricing_identity）。
  与 Grok GK-2 的"剥前缀"方向不同——OpenClaw 是"装饰名→裸名"两级链，装饰名命中时保留装饰名分组。
  桌面统一记录仅存装饰名 → 无内建成本的可计价行当前记 0 + UnpricedModels（fail-loud）；真实数据
  可计价模型行常态带内建 cost.total（LogCost 直采）不受影响。
- **Antigravity**（I-021，**AG-1**；蓝本 `.../rust/adapters/antigravity/src/parser.rs`
  calculate_antigravity_cost / missing_antigravity_pricing / model_candidates）：三层候选链 =
  ①flash effort 变体（gemini-3.x-flash-{high,medium,low}）先 exact-with-fallback 命中（含模型别名
  归一后重试），未命中回退 ②基础名 gemini-3.x-flash，仍未命中 ③google provider（3=vertex_ai /
  24=gemini / 30=evergreen）时拼 google/ · gemini/ · vertex_ai/ · openrouter/google/ 前缀候选
  （非 flash 变体仅裸名 + 前缀组）；另有 **计价 output 分量取 total_output（含 reasoning）**——桌面
  统一记录无 reasoning/extra 字段、按 visible output 计价，含 reasoning 的可计价行成本 fail-loud
  （fixture 已规避，见 I-021 卡）。与 OpenClaw OC-1 的"装饰名→裸名"方向不同：AG-1 是
  "effort 变体→基础名→provider 前缀"三级链且 exact/fuzzy 门槛混合。

**观察项（I-010 审查登记，非阻塞）**：内置定价快照格式无 **tiers 分档**支持——models.dev 部分模型带
context 分档（例：google/gemini-2.5-pro 在 models-dev 带 &gt;200k context 档 input 2.5 / output 15 /
cache_read 0.25，基础档 1.25/10/0.125；anthropic/claude-sonnet-4 同样有 200k+ 档），自研快照解析与
`PricingModelMatcher` 按基础档计价。超阈值（如 &gt;200k context）的真实数据自研侧按基础档、CLI 按
分档计价，成本 fail-loud。本卡接线时一并考虑（或注明需独立扩展：快照条目增加 tiers 段 + 匹配器按
用量选档）；fixture 对账不受影响（用量远低于阈值）。

**观察项（I-013 审查登记，非阻塞）：快照数据版本刷新**——快照部分条目为旧版 models.dev 数据，与
ccusage 20.0.29 CLI 内嵌快照不同价。已实证一例：moonshot/kimi-k2.6 快照为 0.95/4/0.16（无
cache_write），CLI 20.0.29 内嵌为 input 1 / output 4.16 / cache_read 0.18 + cache_write 1（来源
../ccusage/rust/crates/ccusage-core/src/models-dev-pricing.json）。真实 kimi-k2.6 数据当前自研 cost
分量 fail-loud（token 不受影响；I-013 fixture 已规避该模型）。随本卡候选计价接线一并做快照全量刷新
（以录制日 CLI 内嵌 models-dev-pricing.json 为源），或拆独立小卡。

## Acceptance Criteria

- [ ] 方案选型（开工时定，两案对比后择一，记录选型理由）：
  - 方案 A：UsageRecord 增加可选"计价用模型名"侧字段（如 PricingModel），适配器在映射时填主候选，引擎优先按它定价、显示/分组仍用 record.Model；
  - 方案 B：引擎按源 resolver（注册 per-agent 候选函数，引擎逐候选尝试、首个命中生效），记录不变。
- [ ] 九源接线：OpenCode（别名 + 归一化 + provider 限定）、Goose（provider 限定，goose 兜底跳过）、Hermes（qualified 仅 exact-with-fallback、点分归一化）、Kilo（qualified 仅 find_exact）、Codebuff（infer_provider 前缀重试 + missing_pricing 候选集报告）、pi-agent（identity cost 与引擎同候选/别名口径）、Grok（GK-1/GK-2 候选链）、OpenClaw（装饰名→裸名两级链，装饰名命中保留装饰名分组）、Antigravity（AG-1 effort 变体→基础名→google provider 前缀三级链 + 计价 output 取 total_output 口径），逐条对齐蓝本语义与既有移植测试
- [ ] 验收口径：别名/限定键模型在内置快照离线口径下命中价格（UnpricedModels 消失，或仅剩真实未知模型）；候选机制未命中时行为与现状一致（记 0 + UnpricedModels fail-loud）
- [ ] fixture 对账回归：四源 fixture golden 逐位不回归（对账测试全绿）；蓝本内联测试锚点（各源 parser.rs 的候选/计价测试）继续逐条对齐

## Dependencies

- I-009（OpenCode 适配器，含移植的候选纯函数）
- I-012（Goose 适配器，同）
- I-016（Hermes 适配器，同）
- I-017（Kilo 适配器，同）
- I-018（Codebuff 适配器：provider 前缀候选重试 + missing_pricing 候选集报告 + CB-2）
- I-019（pi-agent 适配器：PI-3 identity cost 需 CLI 全量定价表口径）
- I-022（Grok 适配器：GK-1 候选链 + GK-2 "[grok] " 前缀剥离 + 200K 长上下文分档）
- I-020（OpenClaw 适配器：OC-1 装饰名→裸名两级候选链，装饰名命中保留装饰名分组）
- I-021（Antigravity 适配器：AG-1 effort 变体→基础名→google provider 前缀三级候选链 + 计价 output 取 total_output 口径）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填：方案选型记录 + 各源别名/限定键/未命中三场景单测 + fixture 对账回归运行）

Expected:
- （依实现回填）

## Development Log

### 2026-10-12

- 由 I-009/I-012/I-016/I-017 四卡独立审查（approve-with-nits）拆出：四源的候选计价机制已逐条移植为纯函数并有移植测试（候选清单、exact 门槛、键归一化逐位对齐蓝本内联测试），但未接到定价调用点——别名/限定键模型当前记 0（UnpricedModels fail-loud）。本卡消除该欠账；方案选型（记录侧字段 vs 引擎 resolver）开工时定。
- 2026-10-12（I-018/I-019/I-022 批次独立审查补录）：三张 JSONL/单文件卡实现时各自声称"I-024 已登记同类共性欠账"，本卡补录其范围——Codebuff（provider 前缀候选重试 + missing_pricing 候选集报告 + CB-2 快照 gpt-5 cache_write 差异）、pi-agent（PI-3 签名 identity cost 需 CLI 全量定价表口径）、Grok（GK-1 候选链 + GK-2 "[grok] " 前缀剥离 + 200K 长上下文分档，后者并入上文 tiers 观察项）；Dependencies 同步追加三卡。
- 2026-10-12（I-020 独立审查补录）：OpenClaw 实现时偏差①声称"I-024 已登记"不准确（GK-2 是同类但非同一笔），本卡补录 **OC-1**（装饰名→裸名两级候选链，区别于 GK-2 剥前缀方向）；Dependencies 追加 I-020，接线 AC 扩为八源。
- 2026-10-12（I-021 实现补录）：Antigravity 卡实现时按 I-020 教训**不声称已登记**，直接补录 **AG-1**（effort 变体→基础名→google provider 前缀三级候选链 + 计价 output 分量取 total_output 含 reasoning 的口径差异——后者为统一记录无 reasoning 字段的表示层欠账，随候选链一并接线）；Dependencies 追加 I-021，接线 AC 扩为九源。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
