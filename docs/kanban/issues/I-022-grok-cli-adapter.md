---
id: I-022
title: Grok Build CLI 适配器
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Grok Build CLI 会话数据（GROK_HOME 或 ~/.grok/sessions/<url-encoded-cwd>/<uuid>/updates.jsonl），产出 agent=Grok 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/grok/。仅计 turn_completed 完成行；OpenAI 风格 inputTokens 含 cache 需扣除；reasoningTokens 已含在 output 内（丢弃防重复）；costUsdTicks 为发票成本（1 tick = 1e-10 USD），display 口径直接采用、calculate 回退 token×价——本项目统一由 PricingEngine 计价，CostUsd 采用日志 ticks（LogCost 短路），与 CLI display/auto 口径一致。logs/unified.jsonl 明确不用（无逐请求模型 id）。本机暂无数据。

## Acceptance Criteria

- [x] 目录发现（url-encoded cwd 层级）与 turn_completed 过滤
  - ✅ 2026-10-11：`GrokLogLocator` 逐条移植 paths.rs——GROK_HOME **trim 后非空才生效**（pi 同款：
    空串回退默认；单根、必须为存在目录），默认 ~/.grok（存在才保留）；sessions/ 子树递归收集 .jsonl
    仅留文件名恰为 updates.jsonl（logs/unified.jsonl、events.jsonl 等一律不发现），兄弟 summary.json
    存在即携带；按完整路径排序（蓝本 files.sort_by）。configuredDataDir 为仓库共有测试钉定增强
  - ✅ `GrokLogParser` 逐条移植 parser.rs：行预过滤须含 "turn_completed" 片段；仅 sessionUpdate 恰为
    "turn_completed" 且 usage 为对象的行出账；modelUsage 逐模型拆行（键 Ordinal 排序；缺失/null/
    <b>空 map</b> 回退顶层 usage 单行 + summary current_model_id → "unknown"）；时间戳三级回退
    （_meta.agentTimestampMs 毫秒（整数/浮点截断，Rust `as i64` saturating 语义）→ envelope timestamp
    Unix 秒 ×1000（saturating）→ UNIX 纪元）；会话元数据（目录名推导 + summary.json info.id /
    info.cwd ?? git_root_dir / current_model_id 覆盖 + 行内 params.sessionId 优先）与 URL 轻量解码
    （%XX 逐字节解码 → UTF-8 宽松还原，非法字节 U+FFFD）；32 项单测全绿
- [x] token 拆分（cache 扣除、reasoning 丢弃）与 costUsdTicks → CostUsd 换算
  - ✅ `split_input_tokens`：cache_read = min(cachedReadTokens, input)、uncached = input − cache_read；
    cache_creation = min(cacheCreationTokens, uncached) 再从 uncached 扣除（三段还原 inputTokens）；
    reasoningTokens 已含在 outputTokens——<b>丢弃</b>（extra 恒 0，加回会二次计费）；五桶（含
    reasoning）全零跳过（reasoning&gt;0 单独成行、token 全零）。costUsdTicks &gt; 0 → decimal 精确换算
    （ticks / 10_000_000_000m，从 long 构造不经 double）入统一记录 CostUsd（LogCost 短路）；无 ticks
    交定价引擎（蓝本 xai/ 候选链现算属 I-024 欠账，见偏差①）
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ 32 项 Infrastructure 单测（蓝本 loader 7 项 + parser 20 项内联测试同名场景移植：token 三段拆分、
    多模型拆行、双层去重、时间戳优先级、URL 解码宽标量/非法字节、summary 覆盖链、ticks 成本、
    结构性拒绝计数 + 目录发现 7 场景）+ fixture 对账测试 ×2 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账
    测试（HasGrokData 数据存在性 Skip）
  - ✅ `IGrokAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十七路 concat + App DI 双注册

## Subtasks

- [x] `GrokLogLocator`（paths.rs 移植）：ResolveRoot（configuredDataDir → GROK_HOME（trim 非空、
  存在目录）→ ~/.grok）；CollectSessionFiles（sessions/ 递归 .jsonl + updates.jsonl 文件名过滤 +
  兄弟 summary.json + 路径排序）；共享 `JsonSource/SourceFileCollector`、`Jsonl/JsonlHomePath`
- [x] `GrokLogParser`（parser.rs 移植）：ParseLine（结构性失败面 = 顶层非对象、params/update/usage
  标量、modelUsage 非对象或值为标量——serde Option&lt;结构体&gt;/HashMap 反序列化失败；语义跳过 =
  缺 params/update/usage、非 turn_completed、全零行；timestamp/agentTimestampMs 为 Option&lt;Value&gt;）；
  ModelUsageRows；LoadSessionMeta；ResolveTimestamp + ValueAsI64；SplitInputTokens；UrlDecodeLightweight；
  文件内 dedupe_key（有 eventId → "eventId|模型"，无 → 内容键 session|毫秒|模型|四桶|reasoning——
  reasoning 不入统一记录故内容键在解析层比较）seen 集合
- [x] `GrokAdapter`（loader.rs 移植）：发现 → 共享 `IncrementalJsonlScanner` 增量扫描（mtime/大小
  记账）→ 去重委托钩子注入 `GlobalDedupe`（跨文件 "eventId|模型" first-wins——同一服务端事件可被
  导出进多个会话；无 eventId 行不参与防粗键误并，蓝本注释同义）→ 时间戳稳定排序
- [x] 单元测试 ×32（`GrokAdapterTests`：token 拆分三态、旗舰映射（含缓存扣除/reasoning 丢弃/summary
  cwd）、reasoning-only 行、顶层 usage 回退与 unknown、时间戳三级、summary 覆盖链与行内优先、URL
  解码四态、skip 场景组、多模型拆行、双层去重四态、ticks 成本精确 decimal、结构性拒绝计数、跨文件
  排序/全局去重/损坏文件隔离、发现过滤与排序、env 语义三态、无数据目录）
- [x] 对账：合成 fixture（3 会话目录：主会话 5 行 + 跨会话 eventId 重放 + 无 summary 会话 + 损坏
  文件）入仓 + CLI golden 录制（fixtures/golden/grok-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasGrokData 数据存在性 Skip）
- [x] 管道接线：`IGrokAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十七路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无需新增条目**——带 ticks 的行走 LogCost（fixture 全部可计价行带 ticks）；无 ticks
  行用 unpriced 模型名（CLI missingPricing 双侧 0 成本）；"grok-4.5-build" 等裸模型名候选计价属
  I-024 欠账（见偏差①），fixture 规避

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 sessions/**&#47;updates.jsonl（蓝本内联测试同数据：token 拆分、多模型拆行、
   去重双层、时间戳三级、URL 解码、summary 覆盖链、ticks 成本、损坏文件、env 语义），运行
   `GrokAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 仅文件名恰为 updates.jsonl 的 .jsonl 被发现（logs/unified.jsonl 不发现）；仅 turn_completed 且
  usage 对象的行出账；inputTokens 缓存扣除、cache_creation 子集拆分、reasoning 丢弃（extra 恒 0）；
  costUsdTicks decimal 精确换算（1 tick = 1e-10 USD）走 LogCost；文件内内容键去重 + 跨文件
  eventId|模型 全局去重均 first-wins；params/update/usage/modelUsage 标量计 Malformed；GROK_HOME
  空串回退默认目录。

Result: 通过（Infrastructure 32/32，场景清单见 Subtasks）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/grok/`（sessions/D%3A%5Cwork%5Cproj/
   三个会话目录：主会话（ticks 发票成本行 + 双 unpriced 模型行 + cache_creation 拆分行 + 无 eventId
   重复行）、无 summary 会话（同 eventId 重放 + agentTimestampMs 行 + "unknown" 回退行 + tool_call/
   缺 usage 诱饵）、损坏文件）。
2. 录制 golden：`GROK_HOME=$PWD/$FX/grok npx --yes ccusage@20.0.29 grok daily --json --offline
   --timezone UTC > $FX/golden/grok-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateGrokAsync(GrokRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（ticks 行 LogCost 直采两侧同值；unpriced 行双侧
  0 成本）；自研未定价清单 = CLI unpricedModels（grokbuild-alpha-x1 / grokbuild-beta-x2 / unknown）；
  诊断 status=Ok、files=3、malformed=0、invalid=0、linesSkipped=5、duplicate=1、records=7。

Result: 通过——两侧全量 **input=7,655 / output=254 / cacheCreation=25 / cacheRead=11,454 /
total=19,388 tokens，totalCost=$0.0246792** 逐位一致（2025-06-15：grok-4.5-build 7265/160/25/11304
cost 0.0236792（0.0185192 + 0.005 + 0.00016 三行 ticks 直采）+ 双 unpriced 模型 + unknown 行双侧 0；
2026-07-29：agentTimestampMs 毫秒归日 10/1 cost 0.001）；诊断 FilesDiscovered=3、records=7、
DuplicateRecords=1。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_grok"`。
2. 数据存在性门控：定位器（GROK_HOME / ~/.grok 的 sessions/）下发现 updates.jsonl 才运行（与 CLI
   子进程同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 grok daily --json --offline --timezone UTC`（golden）
   vs `AggregateGrokAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致；带 ticks 的行成本一致（LogCost）；无 ticks 的可计价模型行成本
  fail-loud（CLI 候选链现算含长上下文分档，桌面快照无分档，见偏差①②）。

Result: 本机（2026-10-11）**无 Grok 数据 → 测试按门控 Skip**（与 pi/codebuff 门控同态）。门控链路
本身已在本机验证：以 `GROK_HOME=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与自研
全链路对账**通过**（`Real_grok_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行该
门控即得真实数字。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/grok/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 grok/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 GrokLogLocator / GrokLogParser / GrokAdapter 文件头；fixtures
全部合成，无真实数据入仓）：

- **共享引擎复用**：updates.jsonl 为行式 JSONL，走 I-001 共享 `IncrementalJsonlScanner`（文件 mtime/
  大小增量记账 + 变更文件整文件重解析 + 诊断状态）；解析委托承担逐文件语义（含 summary.json 兄弟
  读取与文件内去重），跨文件全局去重经 I-011 首建的 `dedupe` 委托钩子注入（键 "eventId|模型"
  first-wins，文件序 = 发现序（路径排序）与蓝本 read_files_parallel 顺序折叠一致）。共享件复用：
  `collect_files_with_extension` → `JsonSource/SourceFileCollector`、lenient 字段读取 →
  `SqliteSource/LenientJson`、纪元时间戳 → `StrictRfc3339Timestamp.Epoch`。
- **turn_completed 过滤**（Background 口径）：行预过滤须含 "turn_completed" 片段（蓝本
  LinePrefilter::all 同款——无标记的坏行两侧同样静默/显式跳过）；sessionUpdate 恰为 "turn_completed"
  且 usage 为对象才出账；logs/unified.jsonl 无逐请求模型 id，文件名过滤天然排除。
- **token 三段拆分**（蓝本 split_input_tokens 逐条）：cachedReadTokens 可证为 inputTokens 子集
  （session 总数与 unified.jsonl 对账实证）；cache_creation 后到、恒为 uncached 子集（观测恒 0）——
  min 钳制后从 uncached 再扣除，三段恒还原 inputTokens；reasoningTokens 已含在 outputTokens
  （totalTokens == input+output）——丢弃不入 extra（蓝本 extra_total_tokens 恒 0），五桶（含
  reasoning）全零跳过，reasoning&gt;0 单独成行（token 全零记录，蓝本 keeps 语义）。
- **成本落点**（Background 口径）：costUsdTicks 为定点发票成本（1 tick = 1e-10 USD，蓝本以 58 turn
  真实数据实证 ticks/1e10 恰为 xAI 牌价；turn 级为逐请求求和，重算无法复现长上下文分档边界）——
  ticks &gt; 0 时 decimal 精确换算入统一记录 CostUsd（LogCost 短路 = CLI display/auto 优先采纳发票
  成本）；无 ticks 交 PricingEngine（蓝本 calculate 的候选链 xai/、x-ai/ 前缀与 -build 剥离 + 长上下文
  分档 find_at 属 I-024 端到端欠账，见偏差①②）。ticks 从 long 构造 decimal 除以 10_000_000_000m，
  不经 double（精度要求）。
- **去重双层**（蓝本 dedupe_key + loader retain 逐条）：文件内 seen 集合（有 eventId → "eventId|模型"、
  无 → 内容键 session|毫秒|模型|四桶|reasoning——reasoning 不入统一记录，内容键必须在解析层比较，
  蓝本注释明确粗键重构会误并 turn）；跨文件全局 "eventId|模型" first-wins（无 eventId 行保留）。
- **诊断口径**（仓库 I-004 显式化）：LinesSkipped = 预过滤未过行（tool_call、坏行）+ 缺 params/update/
  usage 行 + 全零行 + 文件内 dedupe 折叠行 + 打不开的文件；MalformedLines = 预过滤通过但 JSON/形状
  失败的行（顶层非对象、params/update/usage 标量、modelUsage 非对象或值为标量——serde Option&lt;结构
  体&gt;/HashMap 反序列化失败面）；InvalidRecords 无对应场景恒 0（时间戳有纪元兜底）；DuplicateRecords =
  跨文件全局折叠数（文件内折叠计入 LinesSkipped，与蓝本双层结构对应）；状态 Ok/Partial/NoData 同
  I-006。
- **管道接线**：`IGrokAdapter` 单例 + LocalIngestionDataSource 第十七路 concat；适配器实例持有扫描
  状态（共享引擎记账），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：无需新增（fixture 可计价行全带 ticks 走 LogCost；unpriced 行双侧 0）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选链现算未端到端接入**：蓝本 calculate/auto-无-ticks 路径按候选链（剥 "[grok] " 前缀、剥
  "-build" 后缀、补 xai/ 与 x-ai/ 前缀；exact 全候选优先再 fuzzy）查 PricingMap 现算，且 xai/grok-4.5
  带 200K 长上下文分档（find_at 按时刻分档）；桌面引擎按模型名（快照 exact/家族）匹配、无分档——
  无 ticks 的可计价模型行成本两侧不同（fail-loud）。fixture 全部可计价行带 ticks（真实 Grok 数据
  每 turn 都带，蓝本 58 turn 实证），unpriced 模型行（双侧 0 成本）覆盖缺失计价路径。I-024 已登记
  同类共性欠账。
- ② **长上下文分档不复现**：蓝本 pricing.rs 对 grok-4.5/4.6 在 input &gt; 200K 时按双倍档计价；桌面
  PricingEngine 无分档概念——大 input 的无-ticks 行成本 fail-loud（与①同属定价引擎能力欠账，
  I-024 观察项口径）。
- ③ **无 Display/Calculate 模式开关**：蓝本 Display 用发票成本（无 ticks 记 0）、Calculate 恒重算；
  桌面恒「有 ticks 采纳、无 ticks 现算」= CLI auto 口径（Background 明示的对齐目标）；CLI --mode
  显示差异不复现（I-008 偏差⑦同款架构）。
- ④ **missing_pricing 报告口径**：蓝本 missing_grok_pricing 在有 ticks 的行不告警（display/ticks-backed
  auto 不查表）；桌面 PricingEngine 对 CostUsd 非空行走 LogCost 同样不入未命中清单——语义一致；
  无 ticks + 未定价行的未命中报告由引擎清单承担（模型名/记录数口径与蓝本 missing_pricing_model
  字符串不同，仅诊断面差异）。
- ⑤ **行级拒绝/空产出显式计数**（蓝本静默）：预过滤未过、缺 params/update/usage、非 turn_completed、
  全零行、文件内 dedupe 折叠计 LinesSkipped；形状失败计 MalformedLines——诊断不改变记录内容
  （I-004 显式化口径）。文件内折叠与跨文件折叠分置两个计数（蓝本双层结构对应）。
- ⑥ **模型名展示**：蓝本显示 modelUsage 原始键（如 grok-4.5-build，"[grok] " 前缀仅用于定价候选）；
  桌面 record.Model = 原始键同口径。蓝本 pricing_candidates 的 "[grok] " 剥离语义随①未接线（桌面
  引擎候选含 "[pi] x" 形态剥离但不剥 "[grok] "——若真实数据出现 "[grok] " 键则成本 fail-loud，
  理论角落）。
- ⑦ **u64 → long 边界**：lenient_u64/costUsdTicks 读取层把 u64 溢出钳到 long.MaxValue（saturating；
  I-017 偏差⑥同款）；ticks/1e10 在 long 域内 decimal 精确（蓝本 f64 对超大 ticks 有相对误差——
  现实值域两侧一致）。
- ⑧ **timestamp 理论边界**：毫秒越界（DateTimeOffset 表达域外）回退 UNIX 纪元（蓝本 jiff 同域受限，
  from_millis 直接取值——极端负值/越界毫秒两侧结果不同，理论角落）；浮点时间戳截断按 Rust
  `as i64` saturating 语义（非有限 → 0）。
- ⑨ **无 since/until 窗口 / Display 模式 / has_data 探针不复现**：桌面统一全量出账（I-008 偏差⑦
  同款架构）；数据存在性探测由门控测试的定位器调用承担。
- ⑩ **configuredDataDir 为仓库增强**（非蓝本）：测试钉定入口（env 同规则：trim 非空才生效）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **623 通过 / 20 跳过**
（I-019 收口基线 589/19 + 新增 32 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 GROK_HOME 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 /
TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（三卡均可收口、代码无缺陷；本卡修复 P2-1 登记一项 + 观察项登记）：

- **[P2-1] I-024 卡补登记 GK-1/GK-2 与长上下文分档**：I-024 卡 Background 增补"三源欠账"段
  （Grok 候选链 GK-1 + "[grok] " 前缀剥离 GK-2 + 200K 长上下文分档——后者并入 I-024 既有 tiers
  观察项）、Dependencies 追加 I-022，闭环偏差①的"I-024 已登记"声称。
- **[观察项] Bugs 表新增 GK-3（deferred）**：共享扫描器全局 "eventId|模型" first-wins 去重的胜者
  依赖引擎文件插入序（首次扫描发现序记账），蓝本每次全量按路径排序折叠——跨扫描新增排序在前的
  文件时，重叠 eventId 场景两侧胜者理论上可反向（I-001/I-011 引擎既有特性，非本批引入；记录内容
  不受影响，仅存活副本归属）；随真实数据对账回归再对齐。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **623 通过 / 20 跳过**
  （本批收口数字不变——本批修复为文档/登记项，无代码与测试变更）；grok fixture 对账 2/2 与 CLI
  golden 逐位一致不回归；门控以 GROK_HOME 钉 fixture 复跑通过（1/1 Passed）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| GK-1 | P3 | 无 ticks 的可计价模型行走桌面引擎直配（无候选链/无长上下文分档），CLI 现算含分档——真实数据该场景成本 fail-loud | deferred | 定价引擎候选链与分档能力属 I-024 共性欠账；随 I-024 一并处理或独立小卡 |
| GK-2 | P3 | "[grok] " 前缀键的定价候选剥离未接线（桌面引擎仅剥 "[pi] x" 形态）——若真实数据模型键带该前缀则成本 fail-loud | deferred | 理论角落（modelUsage 键实测为裸模型名）；随 I-024 候选计价接线一并评估 |
| GK-3 | P3 | 共享扫描器的全局 "eventId&#124;模型" first-wins 去重，胜者依赖引擎的文件插入序（首次扫描的发现序记账）；蓝本每次全量按路径排序折叠——跨扫描新增排序在前的文件时，重叠 eventId 场景两侧胜者理论上可反向（I-001/I-011 引擎既有特性，非本批引入；记录内容不受影响，仅存活副本归属） | deferred | 理论角落（需同 eventId 跨会话 + 新增排序在前的文件两个条件叠加）；随真实数据对账回归再对齐（引擎增补按当前发现序重排或适配器全量重读） |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/Grok/GrokLogLocator.cs`、
  `src/Tokenmetry.Infrastructure/Grok/GrokLogParser.cs`、
  `src/Tokenmetry.Infrastructure/Grok/GrokAdapter.cs`、
  `src/Tokenmetry.Core/Abstractions/IGrokAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/Grok/GrokAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/GrokFixtureReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/grok/`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/grok-fixture-daily-v20.0.29.json`、
  `tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`（全绿）；fixture 对账
  `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~GrokFixture"`；门控复跑
  `GROK_HOME=$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/grok TOKENMETRY_RECONCILE_REAL=1
  dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_grok"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 **623/20**，
  2026-10-12 独立审查修复复核后数字不变）。
