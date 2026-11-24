# 与 ccusage CLI 的对账结论（Q-001，v0.3 exit criteria 证据）

- 日期：2026-10-11
- 任务卡：`docs/kanban/issues/Q-001-reconciliation-tests.md`
- 被测两侧：
  - **自研链路**：JSONL 直读适配器（`ClaudeCodeJsonlAdapter` / `CodexJsonlAdapter`）→ 统一 `UsageRecord` → 自有 `PricingEngine`（内置 models.dev 快照）→ 按 UTC 自然日聚合；
  - **CLI 链**：`npx --yes ccusage@20.0.29 <agent> daily --json --offline --timezone UTC`。
- 测试实现：`tests/Tokenmetry.Reconciliation.Tests/`（新项目，已并入 `Tokenmetry.sln`）。
- 操作入口：分流、命令、差异判读与验收清单见 `.agents/skills/ccusage-reconciliation/SKILL.md`；本文保留口径、证据与分歧细节。

## 0. 蓝本同步账（adapter 算法对齐的版本锚，下次同步先看这里）

蓝本仓库 `../ccusage`（adapter 蓝图在 `rust/adapters/`，核心在 `rust/crates/ccusage-core/`）。每次同步在此追加一行：**版本 + 本地 HEAD commit**，下次同步 `git -C ../ccusage log <commit>..HEAD` 即得增量。注意 golden fixtures 仍钉 20.0.29（§1.3），与这里的算法锚分开看。

- **2026-10-30 → 20.0.45**（release commit `154e13bb`）：fast/flex 倍率表照抄、Claude 消息级去重键锚 20.0.45、Kimi k2.5/k2.6 候选、zstd rollout 读取（§6 第 6/7/8 条；11-01 的去重索引增量化不改语义）。
- **2026-11-09 → 20.0.48**（本地 HEAD `0c1bdd19`）：ultrafast 档位（#1989）、OpenCode claude-agent 跨源去重（#2007）（§6 第 6/9 条）。
- **2026-11-24 → 20.1.0 / 20.1.1**（PR #2341 / commit `45ab8193`）：Claude 代理网关「一次响应按 content block 写多行」的响应链去重（§8）。golden fixture 仍钉 20.0.29（该 fixture 全带 requestId、无此形态，两版本输出 tokens 实测一致）；Claude **真实数据**对账改用 20.1.1。
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
| `-fast` 采集标记 | 展示名保留 `-fast`；计价按剥掉后缀的基价，**再乘 fast 倍率**（`fast-multiplier-overrides.json`：claude-opus-4-6/4-7 = 6×、4-8 = 2×、gpt-5.x/6.x = 2.0–2.5×） | 同（`PricingModelMatcher` 剥 `-fast` 找基价 + `SpeedMultiplierTable` 乘倍率）；flex 档 = 0.5× |
| `<synthetic>` 记录 | 计入每日总计、无 per-model breakdown、成本 0 | Model 归一为 null，计入总计、不入 per-model 条目、Missing→$0 |
| 重复消息 | 消息级去重（fixture 验证：同 id+requestId 重复行只计一次） | `ClaudeUsageRecordDeduper`（20.1.x 起另加转录响应链，代理网关多 block 响应见 §8） |
| Codex 累计快照 | 每 turn 连发的重复 cumulative 快照不重复记账（cumulative 守卫） | 同守卫（`CodexJsonlFileParser`） |
| Codex inputTokens | 全量 input 减 cached（cached 计入 cacheReadTokens） | 同（统一记录口径，两侧逐日一致） |
| 日期分桶 | `--timezone UTC`（显式指定；不指定则随本机时区） | 统一记录按 UTC 分桶 |

探针同时验证了计价公式：对 claude-sonnet-4-5（$3/$15/$0.3 write $3.75）、claude-haiku-4-5（$1/$5）、gpt-5.3-codex（$1.75/$14/$0.175）、gpt-5.1-codex-max、gpt-5-codex，CLI `--offline`（LiteLLM 表）与我们的 models.dev 快照**逐分钱一致**。

### 1.3 golden 录制

版本 ccusage **20.0.29**（2026-10-11 录制）；Claude **真实数据**对账例外用 **20.1.1**（20.1.0 #2341 的代理网关多 block 口径，见 §8）。逐 agent 录制命令、环境变量与 fixture 覆盖说明的唯一出处在 `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`，此处不再重复。

固定口径：per-agent 子命令 + 环境变量钉住 fixture 目录 + `--timezone UTC` + `--offline`；fixture 为合成脱敏数据。测试运行时**不调用 CLI**，只解析提交进仓库的 golden JSON（门控的真实数据测试除外）。

Claude/Codex fixture 覆盖面：多日 × 多模型（sonnet-4-5 / opus-4 / haiku-4-5 / `-fast` 标记 / gpt-5.3-codex / gpt-5.1-codex-max / gpt-5-codex）、cache_creation 带 5m+1h 细分与无细分两条计价路径、costUSD 内建与缺失两条成本路径、重复消息去重、重复 cumulative 快照守卫、agent progress 包装行、`<synthetic>` 记录、多项目多会话。

## 2. fixture 对账结果（TC-001）

`ClaudeFixtureReconciliationTests` / `CodexFixtureReconciliationTests`：**per-model 与每日/全期总计两个口径全部零差异**（tokens 精确一致，cost 相对误差 0%，仅 CLI JSON 输出携带的 double 尾数如 `0.32380000000000003` 属表示噪声，远小于绝对容差）。

- Claude：2026-09-14 全期 21.68 万 tokens / $0.3238；2026-09-15 1.29 万 tokens / $0.0238；9 条记录（1 条重复行被两侧一致去重）、0 malformed、0 invalid、无未命中定价模型；
- Codex：三日分别 $0.0427 / $0.08975 / $0.011625（gpt-5.3-codex / gpt-5.1-codex-max / gpt-5-codex），重复快照守卫两侧一致跳过。

## 3. 本机真实数据对账结果（TC-002，TOKENMETRY_RECONCILE_REAL=1）

环境：本机无 Claude 日志（`~/.claude/projects` 不存在 → Claude 真实数据测试按设计 Skip）；有真实 Codex 日志（`~/.codex/sessions`，19 文件）。运行方式：

```bash
TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter RealDataReconciliationTests
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

- `dotnet build Tokenmetry.sln`：0 警告 0 错误；
- `dotnet test Tokenmetry.sln`（门控关）：**227 通过 / 5 跳过 / 0 失败**（基线 208 通过 / 3 跳过 + 新增对账项目 18 通过 + 快照回归 1 通过 + 门控跳过 2）；
- `TOKENMETRY_RECONCILE_REAL=1 dotnet test Tokenmetry.sln`（门控开）：**228 通过 / 4 跳过 / 0 失败**（Codex 真实数据测试实跑通过；Claude 真实数据测试因本机无 Claude 日志 Skip；另 3 个跳过为既有 Windows-only 用例）；
- 新增测试清单：比较器单测 13（含容差/缺失/报告格式）、fixture 对账 4、真实数据 2（门控）、快照回归 1（`Snapshot_PricesGpt56TerraAtOwnRateNotSolFamily`，位于 `Tokenmetry.Core.Tests/Services/PricingEngineTests.cs`）。

## 5. 差异归类与修复

| # | 现象 | 定位 | 归类 | 处置 |
|---|---|---|---|---|
| 1 | 真实数据 2026-07-28 / 2026-09-25 两天 cost 超容差（+96.5% / +99.8%，全期 +1.66%），tokens 完全一致 | 差异模型 `gpt-5.6-terra`；CLI 隐含单价 $2/$12/$0.2（input/output/cacheRead，反推并逐分钱复核），models.dev 上 `openai/gpt-5.6-terra` 同价（2/12/0.2/2.5）；我们内置快照漏收该条目，家族兜底命中 `gpt-5.6`（gpt-5.6-sol，$4/$20）→ 恰好 2× | **定价差异 → 快照数据漏收（真实 bug）** | 修复：`model-pricing-snapshot.json` 补 `gpt-5.6-terra` 条目（2/12/0.2/2.5，match `gpt-5.6-terra`）；新增回归测试断言 terra 按自身价计、不被 sol 家族吞并；修复后真实数据对账全期 cost 与 CLI 逐分钱一致。该模型为本机 `~/.codex/config.toml` 的默认模型，属主流覆盖缺口而非"CLI 与 models.dev 不同步" |
| 2 | fixture 录制期发现 CLI 丢弃部分记录形态 | 见 §6 已知分歧 1/2 的探针结论 | 解析口径分歧（设计使然，非 bug） | 不修；fixture 避开、文档记录（§6） |

I-004 备忘提到的新版 ccusage compaction 去重逻辑：本机 19 个真实文件 0 malformed、token 级全等，说明本数据集未触发 compaction 分歧；后续若遇到 `token_usage_record` / `compacted` 事件形态的数据差异，按比较器输出的"解析/覆盖差异"提示排查。

## 6. 已知分歧（CLI 与自研链路的记录级口径差异，供后续对账排查）

1. **显式 `costUSD: null`**：CLI 整条丢弃；自研保留并按快照计价。真实 Claude 日志中缺失成本时字段通常整体缺省而非显式 null，fixture 亦按真实形态构造；若未来对账出现"我们比 CLI 多计"的解析类差异，优先检查该形态。
2. **`message.id` 缺失的记录**（如部分 `model:"auto"` 快照）：CLI 整条丢弃；自研按蓝本 claude.rs 保留（id null 合法、sessionId 回退文件推导）。本机无真实 Claude 日志，分歧影响面未知，已用既有单测钉住蓝本行为。
3. 其余已知规则（costUSD 优先、5m/1h 倍率、synthetic、去重、累计快照守卫、UTC 分桶）实测两侧一致，见 §1.2。`-fast` 的计价口径见下条 6：当年 fixture 用的 `claude-sonnet-4-5` 倍率恰为 1.0，故旧结论误记为"两侧一致"。
4. **ZCode 非 Z.ai provider 行的成本**（2026-10-18 起）：CLI 对自定义 provider 行计 0（missingPricing / totalCost 0 口径）；自研改为 `CostUsd = null` 交三级计价引擎（用户条目 → models.dev → 内置快照），可解析时计入成本、不可解析时如实未定价。这是为可用性做的**有意分歧**（旧口径的哨兵 0 会经 LogCost 短路压掉用户的全部目录配置），定案见 `docs/plans/2026-10-18-zcode-custom-provider-pricing-design.md`。对账时 ZCode 自定义 provider 部分的 cost 差异属预期，不算解析回归。
5. **第三方渠道按官方价计价**（2026-10-23 起，P-001）：CLI 沿用 models.dev 全量价目（含第三方 provider 与 coding-plan 全零条目），同名多条目时按店面前缀 / rank / 顺序择一；自研的远程层先经官方目录投影（白名单厂商 + 家族归属 + 海外店优先 + 归一键去重），**只承认模型厂商的官方价**，带第三方前缀的名字（如 `openrouter/z-ai/glm-5.3-flash`）剥前缀后按官方价计。这是 Opinionated 立场的**有意分歧**（"追第三方价 = 永远跟边界情况斗争"，2026-10-21 实锤：`scnet-token-plan/GLM-5.3-Flash` 全零条目在真实数据上压过 20+ 有价条目，39.9 亿 tokens 计 $0；golden 审计 2026-10-23：全部 golden 仅含官方模型名，Reconciliation.Tests 不受影响）。对账时第三方渠道部分的 cost 差异（CLI 低估 / 自研按官方价）属预期，不算解析回归；用户要按渠道价计走用户条目（可带 Provider 前缀作用域）。定案见 `docs/plans/2026-10-23-official-pricing-catalog-design.md`。
6. **服务档位倍率**（2026-10-30 起；2026-11-09 随 ccusage 20.0.48 增补 ultrafast）：ccusage 的倍率数据来自 LiteLLM `provider_specific_entry.fast|flex` + overrides 文件；本仓库只认官方价、不接 LiteLLM，故内置一份静态表 `Pricing/speed-multipliers.json`（现锚 20.0.48 的 fast/flex/ultrafast 三个 overrides），且 Codex 档位只认日志里的 `thread_settings_applied`，不读 `config.toml` 的 `service_tier`（老日志因此按 standard 计）。ultrafast（20.0.46 起 #1989）：OpenAI 只为 gpt-6-astra / gpt-6.1-sol 发布 ultrafast 价（恰 6× Standard，exact-only 无 prefix），未发布 ultrafast 价的模型按 Standard 计。对账时若出现"只有 fast/flex/ultrafast 行成本不同"，先看是不是这两种差异。
7. **Kimi `kimi-for-coding` 定价**（2026-10-30 起）：按时间戳切成 `moonshot/kimi-k2.5`（< 1776698890072 ms）或 `moonshot/kimi-k2.6`，与蓝本 `model_candidates` 一致。对账 fixture 不含该模型名，差异只会出现在真实数据上。
8. **Codex 压缩 rollout**（2026-10-30 起）：`.jsonl.zst` 与明文同存时明文优先、跨目录按解压后路径去重；**fork/子代理回放抑制与 compaction 去重仍未移植**（独立计划），因此子代理会话继承的父历史当前会被重复计。
9. **OpenCode claude-agent 跨源去重**（2026-11-09 起，随 ccusage 20.0.48 #2007）：OpenCode 的 claude-agent provider 内跑 Claude Code，Claude 会另写转录，统一入账双计。本仓库在采集管道仲裁丢弃 OpenCode claude-agent 行，当且仅当其原生会话（`content[].native.sessionID`）在**同一 UTC 日**被 Claude 源的主线程记录计了账；Claude 源缺席 / 零记录 / 只有 sidechain 用量 / 日期不同 → 保留。与蓝本的口径差异：蓝本按 Claude 源时区逐日匹配（daily 族），本仓固定 UTC；午夜跨界 turn 双计属两边共有的文档化限制。已知残留：升级前已双计入库的 OpenCode facts 行不会自动清退（facts 是 upsert、无"消失桶"清理语义，与日志删除后的残留同性质）。
10. **DSH 蓝本目录外样本的成本**（A-0b，2026-11-23 起；DSH 于 2026-10-11 接入）：dsh-usage-stats 蓝本的 `estimateTokenCost` 只认其内置 DeepSeek 目录（`deepseek-v4-flash/pro` 的 flat + 时段规则），目录外路由（本机 `ark/glm-5.3-flash` 等）fail-closed 记 0；自研按 A-0b「未知成本纪律」把非 deepseek 路由记 `CostUsd = null`，交给采集侧定价引擎（快照 + models.dev + 用户覆盖）计价，仍缺价才进 `UnpricedModels` fail-loud。对账口径因此是 **tokens 精确一致 + 自研成本 ≥ 蓝本可计价合计**：蓝本全部样本可计价时仍要求两侧成本一致；存在目录外样本时，自研超出部分只允许来自这些样本。真实数据用例按此断言，不再要求"两侧同为 0"。

## 7. 对产品立场的意义

"任何成本数字必须能与 ccusage 核对"（pivot 文档第 3 节）在本仓库内被机制化：fixture 对账测试离线常驻测试套件，真实数据对账一条命令即可在本机复跑（`TOKENMETRY_RECONCILE_REAL=1`）。v0.3 exit criteria（roadmap）的"与 ccusage 对账一致"在 fixture 与本机真实数据两个层面均以 tokens 精确一致、cost 0.5%/1¢ 容差内一致达成，且对账差异可自动定位到解析或定价原因。

## 8. 2026-11-24 增量：Claude 代理网关多 block 响应的重复记账（ccusage 20.1.0 #2341）

### 8.1 现象

本机 2026-11-24（UTC 日）Tokenmetry 显示 18.3 亿 tokens，其中 Claude Code 17.82 亿；同一时刻
全局 `ccusage`（20.1.1）`--since 20261124 --offline --json` 为 8.76 亿，约 2 倍。

### 8.2 根因

用户经代理网关调用 `deepseek-*` 模型，日志形态与 Anthropic 直连不同：

- 一次 API 响应被 Claude Code 按 **content block**（thinking / text / tool_use…）写成多行 assistant 记录，
  共享同一 `message.id` 与同一份 `usage`，但每行 `uuid`、`parentUuid`、时间戳都不同；
- `message.id` 是网关生成的 UUID，不带 `msg_` / `resp_` 前缀；`requestId` 为 null。

旧去重口径（20.0.45，§1.2）在无 requestId 且 id 非 `msg_`/`resp_` 前缀时，把时间戳并进键：
`(messageId, sessionId, 时间戳毫秒)`。同一响应的多个 block 时间戳不同 → 每个 block 各记一次；
实测单会话 2398 条 usage 行只对应 1110 个 `api-request`，过账约 2.1 倍，整天约 2.3 倍。

`ccusage` 20.1.0 已修（PR #2341 "Count proxied multi-block responses once regardless of message
ID shape"）：

- 无 requestId 的基础身份仍是 `(messageId, sessionId, 时间戳)`——网关复用占位 id 的两次响应仍靠时间戳区分；
- 新增**转录响应链**证据：沿 `uuid` → `parentUuid` 回溯；user 的 tool_result 行只有在其引用的
  全部 `tool_use` 都属于同一响应时才是链的一环（普通用户消息/混合块行不建链，链在轮次边界断开）；
  usage 行能回溯到已有响应、且签名（`message.id` + input + cache_creation 总量 + cache_read）一致时
  并入该响应，否则开启新响应；
- message id 的拼写（`msg_` / `resp_`）不再参与判定。

### 8.3 对齐实现

- 新增 `ClaudeResponseChainTracker`（`src/Tokenmetry.Infrastructure/ClaudeCodeJsonl/`）：按转录文件
  逐行维护链状态，给 `UsageRecord.ResponseChainIndex` 赋值（拓扑按 `sessionId × id` 记账）；
- `ClaudeUsageRecordDeduper` 增响应链键 `(messageId, sessionId, sourceFile, chainIndex)`，
  仅在无 requestId 时生效；有 requestId 的 `(messageId, requestId)` 与 sidechain 回放口径不变；
- 旧 fixture golden 保持 20.0.29 录制：金标数据全带 requestId、无该形态，实测 20.0.29 与 20.1.1
  对 golden 输出 tokens 完全一致。**Claude 真实数据对账**改用 20.1.1
  （`RealReconciliationEnvironment.ClaudeRealDataCcusageVersion`），其余 agent 仍锚 20.0.29。
- 新增离线 golden：`fixtures/claude-proxy`（多 block 代理响应 + 被用户消息隔开的同 id 第二轮）
  与 `golden/claude-proxy-fixture-daily-v20.1.1.json`（20.1.1 录制），由
  `ClaudeProxyFixtureReconciliationTests` 固化——前者折叠一次、后者各计一次，tokens/cost 双精确。

### 8.4 验证

- 新单测/适配器用例：多 block 代理响应折叠、普通用户消息断链保留两次真实请求、
  tool_result 归属不符断链、链键按会话/转录文件隔离、exact 与 response 键指向不同槽位时合并；
- 真机对账（`TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests
  --filter Real_claude_data_reconciles_with_cli`）：全期 tokens 逐项精确一致
  （input 23,792,324 / output 7,667,554 / cache_read 1,922,562,944 / total 1,954,022,822）；
  cost 差异只来自内置快照未收录 `deepseek-*`（既有定价缺口，与本次解析无关）；
- 说明：当日数值随会话写入持续增长，跨分钟比较会出现快照差；对账须取同一时刻两侧结果。

## 9. 2026-11-25 增量：Host 维度（宿主归属）不改变 tokens/cost 口径

- `usage_facts` 主键新增 `host`（迁移 5）：`host` 只回答"这次执行由谁发起"（Cindy / 空 = 命令行），
  **Agent 仍是执行引擎**（Claude Code / Codex / Cindy-Pi）。tokens / cost 的解析、计价、UTC 分桶、
  去重口径全部不变，ccusage 对账结论继续有效——host 只是分组键，同一天同一 agent 的多条 host
  行合计等于迁移前的单行。
- 迁移把历史行原样保留为 `host=''`（语义诚实：旧行没有归属证据）；采集管道对"本轮实际扫到的
  agent × 天"做权威重算（`ReconcileUsageFactsAsync`），把覆盖到的旧行替换成带 host 的新行。
  因此升级后短期内可能同时看到 `host=''` 与 `host='Cindy'` 的同日行——合计不变，属预期中间态。
- 同步面：relay 与本机客户端需一起升级（行身份新增维度）。首次同步推整份快照，relay 清该设备旧行
  并递增 `devices.data_epoch`，其他客户端看到 epoch 变化后清掉该设备的旧副本，避免 host 幽灵行。

## 10. 2026-11-25 增量：真实数据对账用例对齐 A-0b 与 §6 分歧

本机（`TOKENMETRY_RECONCILE_REAL=1`）复跑：fixture 回归 50 通过 / 19 跳过 / 0 失败；
真实数据 19 个用例 4 通过 / 14 跳过（无数据）/ 1 失败（OpenCode，设计内 fail-loud）。
失败曾出现在 ZCode 与 DSH，逐项判定均为**已文档化的有意分歧，不是解析/聚合回归**：

| 源 | tokens | cost 差异 | 判定 |
|---|---|---|---|
| ZCode | 50 组 日期×模型 + 40 组每日/全期总计精确一致 | 50 行 CLI=0、自研计价（GLM-5.3 / GLM-5.3-Flash，合计 $241.85） | §6-4 / §6-5 有意分歧 |
| DSH | 11/10/3 组全部精确一致；全期 input 22,012,649 / output 4,008,299 / cacheRead 843,451,328 / total 869,472,276 | 蓝本可计价 $0（pricedSamples=0，incompleteSamples=5,811），自研 $26.94 | §6-10 有意分歧（A-0b 引擎计价） |
| OpenCode | output 871,432 vs 273,355（+598,077 = reasoning） | $3.13 vs $0 | 用例注明的 fail-loud（reasoning 并入 output） |

两处用例的期望值写于新口径落地之前，已改为能表达分歧的形态：

- `Real_zcode_data_reconciles_with_cli`：tokens 全量精确（含每日/全期总计）；成本只对 CLI 有价行
  （cost&gt;0）做容差比较，CLI 记 0 的行只汇总不判失败（§6-4/§6-5）。
- `Real_dsh_data_reconciles_with_blueprint`：tokens 四粒度仍精确一致；成本改为——蓝本全部样本可计价
  时两侧必须一致，存在目录外样本时只要求自研合计 ≥ 蓝本可计价合计，且自研 0 成本记录 ≤ 蓝本
  incompleteSamples。同时修正原断言用 `Sum(TotalEntries)`（日总计 + 全期总计重复累加，显示 2×）
  取成本的问题，改用全期总计行。
- OpenCode 维持 fail-loud 不变（reasoning 并入 output 是记录级口径差异，需人工判读）。
