---
name: ccusage-reconciliation
description: 指导 Tokenmetry 与 ccusage 的对账：fixture/真实数据链路、golden 录制、差异判读与蓝本版本同步。适用于改解析、计价、聚合，新增数据源，或升级 ccusage。
---

# ccusage 对账

信任锚：任何 token/成本数字都必须能与 ccusage 对账（tokens 精确，cost ≤0.5% 或 <$0.01）。
本 skill 只负责入口、分流与验收；口径细节以这些文件为准，不要在本文件另立一套：

- `docs/reconciliation/2026-10-11-reconciliation.md`：§0 蓝本同步账、§1 方法与容差、§6 已知分歧、§8 代理网关多 block、§9 Host 维度
- `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`：逐 agent 录制命令与 fixture 覆盖
- `tests/Tokenmetry.Reconciliation.Tests/Support/RealReconciliationEnvironment.cs`：版本锚、环境门控、数据探测
- `tests/Tokenmetry.Reconciliation.Tests/Comparison/ReconciliationComparator.cs`：容差与差异归类

## 先分流

| 场景 | 入口 |
|---|---|
| 改了解析/计价/聚合，跑回归 | `dotnet test tests/Tokenmetry.Reconciliation.Tests` |
| 单源 fixture | 加 `--filter "FullyQualifiedName~ClaudeFixtureReconciliationTests"` |
| 本机真实数据 | 见「真实数据对账」 |
| 新增 agent 数据源 | 见「新增数据源」 |
| ccusage 版本升级 | 见「蓝本版本同步」 |

fixture 测试只解析仓库内 golden，离线确定性；只有门控的真实数据测试会调用 CLI。

## 真实数据对账

```bash
# bash/zsh
TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests \
  --filter "FullyQualifiedName~RealDataReconciliationTests"

# 单源
TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests \
  --filter "FullyQualifiedName~Real_claude_data_reconciles_with_cli"
```

```powershell
$env:TOKENMETRY_RECONCILE_REAL="1"
dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~RealDataReconciliationTests"
```

- 无数据 / 无 npx 时测试 Skip 而非 Fail；Skip 不是通过，报告时要如实说。
- 首次运行 `npx --yes ccusage@<version>` 需要网络。
- **两侧必须同一时刻取**：日志仍在写入，跨分钟比较会得到快照差（§8.4）。

## 版本锚

- golden 与多数真实数据对账钉 `CcusageVersion = "20.0.29"`。
- Claude 真实数据对账用 `ClaudeRealDataCcusageVersion = "20.1.1"`（20.1.0 #2341 的代理网关多 block 口径）。
- 不要全局升版本；版本差异按「新行为 → 单独 fixture + 新版本 golden」处理（范例：`claude-proxy-fixture-daily-v20.1.1.json`）。

## 差异判读

报告按 日期 × 模型 × 字段 列出两侧值与偏差：

1. **tokens 不精确一致 → 解析/覆盖**：文件发现、行过滤、去重（消息/响应链/cumulative 守卫）、UTC 分桶、五桶映射。
2. **仅 cost 超差 → 定价**：快照漏收/家族兜底、`-fast`/flex/ultrafast 倍率、第三方渠道官方价投影、LogCost 短路。
3. 先排除 §6 已知分歧，再判回归。当前清单（细节读 §6；§6 变更时同步本清单）：
   - 显式 `costUSD: null`、缺失 `message.id`：CLI 丢弃，自研保留（设计使然）
   - ZCode 非 Z.ai provider：自研走三级计价，CLI 计 0（有意）
   - 第三方渠道：自研只认官方价，CLI 用 models.dev 全量（有意）
   - 档位倍率：自研静态表，不接 LiteLLM；Codex 只认日志内 `thread_settings_applied`
   - Kimi `kimi-for-coding` 按时间切 k2.5/k2.6
   - Codex 压缩 rollout：fork/子代理回放抑制与 compaction 去重**尚未移植**（会重复计）
   - OpenCode claude-agent 跨源去重：UTC 口径；升级前已双计行不自动清退
4. 其他已知 fail-loud（reasoning/余量并入 output 等）见各 `<Agent>FixtureReconciliationTests` 的 XML 注释。
5. 禁止用以下方式"修"差异：放松 `ReconciliationTolerance`、为了让测试过而重录 golden、把有意分歧当 bug。
6. 确认是回归：修适配器/定价 → 加聚焦单测；新记录形态 → 补/重录 golden 并加结构断言。

## golden 重录

命令全集在 `fixtures/golden/README.md`；固定形态：

```bash
FX=tests/Tokenmetry.Reconciliation.Tests/fixtures
CLAUDE_CONFIG_DIR="$PWD/$FX/claude" \
  npx --yes ccusage@20.0.29 claude daily --json --offline --timezone UTC \
  > $FX/golden/claude-fixture-daily-v20.0.29.json
```

不变量：

- 只用 per-agent 子命令；统一 `daily` 会扫本机其他 agent 默认目录，golden 不可复现。
- 环境变量钉住 fixture 目录；`--timezone UTC` 对齐自研 UTC 分桶；`--offline` 对齐内置快照计价。
- fixture 必须合成、脱敏：无真实 token、路径、用户名。
- 测试运行时不调 CLI；只有门控真实数据测试例外。
- 重录后同步：golden README 的录制信息、测试里的结构断言、对账文档中的数字。

## 新增数据源

1. 先按 `AGENTS.md` 的固定套路：Core 接口 → Infrastructure 适配器 → DI → Infrastructure.Tests。
2. 建合成 fixture 目录 `tests/.../fixtures/<agent>/`。
3. 按上面的形态录制 `<agent>-fixture-daily-v20.0.29.json`，并把命令补进 golden README。
4. 加 `<Agent>FixtureReconciliationTests`：链路健康诊断 + 结构断言（日期/模型/成本路径）+ 比较器。
5. 加真实数据测试：入口门控、数据存在性探测（复用产品 locator）、必要时版本例外。
6. 更新 golden README 的 fixture 覆盖说明。

## 蓝本版本同步

蓝本检出是本机同级 `../ccusage`（adapter 在 `rust/adapters/`，核心在 `rust/crates/ccusage-core/`）。

1. 读 `docs/reconciliation/2026-10-11-reconciliation.md` §0 最后一行，拿上一版版本号与 commit。
2. `git -C ../ccusage log <commit>..HEAD --oneline`，只挑影响 tokens/cost/日期分桶的变化。
3. 移植 + 单测；倍率/定价表按对应版本更新（如 `src/Tokenmetry.Core/Pricing/speed-multipliers.json` 锚 20.0.48）。
4. golden 策略：旧 golden 不动；新记录形态用独立 fixture + 新版本 golden 固化。
5. 在 §0 追加一行「日期 → 版本（commit）」；有意保留的差异写进 §6/§8。

## 收口

- `dotnet test tests/Tokenmetry.Reconciliation.Tests` 全绿。
- 受影响源的 `tests/Tokenmetry.Infrastructure.Tests/<Agent>` 全绿。
- 范围大时跑 `dotnet test Tokenmetry.sln`；至少 `dotnet build Tokenmetry.sln`。
- 文档按需更新：§0（版本同步）、§6/§8（新分歧）、golden README（录制/覆盖）。
- 报告实际结论与数字；没有测试输出不要写"对账通过"。
