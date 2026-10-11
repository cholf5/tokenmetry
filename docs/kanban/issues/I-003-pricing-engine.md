---
id: I-003
title: 自有定价引擎（内置快照 + models.dev + 用户覆盖）
type: feature
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

实现自有定价引擎：构建期内置价格快照 + models.dev 联网刷新（TTL 24h、失败静默回退）+ 用户覆盖规则，三级回退；为 JSONL usage 记录计算成本。

## Background

- 自主采集后 cost 由我们自己计算，不再依赖 CLI。
- 定价刷新永远在后台执行，绝不阻塞 UI 或刷新主路径——ccusage 慢的根因就是把联网刷新放在关键路径（`docs/chore/why-ccusage-so-slow.md`）。
- 蓝本：`../ccusage-tauri/src-tauri/src/pricing_refresh.rs`（三级回退、TTL）与 `claude.rs` 中 cache write 1h 按 input × 2 计价的规则。

## Acceptance Criteria

- [x] 断网环境下用内置快照完成定价，UI 与刷新流程不被网络阻塞（引擎无任何网络路径，测试验证；页面接线属 I-005）
- [x] models.dev 刷新后台执行，TTL 24h，失败时沿用上一次成功数据（可注入 handler 单测验证）
- [x] 用户覆盖规则（exact 模型名）生效并持久化到设置
- [x] 价格未命中的模型有记录（供对账测试与诊断使用）

## Subtasks

- [x] 内置价格快照嵌入与加载
- [x] models.dev 拉取（TTL 24h、后台、失败回退）
- [x] 用户覆盖规则（exact 匹配）与持久化
- [x] 计价 API（含 cache write 5m / 1h 倍率规则）
- [x] 未命中模型的记录与暴露
- [x] 单元测试（快照定价、覆盖生效、未命中记录）

## Dependencies

- none（与 I-001 可并行）

## Test Cases

### TC-001: 离线定价

Steps:
1. 断网状态下对样例记录计价。

Expected:
- 内置快照命中并返回成本；无网络请求、无阻塞。

### TC-002: 用户覆盖优先

Steps:
1. 为某模型设置覆盖价格后重新计价。

Expected:
- 使用覆盖价格，优先级高于快照与远程数据。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（实现）

**交付内容**

- 计价模型（Core.Models）：`ModelPriceEntry`（USD/百万 tokens 单价 + 家族模式）、`RemotePricingSnapshot`（不可变整体替换的远程价格表）、`PricingSourceKind`/`PricingResult`（Missing=0，未计价时 `CostUsd` 为 null 而非 0）、`TokenUsageDetail`、`UnpricedModelInfo`、`ModelPricingOverride`。
- 定价引擎（Core.Services）：`PricingEngine`（三级回退：用户覆盖 exact → 远程 → 内置快照；未命中记录可枚举、可清空）、`PricingModelMatcher`（候选名归一化 + 匹配，蓝本 pricing.rs 移植）、`BuiltinPricingSnapshot`（嵌入资源加载）、`ModelsDevPricingParser`（Core/Infrastructure 共用解析器）。
- models.dev 客户端（Infrastructure.ModelsDev）：`ModelsDevPricingClient : IRemotePricingSource`（TTL 24h、单飞、失败静默、handler 可注入）。
- 设置：`AppSettings.PricingOverrides`（List，settings.json 经现有 `JsonAppSettingsStore` 透明持久化）。
- 未接 BackgroundRefreshService / IUsageDataSource / 页面（I-005 接线）；未改 facts 存储与 CCUsageCli、ClaudeCodeJsonl 适配器。

**内置快照来源与日期**

- 数据取自 2026-10-11 实测 `https://models.dev/api.json`（当日全量约 5.4MB、226 个 provider；docs/chore/why-ccusage-so-slow.md 记载的 2.36MB 已过时），整理为 models.dev 形状的嵌入资源 `src/CCUsage.Core/Pricing/model-pricing-snapshot.json`（csproj EmbeddedResource，LogicalName `CCUsage.Core.Pricing.model-pricing-snapshot.json`）。
- anthropic 25 条：现行模型（opus-4-5/4-6/4-7、sonnet-4-5/4-6、sonnet-5/5-5、haiku-4-5/5-5、fable-5/5-1 等，取实测价）+ 家族/别名模式（claude-sonnet-4、claude-opus-4、claude-3-5-sonnet、claude-3-opus、claude-3-5-haiku、claude-3-haiku 等，价目取蓝本 adapters/pricing.rs 表）。注意：opus-4-8/opus-5/opus-5-5 原录入价目有误（2×），已在独立审查后按 2026-10-11 实测修正，见 Development Log。
- openai 19 条：gpt-5/5.1/5.2/5.3/5.4/5.5/5.6-sol、gpt-5-mini/5.1-mini、gpt-5-nano/5.1-nano、gpt-6-luna/sol/astra、codex、gpt-4o/4o-mini、gpt-4.1、o3（codex CLI 主流模型）。
- 快照条目可带 `match` 家族模式（最长模式包含匹配），用于命中 models.dev 已移除的历史 id（如 claude-sonnet-4-20250514、claude-3-5-sonnet-20241022——当日实测 anthropic 下已无这些条目）。远程刷新数据按精确 id 覆盖快照。

**models.dev API 形状**（实测确认）

- 现行形状为 provider map：`{ "<provider>": { "id": ..., "models": { "<modelId>": { "id", "name", "cost": { "input", "output", "cache_read", "cache_write" } } } } }`，价格单位 USD / 百万 tokens；`cache_write` 即 5m cache write 价。
- 部分模型带 `tiers` / `context_over_200k` 上下文分档（如 gpt-5.4/5.5/5.6-sol、gpt-6 系）——按基础档计价，分档忽略（蓝本同口径）。
- gpt-5 系普遍缺 `cache_write`（仅 cache_read），按 input × 1.25 推导；anthropic 全带四项。
- 兼容旧形状 `{ "models": [...] | {...} }` 与顶层模型数组（蓝本 parse_models_dev_pricing 三分支）；无价模型跳过，0 条可用视为刷新失败。

**倍率规则出处**（蓝本 ccusage-tauri `aggregation.rs price_entry`，非猜测）

- cost = input×input价 + output×output价 + cache_creation_5m×cache write价 + cache_creation_1h×(input价×2) + cache_read×cache read价（蓝本 `cache_creation_1h * pricing.input * 2.0`，测试 `claude_pricing_preserves_ephemeral_1h_cache_cost` 同构）。
- 5m 部分按模型 cache write 价（Claude 全系 = input × 1.25）；有 5m/1h 细分时不再对 cache_creation 总数重复计价（蓝本 `cache_creation_5m = 细分 ? 5m : 总数` 分支）；无细分时整体按 cache write 价。
- cache 单价缺省推导 input × 1.25 写 / × 0.1 读（蓝本 pricing_refresh.rs `parse_model` 与 adapters/pricing.rs `Rates::simple`）；用户覆盖缺省同规则（蓝本 save_rule）。
- `-fast` 后缀为采集标记，候选名剥离后匹配；provider 前缀（如 `openrouter/x`）优先在该 provider 内精确匹配，同名模型按 provider rank（anthropic/openai/google 最优）取优（蓝本 resolve_cached_price）。
- 成本用 decimal 精确计算（蓝本 f64）；`PriceRecord`：日志自带 costUSD 直接采用（LogCost，对齐 price_entry 的 precomputed 分支），否则按 token 明细计算。

**TTL / 刷新设计**

- TTL 24h（蓝本 `DEFAULT_PRICING_TTL_SECONDS = 86_400`），源 URL `https://models.dev/api.json`；`IsRefreshDue` = 从未成功 || now − FetchedAt ≥ TTL（TimeProvider 可注入，测试用假时钟）。
- 查询路径只读 volatile 内存快照 `Current`，永不阻塞、不触发网络（ccusage 慢的根因即联网进关键路径）；网络仅在显式 `RefreshIfDueAsync`/`RefreshAsync`（20s 超时、SemaphoreSlim 单飞），永不抛出，失败静默沿用上次成功数据并记录 `LastError`/`LastSuccessAt`/`LastAttemptAt`。
- 远程价格仅存内存（不落盘、不进 facts 存储）：应用重启回退内置快照，属可接受降级；响应体解析后条目数为 0 视为失败（蓝本 MissingModels）。

**测试**

- 新增 41 个用例：解析器 8（三种形状、缺价跳过、分档忽略、match 字段、空表、坏 JSON）；引擎 24（快照定价、5m/1h 倍率、无细分、-fast/provider 前缀、gpt-5 系、未命中记录与清空、null 模型、LogCost 短路、覆盖优先/缺省 cache 价/大小写与 -fast/UpdateOverrides/无效覆盖忽略、远程覆盖快照、远程缺条目回退快照、openrouter 前缀取优、TryResolve 命中/未命中）；客户端 8（TTL 到期前后、未刷新即到期、失败沿用旧数据、HTTP 错误、空表、坏 JSON、取消不抛、单飞）+ 设置持久化 1。
- `dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` 180 通过 / 3 跳过 / 0 失败（基线 139 通过 / 3 跳过，净增 41）。

### 2026-10-11（独立审查修复）

- 独立审查结论 **FAIL**（代码逻辑全部合格——计价公式、匹配顺序、客户端并发语义、覆盖规则、未命中语义均与蓝本一致；问题全在快照数据），修复：
  - **[P0] 快照 3 条 Opus 价目 = 实际值 × 2**（claude-opus-4-8 / opus-5 / opus-5-5 写成 10/50/1/12.5 与 8/40/0.4/10）：审查员当日实测 models.dev api.json（5.39MB / 226 provider，与开发日志口径吻合）+ 蓝本 pricing.rs（opus-4-8 → 5e-6）+ 快照自身 opus-4-5/4-6/4-7 行三源互证。断网/首启场景 Opus 档成本静默翻倍且因"命中"永不进未命中清单。已改为实测值（5/25/0.5/6.25 与 4/20/0.2/5），同步修正 2 处被错误数据固化的测试断言，任务卡"现行模型取实测价"的表述按此更正。
  - **[P2] 家族模式吞掉更便宜的子型号**：gpt-4.1-mini / gpt-4.1-nano / o3-mini / o4-mini 原被 gpt-4.1 / o3 家族模式按旗舰价计（虚高 2.5~5 倍）。补 4 条精确条目（models.dev 实测：0.4/1.6/0.1、0.1/0.4/0.025、1.1/4.4/0.55、1.1/4.4/0.275，cache_write 缺失按 input×1.25 推导），并新增回归测试（精确条目优先于家族模式）。
  - **[P2] RefreshAsync 在 Dispose 后可能抛 ObjectDisposedException**：违反"永不抛出"契约，补 catch → return false。
  - **[P2] ProviderRank 大小写敏感**：与蓝本一致（parity），models.dev id 全小写无实际影响，记录备查不修。
- 验证：全解决方案 181 通过 / 3 跳过 / 0 失败（含新增回归测试）。

### 2026-10-11（I-006 联动扩充）

- 内置快照新增 `zai` 段 22 条 GLM 价格（取自 ccusage 蓝本 models-dev-pricing.json 的 zai/* 键；input/output 缺失的条目丢弃，cache 两档缺失省略键走与 pricing.rs 相同的缺省乘数 write=×1.25 / read=×0.1），支撑 ZCode 适配器 Z.ai 限定行的离线计价。快照其余部分与解析器未动。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/CCUsage.Core/Services/PricingEngine.cs`、`src/CCUsage.Core/Services/PricingModelMatcher.cs`、`src/CCUsage.Core/Services/BuiltinPricingSnapshot.cs`、`src/CCUsage.Core/Services/ModelsDevPricingParser.cs`、`src/CCUsage.Core/Models/ModelPriceEntry.cs`、`src/CCUsage.Core/Models/PricingResult.cs`、`src/CCUsage.Core/Models/TokenUsageDetail.cs`、`src/CCUsage.Core/Models/ModelPricingOverride.cs`、`src/CCUsage.Core/Abstractions/IRemotePricingSource.cs`、`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`、`src/CCUsage.Infrastructure/ModelsDev/ModelsDevPricingClient.cs`、`src/CCUsage.Core/Models/AppSettings.cs`
- How to run/verify: `dotnet test tests/CCUsage.Core.Tests/CCUsage.Core.Tests.csproj`（引擎与解析器）与 `dotnet test tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj`（models.dev 客户端，可注入 handler，不依赖网络）
- Results: 2026-10-11 执行 `dotnet build CCUsage.sln`（0 警告 0 错误）+ `dotnet test CCUsage.sln`：Core 64 通过、App 38 通过、Infrastructure 79 通过 / 3 跳过，0 失败（基线 139 通过 / 3 跳过 → 181 通过 / 3 跳过）。审查后快照已按 models.dev 实测修正（Opus ×2 错价 + 4 条 mini/nano 精确条目），离线定价 TC-001 与覆盖优先 TC-002 均由单测覆盖并通过。
