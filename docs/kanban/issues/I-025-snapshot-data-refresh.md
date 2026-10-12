---
id: I-025
title: 定价快照数据版本刷新
type: chore
priority: P3
size: M
status: done
created: 2026-10-12
updated: 2026-10-12
blocked: none
---

## Goal

把内置定价快照（`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`）与 ccusage 20.0.29 CLI 内嵌 models-dev-pricing.json 的存量价差清零：I-024 已刷新 kimi-k2.6 并补 tiers 段，本卡处理其余被非九源 fixture golden 引用、当时不便随卡改价的条目，并补归一化拼写歧义守卫。

## Background

I-024（2026-10-12 独立审查程序化对比，快照 82 条目 vs `../ccusage/rust/crates/ccusage-core/src/models-dev-pricing.json`）确认的实价差清单（快照 → CLI）：

- anthropic/claude-opus-4：15/75/1.5/18.75 → 13/64/1.3/16
- openai/gpt-4o：2.5/10 → 1.25/5
- openai/gpt-4o-mini：cache_write 0.1875 → 0.15
- openai/gpt-6-astra：cache_write 12.5 → 12
- qwen/qwen3-coder-plus：1/5 → 0.574/2.294
- openai/gpt-5.4：cache_write 3.125 → 2.5（快照旧推导 input×1.25，CLI 显式 input×1.0）
- openai/gpt-5.5：cache_write 6.25 → 5（同上）

这些条目被非 I-024 九源（Qwen / Copilot / Codex 等）的 fixture golden 引用，改价需随那些源重录 golden（重录命令见 `tests/CCUsage.Reconciliation.Tests/fixtures/golden/README.md`）。另登记一处引擎差异：蓝本 `normalized_keys`（ccusage-core pricing.rs）对同一归一化拼写挂两个键时置 None（"names neither entry"），桌面归一化趟无此守卫按 rank 取一——当前快照仅 claude-3-5-sonnet / claude-3-5-haiku 两对碰撞且四条价格逐位相同、原拼写趟必先命中，今日无影响；本卡刷新若拆开同价对，点分候选的回退落点可能二义，需一并补守卫。

> **开工后修正（2026-10-12，见 Development Log）**：上表"CLI 内嵌值"实为 ../ccusage rust 仓库 HEAD 的内嵌价；对账锚点是 npm `ccusage@20.0.29`（golden 录制版本），两者已漂移。经 CLI 探针逐桶实测，现快照对上述 7 条目**已逐位等于** npm 锚点值，无需改价；按原清单改价反而会破坏对账（qwen3-coder-plus 偏差约 50%）。

## Acceptance Criteria

- [x] 上述 7 条价差按 CLI 内嵌值核对清零：经探针逐桶核实（I/O/R/W + tiers），现快照值即 npm 20.0.29 锚点值，无需改价；受影响 golden 无改价源、未重录，qwen 两天成本行反算与 golden 逐位吻合，对账套件通过
- [x] 归一化拼写歧义守卫补齐（同归一化拼写多键 → 归一化趟不命中），蓝本 pricing.rs normalized_keys 语义对齐 + 单测（同价对拆开、跨 provider 碰撞、原拼写/唯一拼写控制组、家族兜底）
- [x] 刷新来源与录制日登记进快照文件头（`_provenance` 段：npm 20.0.29 锚点 + 2026-10-12 探针核实 + rust HEAD 漂移警示）
- [x] 全量测试绿（803 通过 / 22 跳过，跳过均为既有门控）；门控真实对账无回归（CCUSAGE_RECONCILE_REAL=1 实跑 3 项通过）

## Dependencies

- I-024（候选计价接线，快照格式与归一化趟所在处）

## Test Cases

### TC-001: CLI 探针逐桶核价（npm ccusage@20.0.29）

Steps:
1. 造单桶探针 fixture（紧凑 JSON——CLI 解析对带空格 JSON 敏感，带空格行被整体跳过）：
   claude 适配器（CLAUDE_CONFIG_DIR）对 `claude-opus-4-20250514` 与裸名 `claude-opus-4` 分别投
   input 100k / output 100k / cache_creation 100k / cache_read 100k / 1h-only 100k；
   claude 适配器对 gpt-4o / gpt-4o-mini / gpt-5.4 / gpt-5.5 / gpt-6-astra 投 cache_creation 100k（W 桶）；
   codex 适配器（CODEX_HOME）对各 gpt 模型投 A(input 100k, cached 0) / B(input 100k, cached 100k) / C(output 100k)
   及 input 300k（tier 探针）。
2. `npx --yes ccusage@20.0.29 <adapter> daily --json --offline --timezone UTC` 读成本，反推每桶单价。

Expected（实测 → 快照现值）:
- claude-opus-4-20250514 与裸名 claude-opus-4：I 15 / O 75 / W5 18.75 / R 1.5 / W1 30（=I×2）＝ 快照 15/75/1.5/18.75 ✓（rust HEAD 的 13/64/1.3/16 不在锚点内）
- gpt-4o：I 2.5 / O 10 / R 1.25 / W 3.125（=I×1.25 派生）＝ 快照 ✓
- gpt-4o-mini：I 0.15 / O 0.6 / R 0.075 / W 0.1875（派生）＝ 快照 ✓（rust HEAD 显式 0.15 不在锚点内）
- gpt-5.4：I 2.5 / O 15 / R 0.25 / W 3.125（派生）；300k 探针 1.5 → tier input 5 ＝ 快照含 tiers 段 ✓
- gpt-5.5：I 5 / O 30 / R 0.5 / W 6.25（派生）；300k → 3.0 → tier input 10 ✓
- gpt-6-astra：I 10 / O 50 / R 1 / W 12.5（派生）；300k → 6.0 → tier input 20 ✓
- qwen3-coder-plus（golden 反算，两日独立吻合）：input=prompt 映射、I 1 / O 5 / R 0.1（派生）→
  02-23 行 1052.5e-6、02-24 行 1220e-6 与 golden 逐位相等＝ 快照 1/5 + 派生桶 ✓（rust HEAD qwen/ 键 0.574/2.294 不在锚点内）

### TC-002: 归一化歧义守卫单测（PricingModelMatcherTests，7 例）

Steps:
1. `dotnet test tests/CCUsage.Core.Tests` —— 同价对拆开（cla-x-1 / cla.x.1 异价）：原拼写各自可解析、
   `cla@x.1` 归一化碰撞不指向任何条目（守卫前按顺序取 dashed）、provider 限定档同守卫、
   唯一拼写照常命中、跨 provider 碰撞置 None 且原拼写 rank 趟不受影响、守卫落空后家族兜底可达。

Expected:
- 全部断言通过（99 通过 / 0 失败）。

## Development Log

### 2026-10-12

- 开工先核锚点：卡片价差清单出自 I-024 审查，比对对象是 `../ccusage` rust HEAD 的 models-dev-pricing.json；
  对账锚点实为 npm `ccusage@20.0.29`（golden 录制版本）。qwen golden 反算（两日逐位）与 claude/gpt 系
  探针实测证明**两者已漂移**：npm 侧 7 条目现值 = 桌面快照现值（rust HEAD 是 models.dev 更新后的新价，
  未进 npm 20.0.29）。结论：不改价，按原清单改会炸对账（qwen 偏差 ~50%，超容差两个数量级）。
- 探针过程两处坑登记：CLI JSONL 解析对带空格 JSON 敏感（json.dumps 默认分隔符的行被整体跳过，
  须紧凑分隔符）；codex daily 输出成本键为 `costUSD`（claude 为 `totalCost`）。
- 归一化拼写歧义守卫落地 `PricingModelMatcher`：provider 限定档与全局档的归一化趟均要求"恰好一个条目"
  携带该归一化拼写（跨 provider 汇总计数），多键共享即跳过该候选（蓝本 normalized_keys 置 None 同式），
  不再按 rank/顺序取一；原拼写趟与家族兜底不变。守卫对现快照无可观测影响（仅有的两对碰撞价格逐位
  相同且原拼写趟必先命中），为未来拆开同价对消除二义。
- 快照登记 `_provenance` 头（npm 锚点 + 核实日期 + rust HEAD 漂移警示）；解析器对无 models 键的顶层段
  安全跳过，DeepSeekPricingCatalog 只读 deepseek 段，均不受影响。
- 验证：全量 `dotnet test CCUsage.sln` 803 通过 / 22 跳过（跳过均为既有真实数据门控）；
  `CCUSAGE_RECONCILE_REAL=1` 实跑真实对账 3 项通过（Codex / Claude 本机真实数据，快照与守卫改动零回归）。
- Bugs：B-001（P2）I-024 审查价差清单对错锚点——已由本卡探针核实并登记 _provenance，避免后续刷新重蹈。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B-001 | P2 | I-024 审查把 ../ccusage rust HEAD 内嵌价当作 CLI 20.0.29 锚点，两者已漂移（claude-opus-4、gpt-4o 系、qwen3-coder-plus 等 7 条），照单改价将破坏对账 | Resolved | I-025 探针逐桶核实 npm 锚点值 = 快照现值，不改价；_provenance 登记锚点优先级 |

## Verification

- Related files: `src/CCUsage.Core/Services/PricingModelMatcher.cs`（守卫）、
  `src/CCUsage.Core/Pricing/model-pricing-snapshot.json`（_provenance 头）、
  `tests/CCUsage.Core.Tests/Services/PricingModelMatcherTests.cs`（+7 例）
- How to run/verify: `dotnet test CCUsage.sln`；门控 `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests --filter RealData`；
  探针复核法与逐桶期望值见本卡 TC-001（可独立重放）
- Results: 全量 803 通过 / 22 跳过（既有门控）；门控真实对账 3 项实跑通过；golden 文件零改动
