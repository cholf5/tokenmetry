# golden 文件（ccusage CLI daily 输出）

本目录存放对账测试（Q-001）使用的 golden 文件：由 ccusage CLI 对仓库内 fixture 录制的
`--json --offline` daily 输出。测试运行时**不再调用 CLI**，只解析这些 JSON 与自研链路比较，
保证离线可跑、可重复。

## 录制信息

- 录制日期：2026-10-11
- CLI 版本：ccusage 20.0.29（`npx --yes ccusage@20.0.29`）
- fixture：本目录上一级的 `claude/`（CLAUDE_CONFIG_DIR 指向）、`codex/`（CODEX_HOME 指向）、
  `copilot/`（COPILOT_HOME 指向，I-008）与 `opencode/`（OPENCODE_DATA_DIR 指向，I-009，
  内容为合成 SQLite 库 opencode.db：v1 message 表 + v2 session_message 表 + session_v2 fork 边界），
  内容为合成数据（脱敏：无真实 token、路径、用户名），
  覆盖多日 / 多模型 / cache_creation 5m+1h 细分、costUSD 内建与缺失（走计算价）两条路径、
  重复消息去重、cumulative 快照守卫、agent progress 包装行、`<synthetic>` 记录。
  Copilot fixture 覆盖：shutdown 累计快照差分（resume 跨日不重复出账）、同 id 重复 shutdown 去重、
  uncached input 拆分（inputTokens 含缓存读写）、usage_checkpoint 行跳过、全零模型条目跳过、
  -1m 内部后缀剥离归并、session-state 嵌套目录不发现（非递归口径）。
  OpenCode fixture 覆盖：v1/v2 双表扫描、同 message id 跨表去重（v1 先扫）、fork 拷贝行剔除
  （session_v2 through 边界）、user 行跳过、坏 JSON 载荷跳过、stored cost 直采与为 0 走计价两条路径。
  Goose fixture 覆盖（I-012）：accumulated_* 列优先（plain 列为干扰值）、整数秒时间戳与 19 字符空格分隔时间、
  provider 缺失按模型前缀归一、tokens 全零会话行跳过、model_config 非法 JSON 跳过、created_at 不可解析跳过。
  Hermes fixture 覆盖（I-016）：五桶 tokens 映射、REAL 秒（含小数截断）与 REAL 毫秒两种 started_at 刻度、
  billing_provider 缺失按模型前缀推断、actual_cost_usd 直采、全零会话行跳过、model 空白行 SQL 排除
  （CLI 20.0.29 只认 actual cost——蓝本 HEAD 另回退 estimated，fixture 不含 estimated-only 行，见卡片 Dev Log）。
  Kilo fixture 覆盖（I-017）：assistant 行过滤、五桶 tokens 映射、载荷 cost 直采（含 0——Auto 对 Some(0.0)
  记 0 不再计价）、stored cost LogCost、非 assistant 行跳过、坏 JSON 载荷跳过。
  Gemini fixture 覆盖（I-010）：.json/.jsonl 双格式、messages 数组路径（user 行跳过、cached 按 total 自洽性
  拆分、tool 并入 input）、JSONL sessionId/model 跨行滚动与同 id 事件就地替换、非 gemini 行的 stats
  （models 明细 overlap 扣减）、无 models 的整块 stats 回退（模型缺省 unknown，CLI missingPricing 双侧 0 成本）。
  Amp fixture 覆盖（I-014）：usageLedger.events 优先（toMessageId 缓存四桶 attach、total 缺口补 output、
  缺 model 事件跳过）、events 存在但不回退 messages、messages 回退路径（usage 级字段缺省回退消息级、
  totalTokens 兜底）、user 行跳过。
  Droid fixture 覆盖（I-015）：settings.model 归一化（剥 [Anthropic] 段）、侧车 .jsonl "Model:" 提取回退、
  archive 旧快照 latest-wins 去重、全零 tokenUsage 跳过、非 settings 的 .json 不发现。
  Qwen fixture 覆盖（I-011）：projects/&lt;p&gt;/chats/&lt;f&gt;.jsonl 三段布局过滤（transcripts/ 与 chats/deep/
  不发现）、user 行与无 usageMetadata 标记行预过滤、坏 JSON 行、usageMetadata null / 非对象 / 全零拒绝、
  totalTokenCount 恰等四桶和（no-op 兜底边界）、output 为 0 时 total 缺口补 output、同内容键跨文件
  first-wins 去重、双项目双日。
  Kimi fixture 覆盖（I-013）：wire.jsonl 新旧双格式（3/5 段布局发现，4 段与 non-wire 不发现）、
  旧格式 StatusUpdate token_usage 五桶（cache_creation 计价、cache_write 按 input×1.25 推导与 CLI
  同式）、total 兜底 output 补齐、同 message_id 同刻 first-wins 去重、config.json 模型显示名、
  新格式 usage.record 仅取 turn 级（session 级与全零拒绝）、kimi-code/ 前缀剥离、秒级时间戳毫秒截断。
  Codebuff fixture 覆盖（I-018）：projects/**/chat-messages.json 多项目多 chat 发现、metadata.usage
  四桶 + 消息级 credits、同 id 同文件重复 last-wins 去重（蓝本 HashMap insert 后到覆盖）、
  metadata.codebuff.usage 零值字段兜底、runState.messageHistory 倒序回退（prompt_tokens /
  completion_tokens / prompt_tokens_details.cached_tokens 别名）、total 兜底 output 补齐、
  无消息时间戳回退 chat 目录名时间戳、缺省模型 codebuff-unknown（CLI missingPricing 双侧 0 成本）、
  坏 JSON 与顶层级对象不计（Malformed 显式计数）。
  pi fixture 覆盖（I-019）：重放抑制（子会话相对 parentSession 指向同批父文件，前缀签名逐位匹配
  后跳过）、subagent-artifacts 派生转录剔除与 run-* 新上下文子会话保留、会话内建 cost.total 直采
  与负成本重算（CLI auto 口径，"[pi] gpt-5" 剥前缀命中 openai/gpt-5）、无内建成本按快照现算
  （含 cacheWrite 的 claude-sonnet 行两侧同价）、坏行预过滤跳过显式计数、run-*/ 会话文件名推导。
  Grok fixture 覆盖（I-022）：turn_completed 过滤（tool_call 与缺 usage 行跳过）、modelUsage 逐模型
  拆行（Ordinal 排序）与顶层 usage+summary current_model_id 回退（无 summary 回退 "unknown"）、
  inputTokens 缓存扣除与 cache_creation 子集拆分、costUsdTicks 发票成本 LogCost 直采（1 tick =
  1e-10 USD）、agentTimestampMs 毫秒优先与 envelope 秒回退、无 eventId 内容键文件内去重、同 eventId
  跨会话全局去重（eventId|模型 first-wins）、summary 缺失时会话目录名 id 与 URL 解码项目路径、
  损坏文件坏行预过滤跳过。
  OpenClaw fixture 覆盖（I-020）：双格式（sessions/**&#47;*.jsonl 直读 + agents/*/agent/openclaw-agent.sqlite
  transcript_events）、model_change（根级 modelId/provider）与 model-snapshot（customType + data 块）
  状态跟踪、usage.cost.total 内建成本 LogCost 直采、totalTokens 兜底（output 为 0 补 output 分支）、
  cacheWrite → cache_creation、归档会话文件名（.jsonl.deleted.* / .jsonl.reset.*）与会话 id 推导
  （首段 ".jsonl" 前缀）、文件内重复行内容键去重、非 message / 非 assistant / 全零行跳过、损坏
  message 块行与坏 JSON 载荷行跳过、**同一事件的迁移 SQLite 副本优先于 JSONL 遗留副本**（同会话/
  时间戳/模型/用量身份匹配替换，成本 0.50 → 0.51）、SQLite-only 会话（会话 id 取列值、模型取
  事件状态）、not-a-database.sqlite 不发现、无内建成本未定价模型行（CLI missingPricing 双侧 0 成本）。

## 录制命令（在仓库根目录执行）

```bash
FX=tests/CCUsage.Reconciliation.Tests/fixtures

CLAUDE_CONFIG_DIR="$PWD/$FX/claude" \
  npx --yes ccusage@20.0.29 claude daily --json --offline --timezone UTC \
  > $FX/golden/claude-fixture-daily-v20.0.29.json

CODEX_HOME="$PWD/$FX/codex" \
  npx --yes ccusage@20.0.29 codex daily --json --offline --timezone UTC \
  > $FX/golden/codex-fixture-daily-v20.0.29.json

COPILOT_HOME="$PWD/$FX/copilot" \
  npx --yes ccusage@20.0.29 copilot daily --json --offline --timezone UTC \
  > $FX/golden/copilot-fixture-daily-v20.0.29.json

OPENCODE_DATA_DIR="$PWD/$FX/opencode" \
  npx --yes ccusage@20.0.29 opencode daily --json --offline --timezone UTC \
  > $FX/golden/opencode-fixture-daily-v20.0.29.json

GOOSE_PATH_ROOT="$PWD/$FX/goose" \
  npx --yes ccusage@20.0.29 goose daily --json --offline --timezone UTC \
  > $FX/golden/goose-fixture-daily-v20.0.29.json

HERMES_HOME="$PWD/$FX/hermes" \
  npx --yes ccusage@20.0.29 hermes daily --json --offline --timezone UTC \
  > $FX/golden/hermes-fixture-daily-v20.0.29.json

KILO_DATA_DIR="$PWD/$FX/kilo" \
  npx --yes ccusage@20.0.29 kilo daily --json --offline --timezone UTC \
  > $FX/golden/kilo-fixture-daily-v20.0.29.json

GEMINI_DATA_DIR="$PWD/$FX/gemini" \
  npx --yes ccusage@20.0.29 gemini daily --json --offline --timezone UTC \
  > $FX/golden/gemini-fixture-daily-v20.0.29.json

AMP_DATA_DIR="$PWD/$FX/amp" \
  npx --yes ccusage@20.0.29 amp daily --json --offline --timezone UTC \
  > $FX/golden/amp-fixture-daily-v20.0.29.json

DROID_SESSIONS_DIR="$PWD/$FX/droid" \
  npx --yes ccusage@20.0.29 droid daily --json --offline --timezone UTC \
  > $FX/golden/droid-fixture-daily-v20.0.29.json

QWEN_DATA_DIR="$PWD/$FX/qwen" \
  npx --yes ccusage@20.0.29 qwen daily --json --offline --timezone UTC \
  > $FX/golden/qwen-fixture-daily-v20.0.29.json

KIMI_DATA_DIR="$PWD/$FX/kimi" \
  npx --yes ccusage@20.0.29 kimi daily --json --offline --timezone UTC \
  > $FX/golden/kimi-fixture-daily-v20.0.29.json

CODEBUFF_DATA_DIR="$PWD/$FX/codebuff" \
  npx --yes ccusage@20.0.29 codebuff daily --json --offline --timezone UTC \
  > $FX/golden/codebuff-fixture-daily-v20.0.29.json

PI_AGENT_DIR="$PWD/$FX/pi/sessions" \
  npx --yes ccusage@20.0.29 pi daily --json --offline --timezone UTC \
  > $FX/golden/pi-fixture-daily-v20.0.29.json

GROK_HOME="$PWD/$FX/grok" \
  npx --yes ccusage@20.0.29 grok daily --json --offline --timezone UTC \
  > $FX/golden/grok-fixture-daily-v20.0.29.json

OPENCLAW_DIR="$PWD/$FX/openclaw" \
  npx --yes ccusage@20.0.29 openclaw daily --json --offline --timezone UTC \
  > $FX/golden/openclaw-fixture-daily-v20.0.29.json
```

说明：

- 使用 `claude daily` / `codex daily` 而非统一 `daily`：统一命令会扫描本机其他 agent 的默认目录，
  golden 在不同机器上不可复现；per-agent 命令 + 环境变量钉住 fixture 目录才是确定性的。
- `--timezone UTC`：与测试侧"统一记录按 UTC 分桶"口径一致（CLI 不带该参数时按本机时区分桶，
  golden 会随机器时区漂移）。
- `--offline`：CLI 使用内置 pricing 快照，与自研定价引擎"内置 models.dev 快照"的离线口径对应。
- 重新录制后需同步核对 `docs/reconciliation/` 下对账文档中的数字。
