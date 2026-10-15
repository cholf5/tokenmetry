---
id: I-018
title: Codebuff 适配器
type: feature
priority: P3
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Codebuff 会话数据（CODEBUFF_DATA_DIR 或 ~/.config/<channel>/projects/**/chat-messages.json，channel 含 manicode/-dev/-staging），产出 agent=Codebuff 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/codebuff/。多 channel 目录枚举 + 单文件 JSON。本机暂无数据。

## Acceptance Criteria

- [x] 多 channel 目录发现与解析；token 映射对齐蓝本
  - ✅ 2026-10-11：`CodebuffLogLocator` 逐条移植 paths.rs——CODEBUFF_DATA_DIR **已设置即只用配置**
    （含空串生效为空、不回退三 channel 默认；蓝本 env 分支提前选定同口径；与共享 EnvDataDirResolver
    不同：本蓝本 env 分支**不过滤不存在的目录**，存在性检查下沉到派生的 projects/ 子目录），默认
    ~/.config/&lt;channel&gt; ×3（manicode / manicode-dev / manicode-staging）；项目根派生：根目录文件名
    恰为 "projects" 者原样使用、否则拼 projects/，仅保留实际存在者按序去重；chat 文件 = 项目根下递归
    收集 .json 且文件名恰为 chat-messages.json（排序 + 去重）；configuredDataDir 参数为仓库共有测试
    钉定增强（非蓝本）
  - ✅ `CodebuffChatParser` 逐条移植 parser.rs：单文件 JSON **数组**（serde from_slice&lt;Vec&lt;Value&gt;&gt;
    拒绝面：坏 JSON / 顶层对象、标量、null → 整文件跳过，仓库口径计 Malformed）；assistant 判定
    variant 优先 role 兜底 ∈ ai|agent|assistant；取数回退链 metadata.usage → metadata.codebuff.usage →
    runState.messageHistory 倒序（providerOptions.usage 与 codebuff.usage，codebuff.model 覆盖
    usage.model），merge_fallback 逐字段 0 值兜底；usage 对象多键名别名取**第一个 &gt;0** 的 u64
    （pick_u64：0 值跳过试下一键），cache_read 取直接键与 promptTokensDetails.cached_tokens /
    prompt_tokens_details.cached_tokens 最大值；total 兜底（共享 TokenUsageFallback）；时间戳链
    message.timestamp（字符串严格 RFC3339 / 数字 &lt;10^10 视为秒）→ createdAt → metadata.timestamp →
    chat 目录名（"2026-01-02T03-04-05.000Z" 形态前两个 '-' 还原 ':'）→ 文件 mtime → UNIX 纪元；缺省
    模型 codebuff-unknown；35 项单测全绿
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ 35 项 Infrastructure 单测（蓝本 loader/parser 内联测试同名场景 + 载荷类型角落：usage null/标量/
    数组、metadata 非对象、runState 非对象历史条目与 providerOptions 标量、credits-only 信号、
    last-wins 去重、env 空串/多根/projects 根、多 channel 枚举、定价快照与缺省模型欠账）
  - ✅ 合成 fixture（7 文件：双项目双日 + runState + codebuff 兜底 + total 兜底 + 缺省模型 + 坏 JSON +
    顶层对象诱饵）入仓 + CLI golden 录制 + fixture 对账测试 ×2 + `TOKENMETRY_RECONCILE_REAL` 门控真实
    对账测试（HasCodebuffData 数据存在性 Skip）
  - ✅ `ICodebuffAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十五路 concat + App DI 双注册

## Subtasks

- [x] `CodebuffLogLocator`（paths.rs 移植）：configuredDataDir → CODEBUFF_DATA_DIR（已设置即不回退
  默认，空串生效为空，不过滤不存在目录）→ ~/.config/&lt;channel&gt; ×3；projects/ 派生（根名恰为
  projects 原样用）+ 存在性过滤 + 按序去重；项目根下递归 .json 收集 + chat-messages.json 文件名过滤 +
  排序去重；共享 `JsonSource/SourceFileCollector`、`Jsonl/JsonlHomePath`
- [x] `CodebuffChatParser`（parser.rs 移植）：AssistantUsage 载体（Model/Credits + 四桶 + extra）；
  is_assistant_message（variant → role，恰三值）；extract_assistant_usage（metadata 链 + runState 倒序 +
  消息级 credits）；parse_usage_object（多别名 pick_u64 首个 &gt;0、cache_read 三路 max、total 兜底、
  载荷内 model/credits 并入）；merge_fallback / has_signal（credits&gt;0 即信号）；message_timestamp 链 +
  parse_codebuff_chat_id_timestamp（'-'→':' 还原）；dedup_key（有 id → "codebuff:{session}:{id}"，无 id →
  内容键含 RFC3339 毫秒文本 + 模型 + 数组 ordinal + 五个计数分量（in/out/cr/cc/extra，蓝本 dedup_key 同为 5））；infer_provider / calculate_codebuff_cost
  的 provider 候选计价不移植（I-024 欠账，见偏差①）
- [x] `CodebuffAdapter`（loader.rs 移植）：发现 → 逐文件解析 → 跨文件 dedup key **last-wins** 去重
  （蓝本 HashMap insert，与 Qwen/Kimi 的 first-wins 相反）→ 时间戳稳定排序 → 统一记录与诊断；Amp
  同款全量重读（单文件 JSON 非 JSONL，无增量记账）
- [x] 单元测试 ×35（`CodebuffAdapterTests`：蓝本 loader 内联测试同数据三场景、metadata.codebuff 兜底
  与 cachedTokensCreated 别名、首个别名 &gt;0 生效、last-wins 跨文件/同文件去重与无 id ordinal 内容键、
  时间戳四层回退链与秒/毫秒自适应、usage null/标量/数组、metadata 非对象、非对象消息与非 assistant 行、
  credits-only 信号、usage 载荷 model/credits、runState 倒序/codebuff.model 覆盖/非对象条目/直接 usage
  先到、坏 JSON+顶层对象+顶层 null、发现过滤与递归、缺省模型、跨文件排序、快照定价（裸 anthropic
  日期后缀名家族命中）、未知模型欠账、env 语义四场景、多 channel 枚举、无数据目录）
- [x] 对账：合成 fixture（projects/alpha|beta 双项目 + 2026-02-10～15 六 chat + 2 诱饵文件）入仓 +
  CLI golden 录制（fixtures/golden/codebuff-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasCodebuffData 数据存在性 Skip）
- [x] 管道接线：`ICodebuffAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十五路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无需新增条目**——claude-sonnet-4-20250514 由快照 anthropic/claude-sonnet-4 家族模式
  （match "claude-sonnet-4"）命中（3/15/0.3/3.75，与蓝本 models-dev-pricing.json
  anthropic/claude-sonnet-4-20250514 同价）；openai/gpt-5 已在快照（1.25/10/0.125 + cache_write 1.5625）；
  codebuff-unknown 按欠账口径不参与定价（CLI missingPricing 双侧 0 成本，fixture 实证）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 projects/**/chat-messages.json（蓝本 loader 内联测试三场景同数据、metadata/codebuff/
   runState 三层回退、total 兜底、重复 id、时间戳四层回退、usage/metadata 非对象、非 assistant 行、
   credits-only、坏 JSON/顶层对象/顶层 null、projects 根名、多 channel、env 空串/多根），运行
   `CodebuffAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 仅文件名恰为 chat-messages.json 的 .json 被发现；非对象消息元素/非 assistant/无信号行计 LinesSkipped；
  坏 JSON 与非数组顶层计 MalformedLines；同 dedup key last-wins（后到覆盖）计 DuplicateRecords；
  CODEBUFF_DATA_DIR 设置即不回退三 channel 默认（空串生效为空）；缺省模型 codebuff-unknown 不参与
  定价（I-024 欠账，fail-loud）。

Result: 通过（Infrastructure 35/35，场景清单见 Subtasks）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/codebuff/`（合成数据，覆盖：metadata.usage
   四桶 + credits、同 id 同文件重复 last-wins、metadata.codebuff.usage 兜底、runState 倒序回退、total
   兜底、chat 目录名时间戳、缺省模型、坏 JSON 与顶层对象诱饵）。
2. 录制 golden：`CODEBUFF_DATA_DIR=$PWD/$FX/codebuff npx --yes ccusage@20.0.29 codebuff daily --json
   --offline --timezone UTC > $FX/golden/codebuff-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateCodebuffAsync(CodebuffRoot)`（适配器 → PricingEngine 内置
   快照 → UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；codebuff-unknown 为
  唯一未定价模型（CLI missingPricing 双侧 0 成本）；诊断 status=Partial、files=7、malformed=2、
  invalid=0、duplicate=1、linesSkipped=1、records=6。

Result: 通过——两侧全量 **input=7,900 / output=2,650 / cacheCreation=500 / cacheRead=4,500 /
total=15,550 tokens，totalCost=$0.04954** 逐位一致（02-10 claude-sonnet-4-20250514 2300/550/200/2500
cost 0.01665；02-11 openai/gpt-5 3000/450/0/1200 cost 0.0084；02-12 claude-sonnet-4-20250514
2000/100/300/800 cost 0.008865；02-13 openai/gpt-5 + codebuff-unknown 600/1550 cost 0.015625）；诊断
status=Partial、FilesDiscovered=7、records=6、DuplicateRecords=1。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_codebuff"`。
2. 数据存在性门控：定位器（CODEBUFF_DATA_DIR / ~/.config/&lt;channel&gt; 的 projects/）下发现
   chat-messages.json 才运行（与 CLI 子进程同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 codebuff daily --json --offline --timezone UTC`（golden）
   vs `AggregateCodebuffAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（extra 并入 output 的显示差异 fail-loud，见偏差②）；缺省模型
  （codebuff-unknown）与 provider 前缀候选计价行的成本分量 fail-loud（见偏差①）。

Result: 本机（2026-10-11）**无 Codebuff 数据 → 测试按门控 Skip**（与 kimi/qwen 门控同态）。门控链路
本身已在本机验证：以 `CODEBUFF_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与
自研全链路对账**通过**（`Real_codebuff_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行
该门控即得真实数字。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/codebuff/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 codebuff/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 CodebuffLogLocator / CodebuffChatParser / CodebuffAdapter
文件头；fixtures 全部合成，无真实数据入仓）：

- **数据形态与引擎选择**：chat-messages.json 为单文件 JSON **数组**（非 JSONL），走 Amp（I-014）
  同款全量重读适配器（不走共享 IncrementalJsonlScanner）；蓝本 load_entries 的"排序文件顺序 +
  HashMap insert last-wins 去重 + sort_by_key(timestamp) 稳定排序"逐条保真。共享件复用既有移植：
  `collect_files_with_extension` → `JsonSource/SourceFileCollector`、纪元时间戳 →
  `StrictRfc3339Timestamp.Epoch`、`apply_total_token_fallback` → `JsonSource/TokenUsageFallback`、
  lenient 字段读取 → `SqliteSource/LenientJson`；严格 RFC3339 → `JsonSource/StrictRfc3339Timestamp`。
- **目录发现**（paths.rs 逐条）：CODEBUFF_DATA_DIR 已设置（含空串）即只用配置、不回退三 channel 默认
  （蓝本 env 分支提前选定）；与共享 `EnvDataDirResolver` 的差异**保真**：本蓝本 env 分支不检查目录
  存在性（存在性检查在派生的 projects/ 上），故 CodebuffLogLocator 自行实现 env 解析而不复用该共享件；
  默认分支 ~/.config/&lt;channel&gt; ×3 同样不做存在性预检；项目根派生：`root.file_name() == "projects"`
  者原样使用（C# Path.GetFileName 对尾随分隔符敏感、Rust 不敏感——已对齐：取文件名前去尾部分隔符）、
  否则拼 projects/；仅保留实际存在者、按序去重（HashSet insert 同口径）。蓝本 PathBuf 组件级相等
  （"a/./b" == "a/b"）未复刻——自研用 Ordinal 文本去重，"./" 段路径在两侧产生的重复文件由 dedup_key
  last-wins 二次折叠兜底（理论角落，登记偏差⑥）。
- **解析分支**（parser.rs 逐条）：顶层必须反序列化为 Vec&lt;Value&gt;（坏 JSON / 对象 / 标量 / null →
  整文件空，仓库口径计 Malformed——蓝本静默，显式计数偏差）；assistant 判定 variant 优先 role 兜底；
  取数链 metadata.usage → metadata.codebuff.usage → runState.messageHistory **倒序**（末位 assistant
  条目字段先到先得；codebuff.model 用 `.or()` 语义**覆盖** usage.model——非 merge 兜底）；消息级 credits
  兜底（usage.credits ≤ 0 时）；parse_usage_object 的 pick_u64 语义与 LenientJson.GetU64 不同
  （**首个 &gt;0** 的别名键生效、0 值跳过试下一键、u64 溢出钳 long.MaxValue），Codebuff 自带实现；
  cache_read = max(直接键, promptTokensDetails.cachedTokens, prompt_tokens_details.cached_tokens)；
  usage 载荷自身的 model/credits 并入；时间戳：字符串走严格 RFC3339、数字 &lt;10^10 视为秒（×1000，
  受门控不溢出）否则毫秒、必须 &gt;0；chat 目录名时间戳还原前两个 '-' 为 ':'；mtime → UNIX 纪元兜底。
- **去重语义**：dedup_key 有 id → "codebuff:{session}:{id}"、无 id → 内容键（RFC3339 毫秒文本 + 模型 +
  **数组 ordinal**（含非 assistant 元素的原始下标）+ in/out/cr/cc/extra 六分量）；跨文件 **last-wins**
  （Dictionary 索引器赋值，蓝本 HashMap insert 同口径）——与 Qwen/Kimi first-wins 相反；session id =
  {channel}/{project}/{chat 目录名}（纯路径后缀函数，channel 缺失回退 manicode、其余回退 unknown）。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎；extra 并入 OutputTokens（仓库口径，与蓝本
  calculate_codebuff_cost 的计价式 output+extra **同值**，成本口径无损，仅 CLI 显示单列 extra 的差异
  fail-loud，见偏差②）；credits 字段桌面统一记录无对应字段不采集（仅参与 has_signal 门槛，见偏差③）。
- **诊断口径**（仓库 I-004 显式化，Amp 单文件同款）：LinesSkipped = 非对象消息元素 / 非 assistant 消息 /
  无信号 assistant 消息（含打不开的文件）；MalformedLines = 坏 JSON / 顶层非数组文件；InvalidRecords =
  无对应场景恒 0（时间戳有 mtime/纪元兜底，无结构性拒绝面）；DuplicateRecords = dedup key 折叠数
  （last-wins）；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`ICodebuffAdapter` 单例 + LocalIngestionDataSource 第十五路 concat；适配器实例无扫描
  状态（线程安全），并发由 LocalIngestionDataSource 信号量串行化。
- **定价**：无需新增快照条目（claude-sonnet-4-20250514 家族命中 anthropic/claude-sonnet-4 同价
  3/15/0.3/3.75；openai/gpt-5 已有）。注意 openai/gpt-5 的 cache_write：快照 1.5625、蓝本
  models-dev-pricing.json 无 cache_write（计 0）——fixture 规避 gpt-5 带 cache_creation 的行（cache_read
  不受影响），真实数据该场景成本 fail-loud（登记偏差④，I-024 观察项口径）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **provider 前缀候选计价与缺失定价报告未端到端接入**：蓝本 calculate_codebuff_cost 先试裸模型名、
  未命中且 provider≠unknown 时重试 "{provider}/{model}"；missing_codebuff_pricing 按候选集报告缺失。
  自研 record.Model = 原始模型名，引擎按名匹配（含 provider 前缀剥离与家族兜底，无 provider **前缀
  添加**重试）——裸名未被快照收录时成本记 0（fail-loud）。I-024 已登记同类共性欠账，本卡不做
  resolver 接线；fixture 用裸 anthropic 日期后缀名（家族命中）与 openai/ 前缀名（直接命中）规避。
- ② **extra（total 缺口）并入 OutputTokens**：CLI daily 的 outputTokens 不含 extra（单列）、totalTokens
  含之；蓝本计价式 output+extra 与自研并桶后计价同值——成本无损，仅 output 分量显示差异 fail-loud；
  fixture 的 total 兜底行走 output 补齐分支（extra 恒 0），该语义由单测覆盖。
- ③ **credits 不采集**：蓝本 LoadedEntry.credits（消息级/usage 级 credits，report 展示用）桌面统一记录
  无对应字段——仅保留其 has_signal 门槛语义（credits&gt;0 的 assistant 消息产出 token 全零记录）；
  CLI daily JSON 的 credits 信息字段不在对账比较面（DTO 忽略）。
- ④ **快照 gpt-5 cache_write 差异**：快照 openai/gpt-5 带 cache_write 1.5625，蓝本内嵌 models-dev
  数据无 cache_write（cache_creation 计 0）——gpt-5 + cache_creation 的真实数据成本两侧不同
  （fail-loud）；fixture 规避（gpt-5 行只用 cache_read），登记 I-024 观察项口径。
- ⑤ **行级拒绝/空产出显式计数**（蓝本静默 continue / 整文件静默空）：非对象元素、非 assistant、无信号
  行计 LinesSkipped；坏 JSON / 顶层非数组计 MalformedLines；读不开的文件计 LinesSkipped（I-004 显式化
  口径，不改变记录内容）。
- ⑥ **路径去重为文本级**（蓝本 PathBuf 组件级相等）：含 "." / 重复分隔符的等价路径在蓝本下去重、自研
  侧可能重复枚举——重复文件解析出相同 dedup_key，由 last-wins 折叠兜底，token 口径不变（仅多读文件）。
- ⑦ **无 since/until 窗口 / Display 模式 / credits 报表不复现**：桌面适配器统一全量出账（I-008 偏差⑦
  同款架构）；report.rs 的 credits 汇总（蓝本专有 report 形状）桌面无对应报表面。
- ⑧ **u64 → long 边界**：读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥同款）；u64
  wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑨ **timestamp 理论边界**：jiff 支持 year 0 而 DateTimeOffset 下限 year 1——数字毫秒/秒与 RFC3339
  解析的越界输入回退 None（走后续回退链），蓝本侧 from_millis 同域受限；真实日志不出现。
- ⑩ **configuredDataDir 为仓库增强**（非蓝本）：测试钉定入口，语义同 Amp/Kimi（非空即覆盖 env 与默认）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **560 通过 / 18 跳过**
（I-013 收口基线 523/17 + 新增 35 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 CODEBUFF_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 /
TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（三卡均可收口、代码无缺陷；本卡修复 P2-1 / P3-typo 两项登记）：

- **[P2-1] I-024 卡补登记三源欠账**：I-024 卡 Background 增补"三源欠账"段（Codebuff provider 前缀
  候选重试 + missing_pricing 候选集报告 + **CB-2** 快照 openai/gpt-5 cache_write 差异）、Dependencies
  追加 I-018/I-019/I-022——本卡偏差①此前声称"I-024 已登记同类共性欠账"而 I-024 scope 未含本卡，
  现已闭环。
- **[P3] dedup_key 计数分量表述更正**：Dev Log/Subtasks "六计数分量" → **5 个**（in/out/cr/cc/extra，
  蓝本 dedup_key 同为 5）；CodebuffChatParser 文档注释与 CodebuffAdapterTests 注释同步更正（纯注释，
  无代码变更）。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **623 通过 / 20 跳过**
  （数字随 I-019/I-022 收口增长，本卡相关项不回归——codebuff fixture 对账 2/2 与 CLI golden 逐位
  一致；门控以 CODEBUFF_DATA_DIR 钉 fixture 复跑通过 1/1 Passed）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| CB-1 | P3 | 路径去重为 Ordinal 文本级，蓝本 PathBuf 为组件级相等——含 "./" 段或重复分隔符的等价根目录在自研侧可能重复枚举（重复文件解析出相同 dedup_key，由 last-wins 折叠兜底，token 口径不变） | deferred | 理论角落（env 传入 "./" 段路径的现实概率极低）；随真实数据对账回归再对齐 |
| CB-2 | P3 | 快照 openai/gpt-5 带 cache_write 1.5625，蓝本内嵌 models-dev 数据无 cache_write（计 0）——gpt-5 + cache_creation 的真实数据成本两侧不同（fail-loud） | deferred | 定价快照版本欠账（I-024 观察项口径）；随 I-024 快照刷新一并处理 |

## Verification

- Related files: `src/Tokenmetry.Infrastructure/Codebuff/CodebuffLogLocator.cs`、
  `src/Tokenmetry.Infrastructure/Codebuff/CodebuffChatParser.cs`、
  `src/Tokenmetry.Infrastructure/Codebuff/CodebuffAdapter.cs`、
  `src/Tokenmetry.Core/Abstractions/ICodebuffAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/Codebuff/CodebuffAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CodebuffFixtureReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/codebuff/`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/codebuff-fixture-daily-v20.0.29.json`、
  `tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`（全绿）；fixture 对账
  `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Codebuff"`；门控复跑
  `CODEBUFF_DATA_DIR=$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/codebuff TOKENMETRY_RECONCILE_REAL=1
  dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_codebuff"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 **560/18**，
  2026-10-12 独立审查修复复核后随批次收口数字 **623/20**，本卡项不回归）。
