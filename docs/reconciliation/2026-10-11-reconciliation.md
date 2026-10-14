# 与 ccusage CLI 的对账结论（Q-001，v0.3 exit criteria 证据）

- 日期：2026-10-11
- 任务卡：`docs/kanban/issues/Q-001-reconciliation-tests.md`
- 被测两侧：
  - **自研链路**：JSONL 直读适配器（`ClaudeCodeJsonlAdapter` / `CodexJsonlAdapter`）→ 统一 `UsageRecord` → 自有 `PricingEngine`（内置 models.dev 快照）→ 按 UTC 自然日聚合；
  - **CLI 链**：`npx --yes ccusage@20.0.29 <agent> daily --json --offline --timezone UTC`。
- 测试实现：`tests/TokenPulse.Reconciliation.Tests/`（新项目，已并入 `TokenPulse.sln`）。
- 结论速览：**fixture 对账 tokens 逐项精确一致、cost 全部一致；本机真实数据（Codex，19 文件 / 2931 条记录）tokens 级全等、cost 全期 $101.3856438 两侧一致**。对账暴露并修复了一处内置快照漏收（gpt-5.6-terra，详见差异归类 §5）。

## 1. 对账方法

### 1.1 容差比较器

`Comparison/ReconciliationComparator.cs`：

- 比较键：日期 × 模型（总计行为 Model=null，全期总计用哨兵日期 `(total)`）；
- tokens 五分量（input / output / cache_creation / cache_read / total）**精确一致**，任何不一致按 **解析/覆盖原因** 归类；
- cost 通过条件：**相对误差 ≤ 0.5%** 或 **绝对误差 < $0.01**（小额日期的相对误差噪声不报差异；golden 成本为 0 时只有绝对误差路径）；超出按 **定价原因** 归类；
- 任一侧成本缺失（CLI 的 codex per-model 输出没有成本字段）时跳过成本比较；
- 差异报告（`ReconciliationReport.Render()`）按 日期 × 模型 × 字段 列出 golden 值 / ours 值 / 差量 / 相对偏差，并标注"解析/覆盖差异"或"定价差异"提示；测试失败时报告全文进入断言消息。

### 1.2 两条链路的口径对齐

对账前用受控探针（构造最小 fixture 直接跑 CLI）确认了 ccusage 20.0.29 的行为规则，两侧口径逐项对齐：

| 口径 | ccusage 20.0.29 实测行为 | 自研链路对应 |
|---|---|---|
| 计价优先级 | 日志带 `costUSD` 时直接采用；字段缺失时按内置价格表计算 | `PricingEngine.PriceRecord`：`CostUsd` 非空走 LogCost，否则按快照计价 |
| `costUSD: null`（显式） | **整条记录丢弃**（不进 totals 也不进 breakdown） | 记录保留、按快照计价（蓝本 claude.rs 口径，见 §6 已知分歧 1） |
| `message.id` 缺失 | **整条记录丢弃** | 记录保留（见 §6 已知分歧 2） |
| cache_creation 5m/1h 细分 | 有细分时按 5m=cache_write 价、1h=input×2 计，细分和计入 cacheCreationTokens；无细分时整体按 cache_write 价 | 同规则（`PricingEngine.ComputeCost`，蓝本 aggregation.rs 移植） |
| `-fast` 采集标记 | 保留在模型名里展示、按剥掉 `-fast` 的基价计价 | 同（`PricingModelMatcher` 剥离 `-fast` 候选） |
| `<synthetic>` 记录 | 计入每日总计、无 per-model breakdown、成本 0 | Model 归一为 null，计入总计、不入 per-model 条目、Missing→$0 |
| 重复消息 | 消息级去重（fixture 验证：同 id+requestId 重复行只计一次） | `ClaudeUsageRecordDeduper` |
| Codex 累计快照 | 每 turn 连发的重复 cumulative 快照不重复记账（cumulative 守卫） | 同守卫（`CodexJsonlFileParser`） |
| Codex inputTokens | 全量 input 减 cached（cached 计入 cacheReadTokens） | 同（统一记录口径，两侧逐日一致） |
| 日期分桶 | `--timezone UTC`（显式指定；不指定则随本机时区） | 统一记录按 UTC 分桶 |

探针同时验证了计价公式：对 claude-sonnet-4-5（$3/$15/$0.3 write $3.75）、claude-haiku-4-5（$1/$5）、gpt-5.3-codex（$1.75/$14/$0.175）、gpt-5.1-codex-max、gpt-5-codex，CLI `--offline`（LiteLLM 表）与我们的 models.dev 快照**逐分钱一致**。

### 1.3 golden 录制命令

版本 ccusage **20.0.29**，录制于 2026-10-11（详见 `tests/TokenPulse.Reconciliation.Tests/fixtures/golden/README.md`）：

```bash
FX=tests/TokenPulse.Reconciliation.Tests/fixtures

CLAUDE_CONFIG_DIR="$PWD/$FX/claude" \
  npx --yes ccusage@20.0.29 claude daily --json --offline --timezone UTC \
  > $FX/golden/claude-fixture-daily-v20.0.29.json

CODEX_HOME="$PWD/$FX/codex" \
  npx --yes ccusage@20.0.29 codex daily --json --offline --timezone UTC \
  > $FX/golden/codex-fixture-daily-v20.0.29.json
```

说明：使用 per-agent 子命令（`claude daily` / `codex daily`）而非统一 `daily`——统一命令会扫描本机其他 agent 的默认目录，golden 无法跨机器复现；环境变量（`CLAUDE_CONFIG_DIR` / `CODEX_HOME`，实测均支持）把数据钉在仓库 fixture 上；`--timezone UTC` 与自研链路 UTC 分桶一致；`--offline` 与自研"内置快照计价"口径一致。测试运行时**不调用 CLI**，只解析提交进仓库的 golden JSON——离线、可重复。fixture 为合成脱敏数据（无真实 token、路径、用户名）。

fixture 覆盖面：多日 × 多模型（sonnet-4-5 / opus-4 / haiku-4-5 / `-fast` 标记 / gpt-5.3-codex / gpt-5.1-codex-max / gpt-5-codex）、cache_creation 带 5m+1h 细分与无细分两条计价路径、costUSD 内建与缺失两条成本路径、重复消息去重、重复 cumulative 快照守卫、agent progress 包装行、`<synthetic>` 记录、多项目多会话。

## 2. fixture 对账结果（TC-001）

`ClaudeFixtureReconciliationTests` / `CodexFixtureReconciliationTests`：**per-model 与每日/全期总计两个口径全部零差异**（tokens 精确一致，cost 相对误差 0%，仅 CLI JSON 输出携带的 double 尾数如 `0.32380000000000003` 属表示噪声，远小于绝对容差）。

- Claude：2026-09-14 全期 21.68 万 tokens / $0.3238；2026-09-15 1.29 万 tokens / $0.0238；9 条记录（1 条重复行被两侧一致去重）、0 malformed、0 invalid、无未命中定价模型；
- Codex：三日分别 $0.0427 / $0.08975 / $0.011625（gpt-5.3-codex / gpt-5.1-codex-max / gpt-5-codex），重复快照守卫两侧一致跳过。

## 3. 本机真实数据对账结果（TC-002，TOKENPULSE_RECONCILE_REAL=1）

环境：本机无 Claude 日志（`~/.claude/projects` 不存在 → Claude 真实数据测试按设计 Skip）；有真实 Codex 日志（`~/.codex/sessions`，19 文件）。运行方式：

```bash
TOKENPULSE_RECONCILE_REAL=1 dotnet test tests/TokenPulse.Reconciliation.Tests --filter RealDataReconciliationTests
```

CLI 实跑（`npx --yes ccusage@20.0.29 codex daily --json --offline --timezone UTC`）vs 自研全链路（`CodexJsonlAdapter` 默认目录探测 → 快照计价 → UTC 聚合）：

**自研链路诊断**：status=Ok，files=19，records=2931，malformed=0，invalid=0，duplicates=0。

**逐日对账（两侧逐项相等）**：

| 日期 | input | cacheRead | output | total | costUSD | 模型 |
|---|---|---|---|---|---|---|
| 2026-01-27 | 8,103 | 91,904 | 2,509 | 102,516 | 0.065389 | gpt-5.2-codex |
| 2026-01-28 | 11,719 | 118,656 | 2,474 | 132,849 | 0.075909 | gpt-5.2-codex |
| 2026-01-30 | 18,429 | 344,704 | 3,540 | 366,673 | 0.142134 | gpt-5.2-codex |
| 2026-01-31 | 140,198 | 1,429,504 | 67,814 | 1,637,516 | 1.444906 | gpt-5.2-codex |
| 2026-02-01 | 512,633 | 11,701,760 | 63,597 | 12,277,990 | 3.835274 | gpt-5.2-codex |
| 2026-02-04 | 293,120 | 3,302,272 | 58,125 | 3,653,517 | 1.904608 | gpt-5.2-codex |
| 2026-02-07 | 8,172,775 | 261,702,912 | 795,894 | 270,671,581 | 71.242882 | gpt-5.3-codex |
| 2026-02-08 | 2,700,923 | 71,411,840 | 264,604 | 74,377,367 | 20.928143 | gpt-5.3-codex |
| 2026-07-28 | 484,862 | 2,911,488 | 15,125 | 3,411,475 | 1.733522 | gpt-5.6-terra |
| 2026-09-25 | 5,296 | 11,008 | 7 | 16,311 | 0.012878 | gpt-5.6-terra |
| **全期** | **12,348,058** | **353,026,048** | **1,273,689** | **366,647,795** | **101.385644** | 3 模型 |

- tokens：per-model（10 组 日期×模型）与每日/全期总计（11 组）**全部精确一致**；input 12,348,058 / cacheRead 353,026,048 / output 1,273,689 与前期人工验证数字一致；
- cost：修复快照漏收（§5）后全期 **$101.3856438 两侧一致**（CLI JSON 值 101.38564379999998 为 double 表示噪声）。

## 4. 测试结果与回归

- `dotnet build TokenPulse.sln`：0 警告 0 错误；
- `dotnet test TokenPulse.sln`（门控关）：**227 通过 / 5 跳过 / 0 失败**（基线 208 通过 / 3 跳过 + 新增对账项目 18 通过 + 快照回归 1 通过 + 门控跳过 2）；
- `TOKENPULSE_RECONCILE_REAL=1 dotnet test TokenPulse.sln`（门控开）：**228 通过 / 4 跳过 / 0 失败**（Codex 真实数据测试实跑通过；Claude 真实数据测试因本机无 Claude 日志 Skip；另 3 个跳过为既有 Windows-only 用例）；
- 新增测试清单：比较器单测 13（含容差/缺失/报告格式）、fixture 对账 4、真实数据 2（门控）、快照回归 1（`Snapshot_PricesGpt56TerraAtOwnRateNotSolFamily`，位于 `TokenPulse.Core.Tests/Services/PricingEngineTests.cs`）。

## 5. 差异归类与修复

| # | 现象 | 定位 | 归类 | 处置 |
|---|---|---|---|---|
| 1 | 真实数据 2026-07-28 / 2026-09-25 两天 cost 超容差（+96.5% / +99.8%，全期 +1.66%），tokens 完全一致 | 差异模型 `gpt-5.6-terra`；CLI 隐含单价 $2/$12/$0.2（input/output/cacheRead，反推并逐分钱复核），models.dev 上 `openai/gpt-5.6-terra` 同价（2/12/0.2/2.5）；我们内置快照漏收该条目，家族兜底命中 `gpt-5.6`（gpt-5.6-sol，$4/$20）→ 恰好 2× | **定价差异 → 快照数据漏收（真实 bug）** | 修复：`model-pricing-snapshot.json` 补 `gpt-5.6-terra` 条目（2/12/0.2/2.5，match `gpt-5.6-terra`）；新增回归测试断言 terra 按自身价计、不被 sol 家族吞并；修复后真实数据对账全期 cost 与 CLI 逐分钱一致。该模型为本机 `~/.codex/config.toml` 的默认模型，属主流覆盖缺口而非"CLI 与 models.dev 不同步" |
| 2 | fixture 录制期发现 CLI 丢弃部分记录形态 | 见 §6 已知分歧 1/2 的探针结论 | 解析口径分歧（设计使然，非 bug） | 不修；fixture 避开、文档记录（§6） |

I-004 备忘提到的新版 ccusage compaction 去重逻辑：本机 19 个真实文件 0 malformed、token 级全等，说明本数据集未触发 compaction 分歧；后续若遇到 `token_usage_record` / `compacted` 事件形态的数据差异，按比较器输出的"解析/覆盖差异"提示排查。

## 6. 已知分歧（CLI 与自研链路的记录级口径差异，供后续对账排查）

1. **显式 `costUSD: null`**：CLI 整条丢弃；自研保留并按快照计价。真实 Claude 日志中缺失成本时字段通常整体缺省而非显式 null，fixture 亦按真实形态构造；若未来对账出现"我们比 CLI 多计"的解析类差异，优先检查该形态。
2. **`message.id` 缺失的记录**（如部分 `model:"auto"` 快照）：CLI 整条丢弃；自研按蓝本 claude.rs 保留（id null 合法、sessionId 回退文件推导）。本机无真实 Claude 日志，分歧影响面未知，已用既有单测钉住蓝本行为。
3. 其余已知规则（costUSD 优先、5m/1h 倍率、-fast、synthetic、去重、累计快照守卫、UTC 分桶）实测两侧一致，见 §1.2。

## 7. 对产品立场的意义

"任何成本数字必须能与 ccusage 核对"（pivot 文档第 3 节）在本仓库内被机制化：fixture 对账测试离线常驻测试套件，真实数据对账一条命令即可在本机复跑（`TOKENPULSE_RECONCILE_REAL=1`）。v0.3 exit criteria（roadmap）的"与 ccusage 对账一致"在 fixture 与本机真实数据两个层面均以 tokens 精确一致、cost 0.5%/1¢ 容差内一致达成，且对账差异可自动定位到解析或定价原因。
