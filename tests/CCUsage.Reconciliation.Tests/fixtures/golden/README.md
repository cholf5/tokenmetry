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
```

说明：

- 使用 `claude daily` / `codex daily` 而非统一 `daily`：统一命令会扫描本机其他 agent 的默认目录，
  golden 在不同机器上不可复现；per-agent 命令 + 环境变量钉住 fixture 目录才是确定性的。
- `--timezone UTC`：与测试侧"统一记录按 UTC 分桶"口径一致（CLI 不带该参数时按本机时区分桶，
  golden 会随机器时区漂移）。
- `--offline`：CLI 使用内置 pricing 快照，与自研定价引擎"内置 models.dev 快照"的离线口径对应。
- 重新录制后需同步核对 `docs/reconciliation/` 下对账文档中的数字。
