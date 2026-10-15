---
id: I-014
title: Amp 适配器
type: feature
priority: P3
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Amp 线程数据（AMP_DATA_DIR 或 ~/.local/share/amp/threads/T-*.json），产出 agent=Amp 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/amp/。单层 JSON 文件（非 JSONL），解析后接入统一记录模型。本机暂无数据。

## Acceptance Criteria

- [x] 线程文件发现与 JSON 解析；token/模型口径映射对齐蓝本
  - ✅ 2026-10-11：`AmpLogLocator` 逐条移植 paths.rs——AMP_DATA_DIR **已设置即只用配置**（含空串：
    Rust env::var 对空串返回 Ok，配置生效、不回退默认），逗号分隔/去空白/仅存目录/按序去重；默认
    ~/.local/share/amp（目录必须存在）；configuredDataDir 参数为仓库共有测试钉定增强（Copilot 同款）；
    线程文件 = &lt;根&gt;/threads 子树递归 .json（单层 JSON 非 JSONL）；解析 lenient 口径：顶层 id
    （去空白非空）、messages（lenient_vec 非对象元素跳过）、usageLedger（lenient_object）；
    **usageLedger.events 优先**（events 数组存在即走账本路径——含空数组不回退；缺失/非数组才回退
    messages）；账本事件 toMessageId（仅整数）查 assistant 消息缓存四桶 attach；messages 路径仅
    role=="assistant"（原样比较）、usage 级 timestamp/model 缺省回退消息级；total 兜底
    （apply_total_token_fallback 共享件，I-010 移植复用）；整数 lenient_u64 口径（浮点/字符串 → 0，
    区别于 Gemini 的浮点截断）
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 17 项单测（临时目录合成数据，fixtures 全合成）+ 合成 fixture（threads/T-*.json）
    入仓 + CLI golden 录制逐位一致 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip，
    门控以 AMP_DATA_DIR 钉 fixture 复跑通过 1/1）；`IAmpAdapter` + LocalIngestionDataSource
    第十一路 concat + App DI 双注册

## Subtasks

- [x] `AmpLogLocator`（paths.rs 移植）：configuredDataDir → AMP_DATA_DIR（逗号分隔、去空白、仅存目录、
  按序去重、**设置即不回退默认**）→ ~/.local/share/amp（不存在即空）；线程文件 = threads/ 子树 .json
- [x] `AmpThreadParser`（parser.rs 移植）：lenient 顶层结构（id/messages/usageLedger）；账本路径
  （timestamp 严格 RFC3339 + model + tokens 的 Option&lt;Value&gt; 关——缺失/null 跳过、标量过门四桶 0 且
  缓存照常 attach、toMessageId 缓存 attach、total 兜底、全零跳过）
  与 messages 路径（role 原样比较、usage/message 级字段回退链、四桶 + totalTokens、全零跳过）；
  严格 RFC3339 解析复用共享 `StrictRfc3339Timestamp`（I-010 移植）
- [x] `AmpAdapter`（loader.rs 移植）：发现 → 逐文件解析 → 记录时间戳稳定排序 → 统一记录与诊断；
  不走共享 IncrementalJsonlScanner（单层 JSON + 蓝本无消息级去重语义），每次全量重读（I-010 同款判断）
- [x] 单元测试 ×22（`AmpAdapterTests`：total 兜底（账本/messages 两路径）、toMessageId 缓存 attach、
  账本优先于 messages、events 空数组不回退、缺 model/tokens 与坏时间戳事件、tokens null 跳过、
  tokens 标量过门四桶 0 + 缓存照常 attach、messages usage null 跳过、usage 标量回退消息级字段、
  缓存源 usage null 按 (0,0) 登记、messages 读取（蓝本旗舰
  场景）、usage→message 字段回退、全零跳过、空账本对象/非对象账本回退 + 非对象消息元素、坏文件/缺 id
  计数、跨文件排序、快照定价、threads 子树发现（含嵌套）、env 设置即不回退（含空串）、缺省目录无数据）
- [x] 对账：合成 fixture（账本优先 + toMessageId attach + total 兜底 + messages 回退 + user 行 +
  缺 model 事件）入仓 + CLI golden 录制（fixtures/golden/amp-fixture-daily-v20.0.29.json）+
  fixture 对账测试 ×2 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasAmpData 数据存在性 Skip）
- [x] 管道接线：`IAmpAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十一路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无新增段**——fixture 的 gpt-5 / claude-haiku-4-5-20251001 均可命中现有
  anthropic/openai 段（后者经快照家族模式 claude-haiku-4-5 匹配，与 CLI 的 models.dev
  anthropic/claude-haiku-4-5-20251001 条目价格逐位同：1/5/0.1/1.25）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 threads/T-*.json（账本 total 兜底、toMessageId 缓存 attach、账本优先、events 空数组、
   缺 model/tokens/坏时间戳、tokens null/标量、usage null/标量、缓存源 usage null、messages 双路径字段
   回退、全零、空账本对象/非对象账本、非对象消息元素、坏文件/缺 id、env 空串），运行
   `AmpAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 账本 events 数组存在即走账本（空数组产出 0 条不回退）；tokens 缺失/null（serde Option&lt;Value&gt;
  null → None）跳过、标量过门且四桶 0 但 toMessageId 缓存照常 attach（可产出仅缓存记录）；messages
  路径仅 role=="assistant"（原样比较），usage 缺失/null 跳过、标量回退消息级 timestamp/model、缓存源
  usage null 按 (0,0) 登记；timestamp/model 按 usage → message 回退；total 兜底：output 为 0 补
  output（两路径同）；四桶与 extra 全零跳过；坏 JSON 计 malformed（仓库口径显式计数，蓝本静默）、
  缺 thread id 计 invalid、坏时间戳计 invalid、缺 model/tokens 与 user 行计 skipped。

Result: 通过（Infrastructure 22/22：Falls_back_to_total_tokens_when_amp_parts_are_missing、
Ledger_events_attach_cache_from_assistant_messages_by_to_message_id、
Ledger_events_take_precedence_over_messages_usage、
Ledger_with_present_but_empty_events_does_not_fall_back_to_messages、
Ledger_events_without_model_or_tokens_are_skipped、
Ledger_tokens_null_skips_event、
Ledger_scalar_tokens_still_attaches_cache、
Message_usage_null_skips_message、
Message_usage_scalar_falls_back_to_message_level_fields、
Cache_source_message_with_null_usage_registers_zero_entry、
Reads_usage_from_messages_when_ledger_is_missing、
Message_usage_falls_back_to_message_level_timestamp_and_model、
Falls_back_to_total_tokens_in_messages_path、Skips_messages_with_no_usage_tokens、
Empty_usage_ledger_falls_back_to_message_usage、
Malformed_thread_files_are_counted_and_do_not_stop_the_scan、
Records_are_sorted_by_timestamp_across_thread_files、Prices_records_via_builtin_snapshot、
Collects_json_thread_files_under_threads_subdirectory_only、
Amp_data_dir_environment_variable_is_respected_without_default_fallback、
Set_but_empty_environment_variable_yields_no_data_without_fallback、
Missing_configured_directory_yields_no_data）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/amp/threads/`（T-thread-a.json：账本路径，
   event-a/event-b（toMessageId=11 attach 986/11372）/event-c（total 兜底）/event-d（缺 model 跳过）+
   缓存源 assistant 消息；T-thread-b.json：messages 路径，两条 haiku 全四桶 + 一条 totalTokens 兜底 +
   user 行）。
2. 录制 golden：`AMP_DATA_DIR=$PWD/$FX/amp npx --yes ccusage@20.0.29 amp daily --json --offline
   --timezone UTC > $FX/golden/amp-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateAmpAsync(AmpRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同——含 gpt-5 缓存写入
  986 tokens 按 cache_write 1.5625（models.dev 缺 cache_write 时 input×1.25 默认）计价，两侧同式）；
  模型清单两侧同；自研定价未命中清单为空；诊断 status=Ok、skipped=2（user 行 + 缺 model 事件）、
  records=6。

Result: 通过——两侧全量 **input=1,815 / output=1,365 / cacheCreation=1,972 / cacheRead=34,744 /
total=39,896 tokens，totalCost=$0.021346825** 逐位一致（01-19：gpt-5 1,800/300/986/11,372 cost
0.008212125、claude-haiku-4-5-20251001 15/220/986/23,372 cost 0.0046847；01-20：gpt-5 0/845/0/0 cost
0.00845）；诊断 status=Ok、skipped=2、records=6、UnpricedModels=∅。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_amp"`。
2. 数据存在性门控：定位器（AMP_DATA_DIR / ~/.local/share/amp）threads/ 下有 .json 才运行（与 CLI 子进程
   同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 amp daily --json --offline --timezone UTC`（golden）
   vs `AggregateAmpAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（extra 并入 output 的显示差异 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Amp 数据 → 测试按门控 Skip**（与 gemini 等门控同态）。门控链路本身已在
本机验证：以 `AMP_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与自研全链路对账
**通过**（`Real_amp_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行该门控即得真实数字。
同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/amp/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 amp/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 AmpLogLocator / AmpThreadParser / AmpAdapter 文件头；
fixtures 全部合成，无真实数据入仓）：

- **共享引擎适用性判断**：单层 JSON 文件（非 JSONL），且蓝本 amp 无任何消息级去重语义（entries 直接
  拼接后按时间稳定排序）——`IncrementalJsonlScanner` 的消息级去重器与 mtime 增量记账均无对应语义锚点，
  不硬套；按 I-010 同款"每次全量重读"口径实现（OpenCode/SQLite 系同口径）。蓝本通用件复用 I-010 移植的
  共享原语：`JsonSource/SourceFileCollector`（threads 子树 .json 递归收集）、`JsonSource/EnvDataDirResolver`
  （AMP_DATA_DIR 设置即不回退语义）、`JsonSource/StrictRfc3339Timestamp`（事件/消息时间戳严格解析）、
  `JsonSource/TokenUsageFallback` + `RawTokenUsage`（apply_total_token_fallback）。
- **账本优先语义**（蓝本 read_thread_file 的 Some/None 角落逐条对齐）：usageLedger 为对象且 events 为
  数组即走账本路径——**空数组也走账本路径、产出 0 条、不回退 messages**（lenient_array 的 Some([]) 与
  None 之别）；usageLedger 非对象 / events 缺失或非数组才回退 messages（蓝本
  empty_usage_ledger_falls_back_to_message_usage 与 malformed_message_element_does_not_drop_the_thread
  两内联测试场景）。
- **账本事件四关**（parse_ledger_events 顺序）：timestamp 文本（去空白非空）→ 严格 RFC3339 可解析 →
  model（去空白非空）→ tokens 过 Option&lt;Value&gt; 关（缺失/null 均跳过，标量过门且字段读取全 0——
  toMessageId 缓存照常 attach）；cache_creation/cache_read 由
  toMessageId（整数）查 assistant 消息（cache_tokens_by_message_id：messageId 仅整数、usage 四桶整数
  口径读取，usage 缺失/null/非对象按 (0,0) 登记）attach；total 兜底与全零跳过同蓝本。
- **messages 路径**（parse_message_usage）：role 原样比较 "assistant"（不 trim）；usage 为
  Option&lt;Value&gt;——缺失/null 跳过，标量过门且 Value::get 全 None（timestamp/model 回退消息级字段、
  四桶 0，随后按全零口径跳过）；timestamp = usage.timestamp ?? message.timestamp、model =
  usage.model ?? message.model（均去空白非空）；四桶 inputTokens/outputTokens/cacheCreationInputTokens/
  cacheReadInputTokens（整数口径）+ totalTokens；total 兜底与全零跳过同蓝本。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎（蓝本 calculate_cost 裸模型名走 PricingMap
  别名/模糊回退）；extra 并入 OutputTokens（蓝本 cost_usage 同式）；credits 字段（event.credits /
  usage.credits）桌面统一记录无对应字段，不采集。
- **诊断口径**（仓库 I-004 显式化）：FilesDiscovered = threads 子树发现的 .json 数、FilesRead = 打开且
  解析完成的文件数（打不开计 LinesSkipped）、LinesSkipped = user/非 assistant 消息 + 缺 model/tokens
  与全零的事件/消息、MalformedLines = 坏 JSON 文件（含顶层数组）、InvalidRecords = 有效 JSON 缺
  thread id + 事件/消息缺或不可解析时间戳（Copilot 同款）、DuplicateRecords = 无对应场景恒 0；状态
  Ok/Partial/NoData 同 I-006。
- **管道接线**：`IAmpAdapter` 单例 + LocalIngestionDataSource 第十一路 concat；适配器无扫描状态。
- **定价快照**：无新增段——gpt-5 命中 openai 段；claude-haiku-4-5-20251001 经快照家族模式
  （claude-haiku-4-5）匹配，价格与 CLI 的 models.dev anthropic/claude-haiku-4-5-20251001 条目逐位同
  （1/5/0.1/1.25，golden 成本逐位一致验证）；gpt-5 缓存写入价两侧同走 models.dev 缺省规则
  （cache_write 缺失 → input×1.25 = 1.5625），golden 0.008212125 逐位验证。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入**：蓝本 calculate_cost 裸模型名走 PricingMap::find（模型别名解析 + 模糊
  匹配 + models.dev 回退）。自研 record.Model = 裸模型名，引擎按模型名 + 自身候选机制匹配（精确 +
  快照家族模式）。**对账后果**：fixture 模型均可命中（golden 成本逐位一致）；依赖蓝本别名/模糊回退的
  模型自研侧可能记 0（fail-loud）。I-024 已登记同类共性欠账，本卡按同口径不做 resolver 接线。
- ② **extra（total 缺口）并入 OutputTokens 与 credits 不采集**：CLI daily 的 outputTokens 不含 extra、
  totalTokens 含之——并入 output 使 total 与 cost 一致（蓝本 cost_usage 同式）；total 缺口且 output
  已知的真实数据在 output 分量 fail-loud（fixture 规避：total 兜底行 output 均为 0，由单测覆盖
  output 已知分支）。credits（账本 event.credits / usage.credits）在蓝本 LoadedEntry 单列，桌面统一
  记录无该字段，不采集（对 daily 聚合无影响）。
- ③ **行级拒绝/空产出显式计数**（蓝本静默 continue/Ok(vec![])）：坏 JSON 文件计 MalformedLines、缺
  thread id 与不可解析时间戳计 InvalidRecords、缺 model/tokens 与非 assistant 行计 LinesSkipped——
  诊断不改变记录内容。
- ④ **文件枚举顺序确定化**：蓝本 loader 不排序线程文件（read_dir OS 序），桌面按完整路径排序后解析
  （同刻记录的列表顺序确定；聚合结果不受影响）。
- ⑤ **home 缺失降级**：蓝本 paths() 在无 AMP_DATA_DIR 且无 home 时返回 Err（整次加载失败）；桌面
  按无数据处理（Roots 空，与其他源一致）。home 解析沿用共享 JsonlHomePath 的 USERPROFILE 优先
  （蓝本 HOME 优先，I-006 系已登记差异同款）；configuredDataDir 为仓库增强。
- ⑥ **lenient 整数边界：无差异**（2026-10-12 审查更正：初版误记"桌面 TryGetInt64 对整数值浮点（11.0）
  同样生效"——实测 JsonElement.TryGetUInt64 / TryGetInt64 对 11.0 均返回 false，与蓝本 serde
  json_value_u64 / Value::as_i64 的整数限定**完全一致**，该偏差不存在）。保留的真实边界仅剩：u64 溢出
  钳到 long.MaxValue（saturating，I-017 偏差⑥同款）。
- ⑦ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构）；
  蓝本 Display 模式桌面无对应（Kilo 偏差③同款）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **457 通过 / 14 跳过**
（I-010 收口基线 438/13 + 新增 17 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 AMP_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 request-changes（本卡 1 项 P1 必修；Gemini 卡另有 P2、Droid 卡可收口）。审查员以
ccusage 20.0.29 原生二进制双向实证（5 个最小 fixture 全部 `InvalidOperationException` 穿透
`AmpAdapter.ScanAsync` → `LocalIngestionDataSource.GetDailyUsageAsync`，一次刷新整体失败），修复如下：

- **[P1] 非对象 tokens/usage 载荷直接崩溃**：`LenientJson.GetU64/GetNonEmptyString` 的
  `parent.TryGetProperty` 要求 parent 为 Object，而账本 `tokens` 与 messages `usage` 元素未做
  ValueKind 守卫。修复三处并**按蓝本 serde Option&lt;Value&gt; 区分 null 与标量**：
  - ① 账本 `"tokens": null` → serde null → None → 事件跳过（初版代码还会崩）；
  - ② 账本 `"tokens"` 为标量 → Some(标量) 过门：字段读取全 None → 四桶 0，**toMessageId 缓存照常
    attach**（全 0 + 缓存可产出记录——审查员特别提醒不得做成"不产出"；已用 CLI 复核：该形态
    golden 产出 0/0/986/11372 的仅缓存记录，与自研单测逐位同）；
  - ③ messages `"usage": null` → None → 消息跳过；
  - ④ messages `"usage"` 为标量 → 过门且 Value::get 全 None：timestamp/model 回退消息级字段、
    四桶 0（随后按全零口径跳过）；
  - ⑤ 缓存源 assistant 消息 `"usage": null` → 按 (0,0) 登记该 messageId（缓存映射不缺失，蓝本同）。
- 新增单测 ×5（与审查员 ①-⑤ 逐一对齐）：Ledger_tokens_null_skips_event、
  Ledger_scalar_tokens_still_attaches_cache、Message_usage_null_skips_message、
  Message_usage_scalar_falls_back_to_message_level_fields、
  Cache_source_message_with_null_usage_registers_zero_entry。TC-001 中"usage 载荷读取（非对象载荷按键
  读取失败同缺失）"的表述同步更正（初版表述与代码均不符蓝本 Option 语义）。
- **[P3a] 偏差⑥更正**："桌面 TryGetInt64 对整数值浮点（11.0）同样生效"与事实相反——实测
  TryGetUInt64/TryGetInt64 对 11.0 均返回 false，与蓝本 as_u64/as_i64 完全一致，该偏差不存在，
  偏差⑥改写为"无差异"（仅保留 u64→long 钳制边界）。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **481 通过 / 15 跳过**
  （本卡收口 457/14 → 审查修复新增 5 项单测 → 全仓 481/15）；fixture 对账 2/2 与 CLI golden 逐位一致
  不回归；门控以 AMP_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）；`TOKENMETRY_RECONCILE_REAL=1` 全量门控
  回归 37 通过 / 9 跳过 / 0 失败（codex/zcode/dsh 真实对账 Passed，gemini/amp/droid 等九源按数据
  存在性 Skip）；审查员最小 fixture 形态（"tokens":null / "tokens":5 / "usage":null / "usage":5）
  单测实证不崩溃且语义对齐蓝本。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/Amp/AmpLogLocator.cs`、
  `src/Tokenmetry.Infrastructure/Amp/AmpThreadParser.cs`、`src/Tokenmetry.Infrastructure/Amp/AmpAdapter.cs`、
  `src/Tokenmetry.Core/Abstractions/IAmpAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/Amp/AmpAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/AmpFixtureReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/amp/`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/amp-fixture-daily-v20.0.29.json`、
  `tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`（全绿）；fixture 对账
  `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Amp"`；门控复跑
  `AMP_DATA_DIR=$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/amp TOKENMETRY_RECONCILE_REAL=1
  dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_amp"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 457/14，
  2026-10-12 审查修复后全仓 **481/15**）。
