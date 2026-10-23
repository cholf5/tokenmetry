---
id: P-001
title: 官方价单一目录——一模型一条目 + 用户条目前缀作用域
type: feature
priority: P1
size: L
status: todo
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

计价目录收敛为"官方公布价、一模型一条目"：白名单厂商 + 家族归属裁决 + 海外店优先 + 归一键去重的投影放 Core，作用于远程条目物化点；用户定价条目新增 provider 前缀作用域（官方=全局变量、用户=局部变量，局部优先）。彻底消灭多候选竞争类 bug。

## Background

- **事故实证（2026-10-21 诊断，本机库 + 最小复现）**：models.dev 远程层把 `glm-5.3-flash`（及大写变体、`ark/glm-5.3-flash`）同名精确匹配到 `scnet-token-plan/GLM-5.3-Flash` **全零条目**——条目按 ModelId ordinal 排序时大写 ID 排前 + 匹配大小写不敏感 + rank 同名严格取先者；蓝本"首个正成本候选"守卫因"桌面无此数据形态"被删（`PricingEngine.cs:119-121`），而 models.dev 已遍布 coding-plan 全零条目。结果 39.9 亿 tokens 计 $0.00（非 null，混入"已知成本"、不进缺价清单），30 天成本显示 $0.71（应约 $141.36）；`ark-code-latest` 成唯一 Missing，"待确认"tooltip 只提它。
- 断网兜底（纯快照）全程正确——病灶仅在远程层，故只投影远程目录，快照（官方八家离线固化）不动。
- golden 审计（2026-10-23）：全部 golden 仅官方模型名（唯一前缀条目 `openai/gpt-5` 本就是白名单厂商），Reconciliation.Tests 不受影响，§6 补记分歧即可。
- 用户拍板（2026-10-23 对话）：官方价 only；海外店为准（zai > zhipuai、alibaba > alibaba-cn）；`-free`/`-fast` 不进目录、剥标记为低优先级兜底候选（`glm-5.3-fast` 真实官方型号精确优先）；想标免费的用户自建全零条目；设置页文案"本工具仅提供一套官方标准价格的估算；非官方 Provider 价格不同时，请自行添加条目（可指定 Provider 前缀）"。

## Design

`docs/plans/2026-10-23-official-pricing-catalog-design.md`（设计定稿 + 六任务实施计划，TDD 步进）。

## Acceptance

- [ ] 事故回归测试：canned models.dev 形状数据下 `glm-5.3-flash` 命中 `zai/glm-5.3-flash`（cost > 0），全零条目不再被命中。
- [ ] `glm-5.3-fast` 按自己的价（不被 `-fast` 剥价吞）；`glm-5.3-flash-free` 落官方付费价；`ark-code-latest` 仍 Missing。
- [ ] `GLM-5.3-Flash`/`glm-5.3-flash` 身份合流（统计一行）。
- [ ] 用户条目：带前缀 scope 精确命中对应渠道；裸名覆盖官方；scope 拼错静默落官方。
- [ ] `dotnet test Tokenmetry.sln` 全量绿（含 Reconciliation.Tests）；§6 补记分歧。
- [ ] 实机复核：30 天成本 ≈ $141，"待确认"只剩真缺价模型，数字记卡。

## Dev Log

-（待实施填写）
