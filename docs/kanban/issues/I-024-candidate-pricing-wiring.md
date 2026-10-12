---
id: I-024
title: 候选计价端到端接线
type: feature
priority: P2
size: M
status: done
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

- [x] 方案选型（开工时定，两案对比后择一，记录选型理由）：
  - 方案 A：UsageRecord 增加可选"计价用模型名"侧字段（如 PricingModel），适配器在映射时填主候选，引擎优先按它定价、显示/分组仍用 record.Model；
  - 方案 B：引擎按源 resolver（注册 per-agent 候选函数，引擎逐候选尝试、首个命中生效），记录不变。
  - **选型：方案 A（落地为记录侧"计价计划"字段 `UsageRecord.Pricing: UsagePricingPlan`，含有序候选链 + 匹配走向 + 计价 output 覆盖，理由见 Dev Log「方案选型」节）**
- [x] 九源接线：OpenCode（别名 + 归一化 + provider 限定）、Goose（provider 限定，goose 兜底跳过）、Hermes（qualified 仅 exact-with-fallback、点分归一化）、Kilo（qualified 仅 find_exact）、Codebuff（infer_provider 前缀重试 + missing_pricing 候选集报告）、pi-agent（identity cost 与引擎同候选/别名口径）、Grok（GK-1/GK-2 候选链）、OpenClaw（装饰名→裸名两级链，装饰名命中保留装饰名分组）、Antigravity（AG-1 effort 变体→基础名→google provider 前缀三级链 + 计价 output 取 total_output 口径），逐条对齐蓝本语义与既有移植测试
- [x] 验收口径：别名/限定键模型在内置快照离线口径下命中价格（UnpricedModels 消失，或仅剩真实未知模型）；候选机制未命中时行为与现状一致（记 0 + UnpricedModels fail-loud）
- [x] fixture 对账回归：九源 fixture golden 逐位不回归（对账测试全绿）；蓝本内联测试锚点（各源 parser.rs 的候选/计价测试）继续逐条对齐

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

### TC-001: 候选计价端到端接线（I-024）

Steps:
1. 方案选型：`UsageRecord.Pricing: UsagePricingPlan(ModelCandidates, Walk, OutputTokenOverride)`（方案 A 落地），引擎 `PriceRecord` 按计划走向逐候选定价，记录不变量（Model / facts）由结构保证。
2. 引擎新能力单测（Core.Tests `PricingEngineTests` + 新增 `PricingModelMatcherTests`）：候选链首个命中生效 / 全候选未命中与单候选 Missing 同口径（UnpricedModels 记显示模型名）/ ExactThenFamily 走向 exact 全候选优先（对照 PerCandidateFull 家族先行）/ ExactOnly 走向不落家族兜底 / OutputTokenOverride 计价 output 覆盖 / 长上下文分档（超档全桶切换 + 恰等阈值不切档 + 档内缺项回落基础档）/ 键归一化（'.'/'@'→'-' 两侧归一、dash↔dot 双向、原拼写命中优先）。
3. 九源接线测试（Infrastructure.Tests 各源适配器测试文件）：
   - OpenCode：k2p6 别名链落记录 Pricing（蓝本 base=[resolved] 形状）→ 引擎 PriceRecord 命中 moonshot/kimi-k2.6（0.0001506，刷新价）；
   - Goose：显式 provider 限定链 + goose 兜底不限定（Missing fail-loud 不回归）；
   - Hermes：点分名 kimi.k2.6 经键归一化命中（hermes 兜底不限定）；google 限定名 + 裸名候选命中 google/gemini-2.5-pro；
   - Kilo：providerID 参与候选链（missing/kilo 兜底不限定）；moonshot 限定命中；
   - Codebuff：infer_provider 全分支移植锚点 + grok-4.5 候选命中 xai/grok-4.5 + "-build" 不剥离（codebuff 蓝本无此链，未命中不回归）；
   - pi-agent：identity cost resolver 接共享引擎（用户覆盖进入重放签名 BilledCost；缺省解析器复用单引擎）；
   - Grok：pricing_candidates 移植锚点（[grok] 前缀 / -build 后缀 / xai//x-ai/ 前缀 / 去重）+ grok-4.5-build 无 ticks 行剥后缀经 xai/grok-4.5 命中（ExactThenFamily 走向）+ 真实未知模型未命中不回归；
   - OpenClaw：装饰名→裸名两级链（ExactThenFamily 走向）无内建成本行按裸名精确命中 + 真实未知装饰名不回归；
   - Antigravity：model_candidates / gemini_flash_pricing_alias 移植锚点 + flash effort 变体 exact-only 走向命中基础名 google/gemini-3.8-flash + 计价 output 取 total_output（OutputTokenOverride=22，含 reasoning）。
4. fixture 对账回归：九源（含其余十源）fixture golden 全部全绿、数字逐位不回归；门控真实对账（CCUSAGE_RECONCILE_REAL=1）本机有数据的源全绿。

Expected:
- 别名/限定键/点分/装饰名模型在内置快照离线口径下命中价格（MatchedKey 指向真实条目）；候选未命中行为与接线前一致（Missing + UnpricedModels fail-loud，键为显示模型名）。
- `dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` 全绿：**757 通过 / 22 跳过**（基线 728/22 + 本卡新增 29）。

### 2026-10-12（独立审查修复，收口）

独立审查结论 approve-with-nits（既有源零变化证明成立、九源候选链逐源对照通过、CB-2 等价性实证、tiers 边界与 13 条 tiers 数据逐位核对一致）。收口前修复：

- 偏差⑧清单补录 openai/gpt-5.4（cache_write 3.125 → 2.5）、openai/gpt-5.5（6.25 → 5）两条实价差（审查程序化对比发现，基础档 cache_write 为快照旧推导值）；归一化拼写歧义守卫差异一并登记。
- 拆出快照全量刷新小卡 I-025（7 条价差 + 归一化歧义守卫，随受影响源重录 golden）。
- CodebuffChatParser.cs 编辑事故格式还原（右花括号并行，零行为影响）。
- 备案不修：UsagePricingPlan.Single 工厂暂无调用方（API 完整性保留）；Grok 候选保留原始大小写经引擎统一小写（比 CLI 大小写敏感略宽，实际模型名全小写）。

## Development Log

### 2026-10-12

- 由 I-009/I-012/I-016/I-017 四卡独立审查（approve-with-nits）拆出：四源的候选计价机制已逐条移植为纯函数并有移植测试（候选清单、exact 门槛、键归一化逐位对齐蓝本内联测试），但未接到定价调用点——别名/限定键模型当前记 0（UnpricedModels fail-loud）。本卡消除该欠账；方案选型（记录侧字段 vs 引擎 resolver）开工时定。
- 2026-10-12（I-018/I-019/I-022 批次独立审查补录）：三张 JSONL/单文件卡实现时各自声称"I-024 已登记同类共性欠账"，本卡补录其范围——Codebuff（provider 前缀候选重试 + missing_pricing 候选集报告 + CB-2 快照 gpt-5 cache_write 差异）、pi-agent（PI-3 签名 identity cost 需 CLI 全量定价表口径）、Grok（GK-1 候选链 + GK-2 "[grok] " 前缀剥离 + 200K 长上下文分档，后者并入上文 tiers 观察项）；Dependencies 同步追加三卡。
- 2026-10-12（I-020 独立审查补录）：OpenClaw 实现时偏差①声称"I-024 已登记"不准确（GK-2 是同类但非同一笔），本卡补录 **OC-1**（装饰名→裸名两级候选链，区别于 GK-2 剥前缀方向）；Dependencies 追加 I-020，接线 AC 扩为八源。
- 2026-10-12（I-021 实现补录）：Antigravity 卡实现时按 I-020 教训**不声称已登记**，直接补录 **AG-1**（effort 变体→基础名→google provider 前缀三级候选链 + 计价 output 分量取 total_output 含 reasoning 的口径差异——后者为统一记录无 reasoning 字段的表示层欠账，随候选链一并接线）；Dependencies 追加 I-021，接线 AC 扩为九源。

### 2026-10-12（实现）：方案选型

**结论：选方案 A，落地为 `UsageRecord.Pricing: UsagePricingPlan`**（`ModelCandidates` 有序候选链 + `Walk` 匹配走向 + `OutputTokenOverride` 计价 output 覆盖），即卡片所述"计价用模型名侧字段"的忠实扩展。理由：

1. **方案 B 的"记录不变"前提不成立**。候选链的输入依赖各源映射期才有的上下文：OpenCode 的 providerID、Goose 的 provider_name、Hermes 的 provider 列、Kilo 的 providerID、Antigravity 的 provider 枚举（3/24/30）——统一记录本就不携带 provider。方案 B 要么给记录加 provider 侧字段（记录照样变），要么引擎按 Agent 名重放各源推导（Core 反向依赖 Infrastructure 的九源纯函数，或引入运行时 resolver 注册表：注册时序、线程安全、引擎与适配器生命周期耦合），分层与复杂度都更差。
2. **蓝本语义是"有序候选链 + 首个命中生效"，不是"单一主候选"**。纯方案 A（单 PricingModel 字段）保不了链序：OpenCode [resolved, normalized, provider/×2] 四候选、Grok 六候选且 exact 全候选优先、Antigravity 变体→基础名→前缀组展开、OpenClaw 装饰名仅精确参与。候选列表侧字段即蓝本链的忠实载体；匹配走向（逐候选完整 / exact 全候选优先 / 仅精确）作为计划字段一并承载（GK-1 的"exact 全候选优先"无法用纯列表表达）。
3. **分组/显示/落库零影响由结构保证**。record.Model 不变（分组、显示、facts 映射照旧读它）；facts mapper 不读 Pricing；候选链与 output 覆盖只进定价引擎——AC"候选名只影响计价"无需约定即可成立。
4. **Core 保持源无关**。引擎只认"有序候选 + 走向 + output 覆盖"三个中性概念；九源语义全部留在各自适配器（既有移植纯函数就地接线，OpenCode/Goose/Hermes/Kilo 的纯函数原样复用，Codebuff/Grok/OpenClaw/Antigravity 本卡补移植并配锚点测试）。

### 2026-10-12（实现）：实现完成

**引擎侧（CCUsage.Core）**：
- `UsagePricingPlan`（新）：候选链 + `PricingCandidateWalk`（PerCandidateFull / ExactThenFamilyAcrossCandidates / ExactOnlyAcrossCandidates）+ OutputTokenOverride；`UsageRecord.Pricing` 侧字段（null = 按 Model 单候选，接线前现状）。
- `PricingEngine`：`PriceRecord` 优先按记录计划计价；`PriceTokens(plan, displayModel, tokens)` 三种走向（PerCandidateFull=蓝本逐候选 find；ExactThenFamily=蓝本 grok exact 全候选优先再 fuzzy、同构承载 OpenClaw OC-1；ExactOnly=蓝本 antigravity flash exact-with-fallback）；全候选未命中与单候选同口径（Missing + UnpricedModels 记显示模型名）。
- `PricingModelMatcher`：精确档补**键归一化变体**（蓝本 find_exact_with_fallback / normalized_pricing_key：候选与条目键两侧 '.'/'@'→'-'，dash↔dot 双向互hit；每档先全部候选原拼写、未命中再归一化拼写——原拼写命中优先，扩展前行为逐位不变）。
- **tiers 分档**（观察项①纳入）：`ModelPriceEntry` 增 LongContextThreshold / 四个 AboveThreshold 单价；`ModelsDevPricingParser` 解析 cost.tiers（type=="context" 且 size&gt;0、取最小阈值、档内缺项回落基础档——蓝本 ModelsDevCost::long_context_tier 同式）；`PricingEngine.ComputeCost` 两段式（请求上下文 input+cache 各桶 &gt; 阈值 → 全桶整体切换，蓝本 cost.rs 同式；恰等阈值不切档）。
- 快照（model-pricing-snapshot.json，82 条目）：**kimi-k2.6 刷新**（0.95/4/0.16 → 1/4.16/0.18/1，源 = ccusage 20.0.29 CLI 内嵌 models-dev-pricing.json，2026-10-12 录制）；11 条既有条目补 tiers 段（claude-sonnet-4、claude-haiku-5-5、gemini-2.5-pro、gpt-5.4/5.5/5.6-sol/5.6-terra、gpt-6-luna/sol/astra、xai/grok-4.3，同源）；新条目 xai/grok-4.5、xai/grok-4.6（GK-1 分档模型，含 200K 档）与 google/gemini-3.8-flash、gemini-3.7-flash、gemini-3.6-flash（AG-1 flash 别名基础名，同源）。
- CB-2 处置（分析后无需改动）：CLI models-dev 层对缺 cache_write 的条目按 input×1.25 推导（ccusage-core pricing.rs `unwrap_or(input * 1.25)`），与快照 openai/gpt-5 显式 cache_write 1.5625 等价——"计 0"不是 models-dev 层行为；LiteLLM 表层的差异核对不在本卡范围，真实数据若现差异由对账 fail-loud 捕获。

**九源接线（CCUsage.Infrastructure）**（映射期填 `Pricing`，分组/显示/facts 全部不变）：
- **OpenCode**：`Pricing = BuildModelCandidates(modelID, providerID)`（既有纯函数，蓝本 base 首项 = resolved 别名解析名）。
- **Goose**：映射期算 `NormalizeProvider`（原仅登记偏差）→ `BuildModelCandidates`（新纯函数：[裸名] + provider≠goose 时 [provider/裸名]，蓝本 calculate_goose_cost / missing_goose_pricing 候选集同式）。
- **Hermes**：`BuildModelCandidates(provider, model, _ => true)`（既有纯函数；qualified 仅精确生效的门槛由引擎精确匹配承载，`HasExactNormalizedPrice` 的点分归一化已进匹配器）。
- **Kilo**：载荷 providerID 读取（此前未读）→ `BuildModelCandidates`（既有纯函数，unknown/kilo 兜底不限定）。
- **Codebuff**：`InferProvider`（新移植，全分支锚点测试）+ `BuildModelCandidates`（[裸名] + provider≠unknown 且模型未自带 "{provider}/" 时 [provider/裸名]，蓝本 calculate/missing_codebuff_pricing 候选集同式）；"-build" 不剥离（那是 Grok 链，测试锚定未命中不回归）。
- **pi-agent**（PI-3）：`PiAgentAdapter` 增可选 `PricingEngine`，DI 传入共享引擎（AppServices）——identity cost resolver 与主管道同一引擎（用户覆盖 / 远程数据同源）、同一套候选/别名口径（display 名 "[store] 裸名" 经引擎候选剥离等价蓝本 store_pricing_at 的 display 精确 → 裸名链）；缺省解析器在实例内复用单引擎（原每条记录新建引擎）。
- **Grok**：`BuildPricingCandidates`（新移植 pricing_candidates：[grok] 前缀剥离、-build 后缀剥离、xai//x-ai/ 前缀、去重保序）+ `ExactThenFamilyAcrossCandidates` 走向（GK-1 exact 全候选优先）。
- **OpenClaw**：候选 = [装饰名, 裸名] + `ExactThenFamilyAcrossCandidates`（OC-1：装饰名仅精确参与——exact 档全候选优先保证裸名精确不高于装饰名精确；家族兜底等价蓝本裸名 find 档）；装饰名命中保留装饰名分组（Model 不变）。
- **Antigravity**：`GeminiFlashPricingAlias` + `BuildModelCandidates`（新移植：变体→基础名→google 前缀组展开）；flash 变体走向 ExactOnly（蓝本 exact_lookup / find_exact_pricing_with_alias）；**OutputTokenOverride = TotalOutputTokens**（AG-1 计价 output 取 total_output 含 reasoning；token 展示/落库仍 visible 口径）。

### 2026-10-12（实现）：与蓝本偏差清单

1. **"首个正成本"→"首个可解析出价格"**：蓝本逐候选 cost&gt;0 才接受（0 价条目继续下一候选），桌面首个能解析出价格条目的候选即生效（0 价条目会遮蔽后续候选）。仅当价格表存在 0 价条目且遮蔽他候选拼法时可观察；现有快照/远程数据无此形态。
2. **exact 档排除带 MatchPattern 条目**：桌面匹配器把带 match 模式的条目归入家族档（既有设计，保护 mini/nano 子型号不被旗舰价吞）；CLI find_exact 无此概念。对 Grok/OpenClaw 的 exact-first 走向：带模式的条目落家族档、价格与 CLI fuzzy 档一致；无模式条目（moonshot/kimi-k2.6、xai/grok-4.5 等）在 exact 档命中。
3. **Kilo 门槛（find_exact 无归一化）被引擎超集**：点分 qualified 键在桌面可归一化命中；CLI Kilo gate 会跳过该 qualified 候选，但其裸名 find 的归一化路径命中同一条目——计费落点一致。
4. **Antigravity flash 变体走向参与用户覆盖**：CLI find_exact_with_fallback 不查用户覆盖（覆盖在 find_at 后处理，flash 链不经 find_at），桌面 exact 档先查覆盖——用户覆盖 flash 变体名时桌面 honoring 用户意图。
5. **UnpricedModels 键**：CLI 报 resolve_model_name(model)（env 别名解析后），桌面报显示模型名（record.Model）——与接线前各源口径一致。
6. **LiteLLM 边际 200K 分档（*_above_200k_tokens 的 tiered_cost）未实现**：桌面快照/远程均为 models.dev 数据（per-model 阈值整体切档），无 LiteLLM 数据源；models.dev tiers 语义已全量实现。
7. **pi-agent identity cost 的 deepseek 时间表**：CLI find_at 对 deepseek-v4-* 按时间表调价；桌面引擎主链不含 deepseek 时间表（I-007 口径：时间表为 DSH 官方路由行的记录侧机制），identity cost 走共享引擎快照/覆盖/远程口径。
8. **快照全量刷新拆卡**：与 20.0.29 内嵌数据逐条对比，除 kimi-k2.6 外还发现 anthropic/claude-opus-4（15/75/1.5/18.75 → 13/64/1.3/16）、openai/gpt-4o（2.5/10 → 1.25/5）、openai/gpt-4o-mini（cache_write 0.1875 → 0.15）、openai/gpt-6-astra（cache_write 12.5 → 12）、qwen/qwen3-coder-plus（1/5 → 0.574/2.294）、openai/gpt-5.4（cache_write 3.125 → 2.5）、openai/gpt-5.5（cache_write 6.25 → 5）等价差（gpt-5.4/5.5 为 2026-10-12 审查程序化对比补录：本卡补 tiers 时基础档 cache_write 仍是快照旧推导值 input×1.25，CLI 为显式值 input×1.0）——这些条目被非本卡九源（Qwen / Copilot / Codex 等）的 fixture golden 引用，改价需随那些源重录 golden，超出本卡合理范围；差异清单已列，拆"快照数据版本刷新"独立小卡（I-025）逐源处理，并一并登记归一化拼写歧义守卫差异（蓝本 normalized_keys 对同一归一化拼写挂两键时置 None 不命中，桌面归一化趟无此守卫按 rank 取一；当前快照仅 claude-3-5-sonnet/haiku 两对碰撞且四条同价、原拼写趟必先命中，今日无影响，快照刷新拆开同价对时点分候选回退可能二义）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - 引擎：`src/CCUsage.Core/Models/UsagePricingPlan.cs`（新）、`src/CCUsage.Core/Models/UsageRecord.cs`、`src/CCUsage.Core/Models/ModelPriceEntry.cs`、`src/CCUsage.Core/Services/PricingEngine.cs`、`src/CCUsage.Core/Services/PricingModelMatcher.cs`、`src/CCUsage.Core/Services/ModelsDevPricingParser.cs`、`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`
  - 九源接线：`src/CCUsage.Infrastructure/OpenCodeSqlite/OpenCodeMessageParser.cs`、`GooseSqlite/GooseSessionMapper.cs`、`HermesSqlite/HermesSessionParser.cs`、`KiloSqlite/KiloMessageParser.cs`、`Codebuff/CodebuffChatParser.cs`、`PiAgent/PiSessionParser.cs` + `PiAgentAdapter.cs`、`Grok/GrokLogParser.cs`、`OpenClaw/OpenClawLogParser.cs`、`Antigravity/AntigravityParser.cs` + `AntigravityAdapter.cs`；DI：`src/CCUsage.App/DependencyInjection/AppServices.cs`
  - 测试：`tests/CCUsage.Core.Tests/Services/PricingEngineTests.cs`、`tests/CCUsage.Core.Tests/Services/PricingModelMatcherTests.cs`（新）、`tests/CCUsage.Infrastructure.Tests/{OpenCodeSqlite,GooseSqlite,HermesSqlite,KiloSqlite,Codebuff,PiAgent,Grok,OpenClaw,Antigravity}/*`、`tests/CCUsage.Reconciliation.Tests/*FixtureReconciliationTests.cs`（三处注释更新）
- How to run/verify: `dotnet build CCUsage.sln`（0 警告 0 错误）；`dotnet test CCUsage.sln`（全绿）；门控真实对账 `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~RealData"`。
- Results:（2026-10-12 实现完成）
  - 构建 0 警告 0 错误（含修复基线 23 个 xUnit1031 阻塞告警——AntigravityAdapterTests 同步改 async）。
  - 测试 **757 通过 / 22 跳过**（基线 728/22；+29 = Core 81→93、Infrastructure 559→576），全绿。
  - fixture 对账：十九源 fixture golden 全部全绿、**数字逐位不回归**（本卡未重录任何 golden；快照改价仅 kimi-k2.6——其唯一消费场景 OpenCode k2p6 在 fixture 中规避，kimi-k2.6 单测断言已更新为刷新价 0.0001506）；门控真实对账（gate=1）本机有数据的 3 项全绿、16 项因无数据 Skip，无回归。
  - 验收口径抽查：k2p6 / kimi.k2.6 / gemini.2.5.pro / grok-4.5-build / [openclaw] claude-sonnet-4-5 / gemini-3.8-flash-medium 等此前记 0 的形状现在全部命中价格；mystery-model-x / grokbuild-alpha-x1 / totally-unknown-model 等真实未知模型仍 Missing + UnpricedModels fail-loud。
