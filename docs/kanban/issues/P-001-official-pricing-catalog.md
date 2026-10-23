---
id: P-001
title: 官方价单一目录——一模型一条目 + 用户条目前缀作用域
type: feature
priority: P1
size: L
status: done
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

- 2026-10-23（实施日，T1–T6 全落地，分支 p-001-official-pricing-catalog）：
  - T1 `OfficialPricingCatalog.Project`（b987e4c）：白名单 8 厂商 + 家族归属表 + 海外店优先 + 归一键去重；红测 6 条复刻事故形态。
  - T2 远程层接线（df7c094）：ModelCatalog 远程条目先投影；`-free` 并入功能后缀剥离（低优先级候选，`-fast` 口径不变）；事故端到端 5 条（glm-5.3-flash 从 scnet 全零条目改命中 zai 官方价）。两个旧口径测试按新口径改写（分歧见对账 §6.5）。
  - T3 身份折叠（03bd579）：实测 I-035 既有机制（ModelId.Normalize + 归一化查找趟）已满足，无产品改动，钉子测试固化。GLM-5.3-Flash/glm-5.3-flash 统计合流自动生效。
  - T4 ProviderScope（494a59f）：作用域条目分流独立清单（不进全局定义表、不冒充裸名/官方价），引擎按"原始名最后 '/' 之前整段 provider 路径"精确匹配，拼错静默落官方价；踩坑：userEntries 装配曾把作用域条目误当裸名用户条目，已过滤。
  - T5 设置页（df53278）：目录面板官方价立场说明 + 编辑弹层 Provider 前缀输入位；保存键改 (ID, 作用域)——同 ID 裸名与各渠道条目互不清掉。
  - T6 对账补记 + 实机验收：§6 新增分歧 5；本机库生产管线重扫后 30 天成本 **$0.71 → $133.96**（glm-5.3-flash $74.11、GLM-5.3-Flash $52.71 按官方价；ark/glm-5.3-flash $3.31；glm-5.3-flash-free 按拍板默认计官方价 $3.13；ark/deepseek-v4.1-flash 与 ark-code-latest 转为如实 Missing 待用户纠偏）。全量 1392 绿。
  - 终审（独立审查者，approve-with-fixes）：修复趟 a2a2a9d——I-1 零价条目永不压价（裁决键 (是否全零, 店面偏好)，堵同店面大小写重复拼写与海外零价压国内有价两残留形态；裁决'零价不压价'>'海外店优先'）+ M-1 作用域条目覆盖计划候选链走法（否则 OpenClaw/Grok/Antigravity 无声失效）。修复后全量 1396 绿。
  - Deferred minor（终审清单，待用户定）：M-2 保存作用域条目清同 ID legacy 规则；M-3 作用域条目列表化 UX；M-4 家族表缺 codex（codex* 冻结快照价）；M-5 国内店回退未钉测试；M-6 作用域命中 MatchedKey 不反映 scope；M-7 仅元数据作用域条目静默失效；M-8 文档表述 + PushCandidate 热路径分配。
