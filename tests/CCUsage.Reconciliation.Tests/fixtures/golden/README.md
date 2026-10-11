# golden 文件（ccusage CLI daily 输出）

本目录存放对账测试（Q-001）使用的 golden 文件：由 ccusage CLI 对仓库内 fixture 录制的
`--json --offline` daily 输出。测试运行时**不再调用 CLI**，只解析这些 JSON 与自研链路比较，
保证离线可跑、可重复。

## 录制信息

- 录制日期：2026-10-11
- CLI 版本：ccusage 20.0.29（`npx --yes ccusage@20.0.29`）
- fixture：本目录上一级的 `claude/`（CLAUDE_CONFIG_DIR 指向）、`codex/`（CODEX_HOME 指向）与
  `copilot/`（COPILOT_HOME 指向，I-008），内容为合成数据（脱敏：无真实 token、路径、用户名），
  覆盖多日 / 多模型 / cache_creation 5m+1h 细分、costUSD 内建与缺失（走计算价）两条路径、
  重复消息去重、cumulative 快照守卫、agent progress 包装行、`<synthetic>` 记录。
  Copilot fixture 覆盖：shutdown 累计快照差分（resume 跨日不重复出账）、同 id 重复 shutdown 去重、
  uncached input 拆分（inputTokens 含缓存读写）、usage_checkpoint 行跳过、全零模型条目跳过、
  -1m 内部后缀剥离归并、session-state 嵌套目录不发现（非递归口径）。

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
```

说明：

- 使用 `claude daily` / `codex daily` 而非统一 `daily`：统一命令会扫描本机其他 agent 的默认目录，
  golden 在不同机器上不可复现；per-agent 命令 + 环境变量钉住 fixture 目录才是确定性的。
- `--timezone UTC`：与测试侧"统一记录按 UTC 分桶"口径一致（CLI 不带该参数时按本机时区分桶，
  golden 会随机器时区漂移）。
- `--offline`：CLI 使用内置 pricing 快照，与自研定价引擎"内置 models.dev 快照"的离线口径对应。
- 重新录制后需同步核对 `docs/reconciliation/` 下对账文档中的数字。
