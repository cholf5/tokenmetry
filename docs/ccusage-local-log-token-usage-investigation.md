# ccusage 本地日志 token 用量解析调查报告

日期：2026-06-19

## 1. 调查目标与结论

本报告调查 `ccusage` 如何从 Claude Code、Codex 等编码助手的本地日志中解析 token 用量，并评估这些逻辑对 `ccusage-gui` 的启示。

核心结论：

1. `ccusage` 当前的真实解析逻辑主要在 Rust CLI 中实现，路径为 `ccusage/rust/crates/ccusage/src/adapter/*`。`ccusage-gui` 目前并不直接解析本地日志，而是通过 `CCUsageCliRunner` 调用 `ccusage --json --offline daily`，再用 `CCUsageCliParser` 解析 CLI JSON 输出。
2. Claude Code 与 Codex 的本地日志格式差异很大：
   - Claude Code 直接在 JSONL 行中写入 `message.usage`、`message.model`、`timestamp`、`requestId`、`message.id` 等信息。
   - Codex 主要依赖 `event_msg` / `token_count` 事件，且 `total_token_usage` 是累计值，需要通过相邻累计值相减得到每轮 delta；也支持 `codex exec --json` 保存的 headless JSONL。
3. `ccusage` 不根据提示词文本估算 token。它只支持本地文件中已经包含可映射的 token 计数字段、模型、时间戳和会话信息的数据源。
4. 准确统计的关键不只是读取字段，还包括：目录发现、JSONL 快速过滤、容错解析、去重、sidechain/subagent replay 处理、模型别名/回退、成本模式和时区聚合。

## 2. 代码位置速览

| 主题 | 关键文件 | 说明 |
| --- | --- | --- |
| Agent adapter 总体约定 | `ccusage/rust/crates/ccusage/src/adapter/README.md` | 每个数据源独立负责路径、解析、模型映射和特定定价语义。 |
| Claude 路径发现 | `ccusage/rust/crates/ccusage/src/adapter/claude/paths.rs` | 查找 `~/.config/claude/projects/`、`~/.claude/projects/` 或 `CLAUDE_CONFIG_DIR`。 |
| Claude 主加载/解析 | `ccusage/rust/crates/ccusage/src/adapter/claude/mod.rs` | 读取 JSONL、解析 `UsageEntry`、成本计算、去重。 |
| Claude daily 快速路径 | `ccusage/rust/crates/ccusage/src/adapter/claude/daily.rs` | 直接读 daily summary 所需字段并聚合。 |
| Codex 路径发现 | `ccusage/rust/crates/ccusage/src/adapter/codex/paths.rs` | 查找 `${CODEX_HOME:-~/.codex}/sessions/` 和 `archived_sessions/`。 |
| Codex 类型/字段兼容 | `ccusage/rust/crates/ccusage/src/adapter/codex/types.rs` | 定义 session/headless 日志 DTO，兼容多种 usage 字段名。 |
| Codex 事件解析 | `ccusage/rust/crates/ccusage/src/adapter/codex/parser.rs` | 识别 `token_count`、计算 delta、模型回退、subagent replay 处理。 |
| Codex 加载去重 | `ccusage/rust/crates/ccusage/src/adapter/codex/loader.rs` | 收集文件、并行读取、事件级去重。 |
| Codex 聚合 | `ccusage/rust/crates/ccusage/src/adapter/codex/aggregate.rs` | 按 daily/monthly/session 聚合、跨文件去重。 |
| Codex 报告/成本 | `ccusage/rust/crates/ccusage/src/adapter/codex/report.rs` | Codex 专用 JSON/table 输出和成本计算。 |
| 通用 token 类型 | `ccusage/rust/crates/ccusage/src/types.rs` | `TokenUsageRaw`、`UsageSummary`、`CodexRawUsage` 等核心结构。 |
| 通用 Claude 成本 | `ccusage/rust/crates/ccusage/src/cost.rs` | Display/Auto/Calculate 成本模式和缓存 token 成本。 |
| ccusage-gui 调用 CLI | `ccusage-gui/src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliRunner.cs` | 启动外部 `ccusage` 进程并读取 stdout。 |
| ccusage-gui 解析 CLI JSON | `ccusage-gui/src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliParser.cs` | 只解析 CLI 的 daily JSON，而非直接解析原始日志。 |

## 3. Claude Code 日志解析流程

### 3.1 数据目录发现

Claude adapter 的数据目录逻辑在 `claude/paths.rs`：

- 默认查找：
  - `~/.config/claude/projects/`
  - `~/.claude/projects/`
- 如果设置 `CLAUDE_CONFIG_DIR`，则只使用该环境变量指定的目录；支持逗号分隔多个目录。
- `CLAUDE_CONFIG_DIR` 可以指向 Claude config 根目录，也可以直接指向 `projects/`，代码会规范化到 config 根目录。
- `projects/` 会递归扫描 `.jsonl` 文件，兼容两类结构：
  - 现代/扁平：`projects/{project}/{sessionId}.jsonl`
  - 嵌套：`projects/{project}/{sessionId}/{file}.jsonl`
  - subagent：`projects/{project}/{sessionId}/subagents/{file}.jsonl`

项目名来自路径中 `projects` 后的第一个 segment。session 报告使用路径推导出来的 session：扁平文件取文件名去掉 `.jsonl`；嵌套文件取 session 目录名；subagent 文件归入父 session。

### 3.2 JSONL 行过滤和容错

Claude 主解析函数是 `read_usage_file`。流程如下：

1. 整个文件按字节读取。
2. 按行遍历 JSONL。
3. 先用 `memmem` 搜索字节片段 `"usage":{`，没有 usage 的行直接跳过，避免对所有行做 JSON 反序列化。
4. 调用 `has_unsupported_null_field` 跳过已知不允许为 `null` 的字段，例如 `model`、`speed`、`costUSD`、`version`、`sessionId`、`requestId`、`cache_read_input_tokens` 等。
5. 用 `serde_json::from_slice::<UsageEntry>` 解析行。解析失败、时间戳无效、版本/session/request/message/model 为空等情况都会跳过该行。
6. 单个文件无法读取时返回空结果，不中断整体加载。

这种设计说明 `ccusage` 偏向“尽量读取可用数据”，而不是遇到坏行就失败。

### 3.3 Claude 日志核心字段

Claude 的一条可统计记录反序列化为 `UsageEntry`，关键字段包括：

```text
UsageEntry
- sessionId?: string
- timestamp: string
- version?: string
- message: UsageMessage
- costUSD?: number
- requestId?: string
- isApiErrorMessage?: boolean
- isSidechain?: boolean

UsageMessage
- usage: TokenUsageRaw
- model?: string
- id?: string

TokenUsageRaw
- input_tokens: u64
- output_tokens: u64
- cache_creation_input_tokens: u64
- cache_read_input_tokens: u64
- speed?: standard|fast
- cache_creation?: {
    ephemeral_5m_input_tokens: u64
    ephemeral_1h_input_tokens: u64
  }
```

Token 总量的基本公式是：

```text
total = input_tokens
      + output_tokens
      + cache_creation_token_count()
      + cache_read_input_tokens
```

其中 `cache_creation_token_count()` 优先使用新版 `cache_creation.ephemeral_5m_input_tokens + ephemeral_1h_input_tokens`；如果没有该 breakdown，则回退到旧字段 `cache_creation_input_tokens`。

### 3.4 模型、成本和 speed 处理

Claude 的成本计算在 `cost.rs`：

- `--mode display`：只使用日志里的 `costUSD`，缺失则为 0。
- `--mode auto`：优先使用日志里的 `costUSD`；缺失时按 pricing 表计算。
- `--mode calculate`：总是按 token 和 pricing 表计算。

按 token 计算时会分别计算：

- input tokens
- output tokens
- cache creation 5m tokens
- cache creation 1h tokens：使用 input 价格的 2x multiplier
- cache read tokens
- 超过 200k token 的 tiered pricing
- `speed == fast` 时使用模型的 `fast_multiplier`

模型名处理上：

- `model == "<synthetic>"` 会被忽略，不进入模型 breakdown。
- `usage.speed == Fast` 时，模型名会追加 `-fast` 后缀，用于区分 fast 定价和展示。
- 聚合时还会通过 `model_aliases::resolve_model_name` 解析模型别名。

### 3.5 Claude 去重与 sidechain 处理

Claude 的去重逻辑非常关键，避免同一条消息在多个文件或 sidechain 中重复计费。

去重键主要是：

```text
(message.id, requestId)
```

如果发现 sidechain 场景，会额外按 `message.id` 做宽松匹配。原因是 Claude Code 的 `/btw` 等 side question 可能把父会话消息 replay 到 sidechain 日志里，保留同一个 message id 但换了 request id，并带有父消息的 cache read 用量。如果不去重，会明显高估 token。

替换规则大致为：

1. 如果候选和已有记录一个是 sidechain、一个不是，优先保留非 sidechain 的父记录。
2. 如果同类记录 token 总量不同，保留 token 总量更大的记录。
3. 如果 token 总量相同但 cost 不同，daily 快速路径保留 cost 更大的记录。
4. 如果一个记录带 speed 信息、另一个没有，优先保留带 speed 的记录。

这套逻辑同时存在于主加载路径 `claude/mod.rs` 和 daily 快速路径 `claude/daily.rs`。

### 3.6 Claude 聚合结果

解析后的 `LoadedEntry` 会携带：

- 原始 `UsageEntry`
- 解析后的 timestamp/date
- project/session/project_path
- cost
- model
- usage limit reset time
- missing pricing model 信息

之后的 report 层再按 daily、weekly、monthly、session、blocks 等视图聚合。daily 快速路径中会直接把 `DailyLoadedEntry` 聚合为 `UsageSummary`：累加 input/output/cache tokens 和 cost，并生成 per-model breakdown。

## 4. Codex 日志解析流程

### 4.1 数据目录发现

Codex adapter 的路径逻辑在 `codex/paths.rs`：

- 默认根目录：`${CODEX_HOME:-~/.codex}`。
- `CODEX_HOME` 支持逗号分隔多个目录。
- 对每个 Codex home，依次查找：
  - `sessions/`
  - `archived_sessions/`
- 如果两者都不存在，则把 home 本身当作 JSONL 目录读取，这使得保存的 `codex exec --json` 输出也能被统计。
- 如果同一个 Codex home 的 `sessions/` 和 `archived_sessions/` 中存在相同相对路径的 JSONL，active `sessions/` 版本优先，archive 副本跳过。

### 4.2 Codex 支持两类日志形态

Codex parser 支持两种主要形态：

1. **交互式 session JSONL**
   - 关注 `type == "turn_context"` 的模型上下文。
   - 关注 `type == "event_msg"` 且 `payload.type == "token_count"` 的 token 事件。
2. **headless / codex exec JSONL**
   - 行内可能直接包含 `usage`，也可能嵌套在 `data.usage`、`result.usage` 或 `response.usage`。
   - 模型可能在顶层 `model`/`model_name`、`metadata.model`，或嵌套对象中。

为减少 JSON 解析成本，`codex_line_usage_kind` 会先用字节搜索判断一行是否可能包含：

- `"type":"event_msg"`
- `"type":"turn_context"`
- `"type":"token_count"`
- `"usage":`
- `"input_tokens":`
- `"prompt_tokens":`

只有疑似相关的行才进入 serde 解析。

### 4.3 Codex session token_count 事件

Codex 的关键事件形态是：

```text
type == "event_msg"
payload.type == "token_count"
payload.info.total_token_usage  // 累计 token
payload.info.last_token_usage   // 当前 turn delta，若存在优先使用
```

解析策略：

1. 遇到 `turn_context`，从 payload 中读取当前模型并缓存。
2. 遇到 `event_msg/token_count`：
   - 读取 timestamp。
   - 读取 `last_token_usage`，如果存在则直接作为当前事件 token。
   - 如果没有 `last_token_usage` 但有 `total_token_usage`，就用当前累计值减去 `previous_totals` 得到 delta。
   - 更新 `previous_totals = total_token_usage`。
   - 如果所有 token 字段都是 0，则跳过。
3. 生成统一的 `CodexTokenUsageEvent`：

```text
CodexTokenUsageEvent
- session_id: string
- timestamp: string
- model?: string
- input_tokens: u64
- cached_input_tokens: u64
- output_tokens: u64
- reasoning_output_tokens: u64
- total_tokens: u64
- is_fallback_model: bool
```

需要注意：Codex 的 `reasoning_output_tokens` 是 informational breakdown，已经包含在 output billing 里，不会另行计费。

### 4.4 Codex usage 字段兼容

`CodexRawUsage` 的反序列化兼容多种字段名：

| 统一字段 | 可接受原始字段 |
| --- | --- |
| `input_tokens` | `input_tokens`、`prompt_tokens`、`input` |
| `cached_input_tokens` | `cached_input_tokens`、`cache_read_input_tokens`、`cached_tokens` |
| `output_tokens` | `output_tokens`、`completion_tokens`、`output` |
| `reasoning_output_tokens` | `reasoning_output_tokens`、`reasoning_tokens` |
| `total_tokens` | `total_tokens`，缺失或为 0 时可由 input + output + reasoning 回填 |

对于非关键字段，Codex 使用 lossy deserializer：对象类型不对、数字/字符串/布尔等意外类型通常会被当作 None，而不是让整行失败。这是为了适配 Codex CLI 日志格式演进。

### 4.5 Codex 模型解析和回退

Codex 模型来源优先级包括：

- 当前 `turn_context` 的 payload/model/model_name/metadata.model。
- `token_count` payload 或 info 上携带的 model/model_name/metadata.model。
- headless 结果的顶层、`data`、`result`、`response` 的 model/model_name/metadata.model。

如果当前事件没有显式模型，会沿用最近一次 `turn_context` 缓存的模型。如果仍没有模型，parser 会回退到 `gpt-5`，并标记 `is_fallback_model = true`。

此外，特殊模型标签 `codex-auto-review` 会按日志日期映射到内置 `codex-auto-review-fallbacks.json` 中的具体 OpenAI/Codex 模型，例如不同日期映射到不同的 `gpt-5.x` 系列。这个回退也会标记为 fallback。

### 4.6 Codex subagent replay 与跨文件去重

Codex 也有 replay 问题，尤其是 subagent/thread spawn 文件。

Parser 中的处理逻辑：

1. 先读取文件前 16KB，搜索 `thread_spawn` 判断是否可能是 subagent session。
2. 如果是 subagent，再扫描 token_count 行，检测开头多个 token_count 是否落在同一秒。这通常代表父历史在 subagent 创建时被 replay。
3. 对 replay 秒内的 token_count 事件跳过，但如果这些行带 `total_token_usage`，仍更新 `previous_totals`，这样后续真实 subagent 事件在用累计值相减时有正确 baseline。

此外，loader/aggregate 层还有事件去重：

- `loader.rs` 用 `(timestamp, model, input, cached, output, reasoning, total)` 去重，避免复制的分支历史重复计数。
- `aggregate.rs` 也用包含 timestamp、模型、token 字段的 key 去重；在 session 视图下会把 session id 也放进 key，以便两个不同 session 中完全相同的事件仍能分别出现在各自 session 报告里。

### 4.7 Codex 聚合和成本

Codex 聚合在 `aggregate.rs`，按 CLI 参数生成：

- daily：按本地时区日期 `YYYY-MM-DD`
- weekly：按周起始日期
- monthly：按 `YYYY-MM`
- session：按 session id

聚合字段：

```text
CodexGroup
- input_tokens
- cached_input_tokens
- output_tokens
- reasoning_output_tokens
- total_tokens
- models: BTreeMap<String, CodexModelUsage>
- last_activity
```

Codex 报告和成本在 `report.rs`，与 Claude 有几个重要差异：

1. 展示时 `inputTokens = input_tokens - cached_input_tokens`。
2. `cacheReadTokens = cached_input_tokens`。
3. `cacheCreationTokens = 0`，因为 Codex 当前映射的是 cached input/cache read，不像 Claude 那样有 cache creation breakdown。
4. 成本公式：

```text
cost = non_cached_input * input_price
     + cached_input * cache_read_price_or_input_price
     + output_tokens * output_price
```

5. `reasoning_output_tokens` 只展示，不单独计费。
6. `--speed auto|standard|fast` 是 Codex 特有选项；fast 模式使用模型的 `fast_multiplier`，如果 pricing 没有该 multiplier，则 fallback 到 2x。

## 5. ccusage 对“可支持数据源”的判断标准

`docs/guide/source-support-qa.md` 给出了判断原则：只有本地记录包含足够信息时才支持一个 agent。至少需要：

- 每消息或每轮 token counts。
- input/output token counts；如果有 cache/reasoning token，也要能映射。
- 模型标识，用于 pricing。
- 时间戳，用于日期过滤和分组。
- session/conversation 标识。
- 稳定的本地文件格式，例如 JSONL、SQLite 或结构化 telemetry export。

如果本地只有 prompt/transcript 文本、quota 百分比、云端状态或 opaque binary，`ccusage` 不估算 token。原因是估算无法覆盖隐藏 system context、工具调用 payload、缓存输入、provider-side accounting 和 tokenizer 差异。

这解释了为什么 adapters 目录里每个数据源都需要源特定解析器，而不是统一“读聊天记录后估算”。

## 6. Claude 与 Codex 的关键差异对比

| 维度 | Claude Code | Codex |
| --- | --- | --- |
| 默认目录 | `~/.config/claude/projects/`、`~/.claude/projects/` | `${CODEX_HOME:-~/.codex}/sessions/`、`archived_sessions/` |
| 环境变量 | `CLAUDE_CONFIG_DIR` | `CODEX_HOME` |
| 文件结构 | `projects/{project}/{session}.jsonl` 或嵌套 session/subagents | sessions/archived_sessions 下递归 JSONL；也支持直接 JSONL 目录 |
| 核心事件 | 行内 `message.usage` | `event_msg` + `payload.type=token_count`，或 headless `usage` |
| token 形态 | per-message usage | 常见为 cumulative total，需要转 delta |
| cache 字段 | `cache_creation_input_tokens`、`cache_creation`、`cache_read_input_tokens` | `cached_input_tokens` 等价映射为 cache read |
| reasoning 字段 | Claude 通用结构中无单独 reasoning | 有 `reasoning_output_tokens`，展示但不单独计费 |
| 成本来源 | 可用日志 `costUSD` 或按 pricing 计算 | 按 pricing 计算，Codex 有 speed 模式 |
| 模型回退 | `<synthetic>` 忽略；fast 追加 `-fast` | 缺模型回退 `gpt-5`；`codex-auto-review` 按日期映射 |
| 去重重点 | `message.id + requestId`，sidechain replay | cumulative delta、subagent replay、跨文件复制历史 |

## 7. 对 ccusage-gui 的启示

当前 `ccusage-gui` 的实现是 CLI wrapper：

- `CCUsageCliDataSource` 调用 `ccusage --json --offline daily`。
- `CCUsageCliRunner` 负责运行外部进程。
- `CCUsageCliParser` 解析 daily JSON 到 `DailyUsageSummary`。

这意味着 GUI 暂时不需要复制 Claude/Codex 原始日志解析逻辑。这个选择有明显优点：

1. 避免在 C# 中重写复杂且快速变化的 adapter 逻辑。
2. 自动复用 CLI 对 sidechain、subagent replay、模型回退、pricing、成本模式、时区和多数据源的支持。
3. GUI 的维护重点可以放在 CLI 输出 schema、缓存、错误提示和交互体验上。

但也有风险：

1. CLI JSON 输出字段变化会影响 `CCUsageCliParser`。目前 parser 只覆盖 daily 输出的一部分字段。
2. 如果 GUI 需要 Codex 专属字段，例如 `reasoningOutputTokens` 或 `isFallback`，需要扩展 DTO。
3. 如果 GUI 想展示 agent/source 维度细分，要确认 CLI JSON 是否提供足够 metadata；当前代码注释也指出真实 CLI 不提供 agent 级别成本拆分。
4. GUI 调用的是 `--offline`，pricing 数据依赖本地缓存或内置数据，可能与在线最新 pricing 有差异。

## 8. 建议

1. **短期继续复用 CLI**：不要在 GUI 中直接解析 Claude/Codex 原始日志，除非有强需求。CLI adapter 已经包含大量边界处理。
2. **为 GUI 明确 JSON contract**：整理 GUI 实际依赖的 CLI JSON 字段，添加 snapshot 测试，防止 CLI 升级破坏解析。
3. **扩展 DTO 时优先兼容 unified reports**：Codex focused report 的字段名与 Claude/unified daily 不完全一致，例如 Codex 使用 `reasoningOutputTokens`、`costUSD`、`models` map；GUI 如要支持 focused Codex 视图，应单独建 DTO 或做 schema 分支。
4. **错误提示应暴露数据目录信息**：当 CLI 返回空数据或错误时，GUI 可提示 Claude/Codex 的默认目录和对应环境变量。
5. **保留“本地 token 计数优先”的原则**：不要在 GUI 中根据文本估算 token，否则会与 ccusage 的准确性原则冲突。

## 9. 参考文件

- `ccusage/rust/crates/ccusage/src/adapter/claude/README.md`
- `ccusage/rust/crates/ccusage/src/adapter/claude/paths.rs`
- `ccusage/rust/crates/ccusage/src/adapter/claude/mod.rs`
- `ccusage/rust/crates/ccusage/src/adapter/claude/daily.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/README.md`
- `ccusage/rust/crates/ccusage/src/adapter/codex/paths.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/parser.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/types.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/loader.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/aggregate.rs`
- `ccusage/rust/crates/ccusage/src/adapter/codex/report.rs`
- `ccusage/rust/crates/ccusage/src/types.rs`
- `ccusage/rust/crates/ccusage/src/cost.rs`
- `ccusage/docs/guide/source-support-qa.md`
- `ccusage-gui/src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliRunner.cs`
- `ccusage-gui/src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliParser.cs`
- `ccusage-gui/src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs`
