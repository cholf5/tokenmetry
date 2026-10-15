---
id: I-001
title: Claude Code JSONL 直读适配器
type: feature
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

在 `Tokenmetry.Infrastructure` 新增 Claude Code JSONL 直读适配器，把本地会话日志解析为统一 usage 记录，摆脱对 `ccusage` CLI 的运行时依赖。

## Background

- 自主采集是 v0.3 的第一步：CLI 输出格式受制于人，且默认联网刷新极慢（依据见 `docs/chore/why-ccusage-so-slow.md`）。
- 解析规则参照 `../ccusage-tauri/src-tauri/src/claude.rs` 逐条移植（多根目录发现、cache_creation 5m/1h token 细分、损坏行诊断计数）。
- 产出的统一记录模型是 I-002 facts 表和 I-003 定价引擎的输入。

## Acceptance Criteria

- [x] 能从默认路径及 `CLAUDE_CONFIG_DIR` 自定义路径发现并解析 JSONL，产出统一 usage 记录（model、agent、时间戳、input / output / cache_creation / cache_read tokens）
- [x] 增量扫描：只解析新增或变更的文件，重复运行不产生重复记录
- [x] 损坏行 / 未知行跳过并计入诊断计数，不中断整体解析
- [x] 单测覆盖：样例 JSONL fixture、多根目录、空目录、损坏行

## Subtasks

- [x] 日志目录发现（默认路径 + `CLAUDE_CONFIG_DIR` + 多 profile）
- [x] JSONL 行解析与 usage 事件抽取（定义统一记录模型）
- [x] cache_creation 5m / 1h token 细分与计价标记
- [x] 增量扫描（文件 mtime / offset 记账）与消息级去重
- [x] 诊断计数（malformed / duplicate / unknown-model）
- [x] fixtures + 单元测试

## Dependencies

- none

## Test Cases

### TC-001: 样例 fixture 解析

Steps:
1. 使用仓库内样例 JSONL fixture 运行适配器。

Expected:
- 记录数量与内容与 fixture 预期一致，tokens 字段正确。

### TC-002: 重复运行幂等

Steps:
1. 对同一目录连续运行两次适配器。

Expected:
- 第二次运行不产生重复记录（增量或去重生效）。

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（实现）

- 完成 Claude Code JSONL 直读适配器，统一记录模型与抽象放 Core，实现放 `src/Tokenmetry.Infrastructure/ClaudeCodeJsonl/`（与 `CCUsageCli/`、`Cache/` 平级）：
  - `Tokenmetry.Core/Models/UsageRecord.cs`：统一 usage 记录（model、agent、时间戳、input / output / cache_creation / cache_read tokens、cache_creation 5m/1h 细分、MessageId / RequestId 消息级去重标识、SourceFile 诊断字段、TotalTokens 汇总口径与 ccusage 一致）。
  - `Tokenmetry.Core/Models/ClaudeCodeScanResult.cs`：扫描结果 + 诊断计数（files_discovered / files_read / lines_skipped / malformed / invalid / duplicate / missing_cost / unknown_model + Status）。
  - `Tokenmetry.Core/Abstractions/IClaudeCodeJsonlAdapter.cs`：适配器接口，产出 `Task<ClaudeCodeScanResult>`。
  - `ClaudeCodeLogLocator`：根目录解析（显式配置优先；否则 `CLAUDE_CONFIG_DIR`（逗号分隔多 profile、`~` 展开，存在时不再回退）→ `XDG_CONFIG_HOME/claude` → `~/.config/claude` → `~/.claude`）、projects 目录归一化、递归枚举 .jsonl。环境变量经可注入访问器读取，测试不依赖真实用户目录。
  - `ClaudeCodeJsonlLineParser`：usage 标记快筛、agent progress 包装行（`data.message.message.usage`）识别、显式 null cache token 拒绝、字段判空、semver 前缀校验、模型归一化（`<synthetic>` → null、speed=fast → `-fast` 后缀）、cache_creation 5m/1h 细分（I-003 按 1h=2×input 计价的输入）。DTO 反序列化规则逐条对齐 claude.rs（required 字段缺失、u64 负数/非整数、未知 speed 均整条拒绝）。
  - `ClaudeUsageRecordDeduper`：(message.id, requestId-or-空串哨兵) 精确 key 折叠 auto 模式流式部分行；同 message id 下任一方为 sidechain 视为回放并优先保留父记录；其余按 token 总量、`-fast` 后缀择优；无 message id 不参与去重。
  - `ClaudeCodeJsonlAdapter`：增量扫描按文件 mtime/大小进程内记账，只重解析新增/变更文件，删除文件同步移除其记录；重复运行幂等。未接 App DI、未动 IUsageDataSource 与页面（接线为 I-005）。
- 关键决策：
  - 时间戳统一解析为 `DateTimeOffset`（AdjustToUniversal | AssumeUniversal，无时区按 UTC），无法解析按 invalid 计——claude.rs 保留原始字符串，其下游聚合（aggregation.rs）对部分格式还有回退，实际比本实现宽松；该偏差无害（真实日志为严格 RFC3339），统一模型直接给 facts/定价可用的时间戳。
  - costUSD 用 `decimal?`（对齐现有 Core 模型的成本口径）。
  - System.Text.Json 的 `JsonElement.TryGetProperty` 是字面属性名查找而非 JSON Pointer，自实现 RFC 6901 指针解析（serde_json `Value::pointer` 语义：路径存在即命中、null 也算存在）用于包装行识别与 null 拒绝。
  - 空串与 null 区分：claude.rs `is_some_and(|s| s.trim().is_empty())` → null 合法（sessionId 回退文件推导值）、显式空串才拒绝；打不开的文件计入 lines_skipped 且不计 files_read，均与 claude.rs 对齐。
  - 诊断口径：FilesRead / LinesSkipped / Malformed / Invalid 为本次解析事件（增量下未变更文件不重计）；Duplicate / MissingCost / UnknownModel 按当前累计去重后的结果集计算，多次运行稳定。
- 验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 全绿 114 通过 / 3 跳过（跳过为既有 Windows 专属测试，未改动）。新增 49 个测试（parser 25、locator 9、deduper 5、adapter 10），fixtures 取自 claude.rs 内联测试的真实 payload。

### 2026-10-11（独立审查修复）

- 独立审查结论 PASS with minor issues（无 P0），修复：
  - [P1] 增量扫描下坏文件状态跨运行翻转（Partial → Ok）：适配器按文件记忆"存在解析错误"，状态聚合时纳入未重新解析的历史坏文件；文件修复重解析后恢复 Ok。新增回归测试。
  - [P2] DTO 改为精确 JSON 名绑定（补全全部 `JsonPropertyName`）并删除 `PropertyNameCaseInsensitive`，恢复 serde 严格性——"SessionID" 等大小写变体按未知字段忽略，不再被误绑定。新增回归测试。
  - [P2] 补 `isApiErrorMessage`（serde `Option<bool>`）类型校验：字符串等错误类型整条拒绝，与 claude.rs 一致。新增回归测试。
  - [P2] 读取中途 IO 失败不再落账：丢弃本次部分结果与计数、保留上次成功解析的记录，下次扫描重读整个文件；打不开的文件同样不落账。为此给适配器加了可注入 reader 工厂，新增中断重试回归测试。
- 记录的已知取舍（审查确认，不修）：
  - (mtime, size) 记账存在"同字节长度改写 + mtime 粒度内"的遗漏窗口——对 append-only 的 Claude 日志风险极低；实现为 mtime+size 变更时整文件重解析，无 offset 续读（子任务原文写 offset 记账，实际采用此简化）。
  - 诊断口径与 claude.rs 的已声明偏差保留：Duplicate / MissingCost / UnknownModel 按累计结果集计算，下游（I-002 / I-005）消费时需知晓。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Core/Models/UsageRecord.cs`、`src/Tokenmetry.Core/Models/ClaudeCodeScanResult.cs`、`src/Tokenmetry.Core/Abstractions/IClaudeCodeJsonlAdapter.cs`、`src/Tokenmetry.Infrastructure/ClaudeCodeJsonl/`（ClaudeCodeJsonlAdapter / ClaudeCodeJsonlLineParser / ClaudeCodeLogLocator / ClaudeUsageRecordDeduper）、`tests/Tokenmetry.Infrastructure.Tests/ClaudeCodeJsonl/`、`tests/Tokenmetry.Infrastructure.Tests/fixtures/claude-code/`（sample-session / corrupted-lines / duplicate-messages）
- How to run/verify: `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj`（全解决方案：`dotnet build Tokenmetry.sln && dotnet test Tokenmetry.sln`）
- Results: 2026-10-11 — build 0 警告 0 错误；全解决方案 118 通过 / 3 跳过（Windows 专属基线保持）；其中 Infrastructure.Tests 63 通过 / 3 跳过，新增 53 个测试全部通过（含审查修复后的 4 个回归测试）。TC-001（样例 fixture 解析：6 条记录、tokens/模型/会话字段与 fixture 预期一致）与 TC-002（重复运行幂等：第二次 FilesRead=0、记录集不变、去重计数稳定）均由单测覆盖并通过。审查复核通过（AC 逐条 ✓）。
