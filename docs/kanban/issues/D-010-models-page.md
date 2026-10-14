---
id: D-010
title: 模型统计页（价格缺失显示未知口径样板）
type: feature
priority: P2
size: S
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

模型维度明细页：模型、关联 Agent、输入 / 输出 / 缓存读取 / 总 Token、成本；价格缺失显示「未知」而非 0，是全应用「未知成本口径」的样板页。

## Background

- 设计基准：原型 `#page-models`；页面 meta 文案「价格缺失时成本应标记为未知，而非 0」直接对应产品立场（可对账、缺价不折零，I-024 收口确立）。
- 关联 Agent 列：同一模型可能被多个 Agent 使用——显示主用 Agent 或「多 Agent」聚合展示，实现时定案记录。

## Acceptance Criteria

- [x] 全模型列表接 I-029 模型维度聚合
- [x] 缺价模型成本列显示「未知」且有视觉区分（chip 或弱化色），不混入合计
- [x] 合计行只计已估价部分，或已估/未估拆分显示，口径在页面可见
- [x] 随全局筛选联动，明暗两套目检

## Subtasks

- [x] ModelsViewModel + 明细表
- [x] 未知成本视觉与合计口径
- [x] 关联 Agent 列展示定案

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 未知口径

Steps:
1. fixture 含缺价模型；查看列表与合计。

Expected:
- 缺价行显示「未知」；合计不含该行成本但含其 token。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 实现落地：ModelsViewModel（明细行 + 合计行 VM）+ ModelsView（明细表 + 合计行 + 口径脚注），外壳真实化；meta 文案对齐原型「价格缺失时成本应标记为未知，而非 0」。本页无行点击下钻（卡内未要求）。
- **决策点 1（关联 Agent 列）定案：a) 契约演进**——`DimensionUsageRow` 增补 `DominantAgent`（主用 Agent）与 `AgentCount`（关联 Agent 数），仅模型维度填充（Agent/项目维度恒 null，测试锁定）。成本可控故取 a)：实现端 `GetDimensionBreakdownAsync` 本就把 session_facts 全量载入内存并按 dominant_model 分组，同循环加每模型每 Agent 会话计数（嵌套 Dictionary）即可，无额外查询。派生口径：主用 Agent = 会话数最多者（并列取键序数最小，与该模型 token 量无关——与 SessionCount 同源同批 session_facts，口径一致）；无会话数据（纯 CLI 源只有 facts 行）时诚实 null 不猜。展示定案：唯一 Agent → 该 Agent 名；多 Agent → 「主用名 等 N 个」（主用 + 聚合语义同时可见）；无会话 → "—"。对账测试：UsageQueryServiceTests 新增 `Model_breakdown_derives_dominant_agent_from_session_facts`（多 Agent / 1:1 并列 / 仅 facts 三分支）+ 共享 fixture 单 Agent 断言 + Agent/项目维度恒 null 断言。
- 未知成本口径（TC-001）：行成本四态（未知 / $x.xx+ / $x.xx / —），全未知行呈「未知」chip（复用 Border.chip 令牌，明暗成套）+ ToolTip「不计入合计」；混合行 "$x.xx+" 普通文本（"+" 即已知成本下限标记）。合计行：各 Token 列 = 全部行之和（含缺价行——缺价只影响成本不影响用量）；成本 = 已知成本之和（AggregatedCost.Add 归并），存在未知 → "$Y+"，全部未知 → 「未知」绝不折 0，无成本数据 → "—"；脚注「N 个模型成本未知（缺价）：其成本未计入合计，Token 已计入总量」使口径在页面可见；空明细无合计行（空态接管）。
- token 列（输入/输出/缓存读取/总 Token，含合计行）用紧凑格式 FormatCompactTokens：千分位全数字在定宽列会被裁切成错值（真实数据实测裁尾），宁紧凑不裁切（与原型 fmt() 全数字为显式偏差，D-009/D-011 同款裁定）。
- 遗留风险：本机真实数据暂无缺价模型（截图合计无未知脚注）——chip 形态与 TC-001 由 stub/fixture 用例覆盖，真实缺价源出现后的视觉样本留待补充目检。

### 2026-10-13（审查修复轮）

- **P2：m-tie 对账用例量级交换，断言获得区分度**——`Model_breakdown_derives_dominant_agent_from_session_facts` 中 a4/a5 的 token 量级互换（Codex 900 / Claude Code 100），使键序数胜者（Claude Code）token 更少："并列取会话数、平局取键序数最小"的正确实现与"取 token 大者"的错误实现不再同时通过该断言。
- **P2：契约文档补齐**——`DimensionUsageRow.DominantAgent` XML doc 补第三种 null 来源：该模型的会话中无有效 Agent 归属（空白 Agent 键被跳过）。注释级修复，无行为变化。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.Core/Models/DimensionUsageRow.cs`（DominantAgent/AgentCount 契约演进）、`src/TokenPulse.Core/Services/UsageQueryService.cs`、`src/TokenPulse.Core/Abstractions/IUsageQueryService.cs`、`src/TokenPulse.App/ViewModels/ModelsViewModel.cs`、`src/TokenPulse.App/Views/ModelsView.axaml(.cs)`、`tests/TokenPulse.Infrastructure.Tests/Query/UsageQueryServiceTests.cs`（对账）、`tests/TokenPulse.App.Headless.Tests/Shell/DimensionPagesTests.cs`（页面层）
- How to run/verify: `dotnet test`（Infrastructure 的 UsageQueryServiceTests + Headless 的 DimensionPagesTests）+ 明暗截图目检
- Results: 2026-10-13（审查修复轮后）全套件 987 通过 / 22 跳过 / 0 失败 / 0 警告。TC-001 通过（`Totals_row_includes_unpriced_tokens_but_excludes_unknown_cost`：缺价行「未知」、合计 Token 10,790 含缺价行 160、成本 $5.60+ 不含缺价行）；成本四态（全已知 "$Y" / 混合 "$Y+" / 全未知「未知」/ 空 "—"）与关联 Agent 三态各有用例（m-tie 用例已量级交换为可区分断言）；全局筛选联动 / 序号防护 / 失败保留上次成功数据 / Dispose 退订覆盖。截图 `docs/design/models-light.png`、`docs/design/models-dark.png`（真实数据 2 模型全已知成本，合计行可见）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：三卡组合审查结论"有问题需修复"——1×P1 + 3×P2；契约演进推导、对账三分支手算、两条跨页途径端到端、TC-001 合计口径、数字裁切修复、三页模式合规全部核实为真。修复（原开发 agent 完成，PM 验收）：
1. **P1 Agent 跳转死胡同**（目录缓存不刷新时新 Agent 只补全局选项、右栏复选组快照不含它 → 会话页 Empty 硬 0/0 且「清除」不可恢复）：OpenSessionsForAgent 补全局选项时同步补入已勾选的右栏复选项（复选组非空才补，空组走既有 Single 兜底），死胡同解除且状态对用户可见可清除；新增死胡同变体端到端回归。
2. P2 tie-break 对账缺区分度（键序数胜者恰巧 token 更多）→ 交换 fixture token 量级使断言 discriminating。
3. P2 目录无界超集不变量落成双向注释锁定（防后人收窄目录查询）。
4. P3 DominantAgent XML doc 补第三种 null 来源（会话无有效 Agent 归属）。
PM 验收：build 0 警告 + 全量 987 通过 / 22 跳过 / 0 失败。
