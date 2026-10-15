---
id: I-004
title: Codex JSONL 适配器
type: feature
priority: P2
size: M
status: done
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

- [x] Codex 日志目录发现与解析，产出统一 usage 记录（agent 名取蓝本口径 "Codex"，即 codex.rs 的 AGENT_NAME）
- [x] 与 Claude 适配器共享扫描 / 去重 / 诊断计数框架，无复制粘贴的分叉逻辑（共享边界见 Development Log）
- [x] 损坏行跳过并计数，不中断解析
- [x] 对账测试覆盖 codex 样例 fixture（tokens 精确、cost 容差内）——以"codex fixture 经 PricingEngine 快照价计价"测试先行覆盖（`Codex_records_are_priced_by_pricing_engine_with_snapshot_rates`）；依赖 Q-001 对账比较器的与 ccusage CLI 输出比对部分转 Q-001

## Subtasks

- [x] Codex 日志目录发现
- [x] JSONL 解析与 usage 事件抽取（复用统一记录模型）
- [x] 接入增量扫描与诊断计数
- [x] fixtures + 单元测试

## Dependencies

- I-001（共享框架）、I-003（定价）

## Test Cases

### TC-001: codex fixture 解析

Steps:
1. 使用 codex 样例 fixture 运行适配器。

Expected:
- 记录内容正确，agent=codex，tokens 与 fixture 预期一致。

Result: 通过（`CodexJsonlAdapterTests.Parses_sample_fixture_from_configured_home`，agent="Codex"）。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（实现完成）

移植与设计决策（蓝本 `ccusage-tauri/src-tauri/src/codex.rs`，逐条对齐其解析规则；真实格式另以本机 `~/.codex/sessions` 样本核对，fixtures 全部使用蓝本内联测试的合成数据，无真实会话内容）：

- **目录发现**（`CodexLogLocator`，对齐 resolve_homes / codex_home_candidates / codex_sources_from_homes / collect_deduped_files）：配置 home 优先（支持 `~` 展开，不再回退自动探测）；否则 `CODEX_HOME`（逗号分隔多 profile）→ `~/.codex`（USERPROFILE 优先于 HOME，与 Claude 定位器一致）；每个 home 优先 `sessions/` 与 `archived_sessions/`，两者都不存在时回退 home 本身；同一 home 下同相对路径的归档副本让位给活动 sessions 副本。环境变量经注入访问器读取，测试不依赖真实用户目录。
- **token 口径映射**：Codex 的 `input_tokens` 含缓存部分。映射到统一记录为 `InputTokens = input − cached`（非缓存输入）、`CacheReadTokens = cached`（截断至 input）、`OutputTokens` 照抄（已含 reasoning）。三者之和与蓝本 `total_tokens` 精确一致，且 PricingEngine 的计价公式（非缓存 input 价 + cache_read 价 + output 价）与蓝本 price_codex 完全等价。
- **成本**：适配器不内联计价（蓝本 price_codex 不移植），`CostUsd = null`，统一由 I-003 PricingEngine 按 models.dev 快照价计算（快照含 gpt-5 系逐版本价，比蓝本的 nano/mini/gpt-5 粗分档更精确）；诊断上体现为 `MissingCostRecords = 全部记录`。
- **agent 命名**：取蓝本 `AGENT_NAME = "Codex"`（任务卡 Goal 的小写 `codex` 视为规格速记；UsageRecord.Agent 为展示口径，与 "Claude Code" 风格一致）。
- **模型回退链**：payload.model → payload.model_name → info.model → info.model_name → turn_context 记忆 → 回退 `gpt-5`（FALLBACK_MODEL），因此 Codex 记录 Model 恒不为 null（UnknownModelRecords 恒 0）。

共享 / 分叉边界（AC2）：

- **共享（无分叉复制）**：I-001 的增量扫描状态机从 `ClaudeCodeJsonlAdapter` 提取为 `IncrementalJsonlScanner`（文件 mtime/大小记账、只重解析变更文件、打不开/中途失败的落账与重试语义、消息级去重、诊断计数与 Ok/Partial/NoData 状态计算），Claude 与 Codex 适配器共用同一实现；`JsonlFileCollector`（递归 .jsonl 枚举、跳过符号链接与不可读目录）、`JsonlHomePath`（USERPROFILE/HOME 与 `~` 展开）、`JsonlTimestamp`（统一记录时间戳解析）同样两处共用；消息级去重直接复用 `ClaudeUsageRecordDeduper`（Codex 记录无 message id，去重按设计直通，重复保证由文件级记账 + 归档副本去重承担）。
- **结果模型泛化**：`ClaudeCodeScanResult` / `ClaudeCodeScanDiagnostics` / `ClaudeCodeScanStatus` 泛化为规范名 `JsonlScanResult` / `JsonlScanDiagnostics` / `JsonlScanStatus`（Core.Models），I-001 旧名保留为空壳兼容别名（枚举以 const 值集合同名暴露）；`IClaudeCodeJsonlAdapter` 对外签名与 Claude 解析规则、扫描行为不变，Claude 现有 79 项测试原样通过。
- **分叉（必须不同）**：`CodexLogLocator`（CODEX_HOME / sessions+archived 结构与按 home 相对路径去重，Claude 的 projects/ 结构不适用）与 `CodexJsonlFileParser`（token_count 为逐 turn 累计快照，必须跨行维护 previous_totals 差分状态；行预过滤标记为 token_count/turn_context/session_meta 三个子串）。
- **malformed 口径差异**：蓝本 codex 用单次 serde `from_str` 同时完成 JSON 语法与 DTO 类型校验，故字段类型不符的行与坏 JSON 同计 `malformed_lines`（Claude 口径为 DTO 失败计 invalid）；C# 侧以 `JsonDocument.Parse` + 严格 DTO 反序列化两级实现，两级失败均计 malformed，行为与蓝本一致。
- **有意偏差**（均已在代码注释说明）：① 统一记录要求可解析的 `DateTimeOffset` 时间戳，缺失/空白静默跳过（与蓝本一致），存在但不可解析计 invalid（Claude 适配器同口径）；② token 分量用 `ulong?` 对应 serde u64，超出 long 范围的异常值按 invalid 拒绝（蓝本 u64 直通，实际不可达）；③ 蓝本的 `total == 0 → invalid` 分支与 Disabled 扫描状态已移植/不移植的取舍：前者不可达但逐条对齐保留，后者依赖数据源启停设置（桌面端暂无该设置，NoData 覆盖同等场景）。
- **接口设计**：独立 `ICodexJsonlAdapter`（与 Claude 接口同形态：`ScanAsync → JsonlScanResult`）。不抽公共接口的理由：两者构造依赖不同（配置根语义分别是 CLAUDE_CONFIG_DIR 式根目录与 CODEX_HOME 式 home），I-005 将独立注册与接线，共享已发生在实现层（引擎/去重器/结果模型），共享接口在接线方案定型（I-005）前只会约束设计。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 207 通过 / 3 跳过（基线 181/3 原样保留，新增 26 项 Codex 测试）。另以本机真实 `~/.codex/sessions` 做过只读 smoke（19 文件 / 5802 记录 / 0 malformed / 全部模型命中快照价 / 二次扫描幂等），仅用于验证，未将任何真实数据带入仓库。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/CodexJsonl/`（CodexJsonlAdapter、CodexJsonlFileParser、CodexLogLocator）、`src/Tokenmetry.Infrastructure/Jsonl/`（IncrementalJsonlScanner、JsonlFileCollector、JsonlHomePath、JsonlTimestamp、JsonlScanOutput）、`src/Tokenmetry.Core/Models/JsonlScanResult.cs`、`src/Tokenmetry.Core/Abstractions/ICodexJsonlAdapter.cs`、`tests/Tokenmetry.Infrastructure.Tests/CodexJsonl/`、`tests/Tokenmetry.Infrastructure.Tests/fixtures/codex/`；Claude 侧改动：`src/Tokenmetry.Infrastructure/ClaudeCodeJsonl/ClaudeCodeJsonlAdapter.cs`（接入共享引擎）、`ClaudeCodeLogLocator.cs`（接入共享收集器）、`ClaudeCodeJsonlLineParser.cs`（时间戳解析共用）、`src/Tokenmetry.Core/Models/ClaudeCodeScanResult.cs`（兼容别名）
- How to run/verify: `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj`
- Results: 2026-10-11 — `dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 全绿：Core 64 通过、Infrastructure 105 通过 / 3 跳过（含 26 项新增 Codex 测试）、App 38 通过，合计 207 通过 / 3 跳过（基线 181/3 全部保留）

### 2026-10-11（独立审查修复）

- 独立审查结论 PASS with minor issues；token 映射口径与价格公式经独立验证与发布版 ccusage CLI 精确一致，但发现 **[P1] 真实数据上逐 turn 双重记账（~2×）**：
  - 根因：Codex CLI 每个 turn 连发两条 token_count（第二条为重复快照）；蓝本 codex.rs 的 `last.or_else(delta)` 无累计推进守卫，重复事件也产出记录。审查员用本机真实数据对照发布版 ccusage CLI v20.0.29 实测：桌面端 input 23,958,392 vs CLI 12,348,058（1.94×）、cost 1.98×。
  - 修复：移植 ccusage 的 `cumulative_advanced` 守卫——仅当 `total_token_usage` 缺失或与上次不同时采信 `last_token_usage`，否则退回差分（为零即被全零检查跳过）。新增重复事件回归测试。
  - **修复后本机真实数据复扫：input 12,348,058 / cacheRead 353,026,048 / output 1,273,689，与 ccusage CLI 三个数字完全一致**——Q-001 "tokens 精确一致" 的对账基础成立。
- 顺带修复 [P2]：`total==0` 分支与 overflow 检查的双计数可能（overflow 检查前移；total==0 保持蓝本语义"计 invalid 仍产出"，仅 cached>0 的退化事件可达）。
- 备忘（转 Q-001）：真实日志已出现 `token_usage_record` 与 `compacted` 事件，新版 ccusage 有对应 compaction 去重逻辑、蓝本未覆盖，本机数据暂未受影响（0 malformed），对账测试时留意。
