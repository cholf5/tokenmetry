---
id: I-010
title: Gemini CLI 适配器
type: feature
priority: P2
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Gemini CLI 会话数据（GEMINI_DATA_DIR 或 ~/.gemini/tmp/**/chats/*.json|jsonl），产出 agent=Gemini CLI 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/gemini/。JSON/JSONL 混合格式；注意 antigravity 的 SQLite 数据归 I-021，本卡只做 chats 文件。本机暂无数据，fixture 依蓝本合成。

## Acceptance Criteria

- [x] 目录发现与 .json/.jsonl 双格式解析
  - ✅ 2026-10-11：`GeminiLogLocator` 逐条移植 paths.rs——GEMINI_DATA_DIR **已设置即只用配置**（含空串：
    Rust env::var 对空串返回 Ok，配置生效、不回退默认），逗号分隔/去空白/仅存目录/按序去重；默认
    ~/.gemini/tmp（目录必须存在）；configuredDataDir 参数为仓库共有测试钉定增强（Copilot 同款）；
    根树下递归收集 .json 与 .jsonl（扩展名精确小写匹配）、合并后按完整路径排序去重（蓝本
    discover_log_files 同口径）。解析按扩展名分流：.jsonl 逐行、其余按整文件 JSON
    （蓝本 `.jsonl` 分支同口径，"JSONL" 大写走整文件分支）
- [x] token/模型口径映射对齐蓝本
  - ✅ `parse_tokens` 多键别名（input/prompt/input_tokens/prompt_tokens 等、total 键存在即优先）+
    value_u64 浮点截断/负数归 0（区别于 Amp/Droid 的整数 lenient_u64）；input 归一化双口径——直接事件
    normalize_session_input（total 自洽性判别 cached 是否已含于 input，命中才拆 cache_read）、stats 事件
    恒 subtract_cached_overlap（input−min(input,cached)）；tool 并入 input；apply_total_token_fallback
    移植（output 为 0 补 output、否则缺口并入 thoughts/extra）；四桶与 extra 全零的事件不产出；
    type 判别原样字符串不 trim（" gemini " 不命中）；JSONL sessionId/model 跨行滚动、同 id 事件就地替换
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 19 项单测（临时目录合成数据，fixtures 全合成）+ 合成 fixture（.json/.jsonl 四文件）
    入仓 + CLI golden 录制逐位一致 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（数据存在性 Skip，
    门控以 GEMINI_DATA_DIR 钉 fixture 复跑通过 1/1）；`IGeminiCliAdapter` + LocalIngestionDataSource
    第十路 concat + App DI 双注册

## Subtasks

- [x] `GeminiLogLocator`（paths.rs 移植）：configuredDataDir → GEMINI_DATA_DIR（逗号分隔、去空白、仅存
  目录、按序去重、**设置即不回退默认**，空串生效为空列表）→ ~/.gemini/tmp（不存在即空）；递归收集
  .json/.jsonl 双扩展名、路径排序去重；共享 `EnvDataDirResolver` / `SourceFileCollector`（本卡首建）
- [x] `GeminiLogParser`（parser.rs 移植）：GeminiRecord 信封（JsonElement 导航视图）；整文件三分支
  （messages 数组 → type=="gemini" 消息 + 会话级 startTime/lastUpdated/mtime 回退；顶层 type=="gemini"
  → 单条直接事件 + mtime 回退；stats → models 明细优先、无产出回退整块解析、模型缺省 unknown、
  result.stats 嵌套回退）；JSONL 逐行（sessionId/model 滚动、id 就地替换、stats 用滚动 current_model）；
  严格 RFC3339 解析（共享 `StrictRfc3339Timestamp`，parse_ts_timestamp 逐条移植：长度 20/24/25/29、
  毫秒恰 3 位、偏移 ±23:59）；apply_total_token_fallback 移植（共享 `RawTokenUsage`/`TokenUsageFallback`）
- [x] `GeminiCliAdapter`（loader.rs 移植）：发现 → 逐文件解析 → 事件时间戳稳定排序 → 统一记录与诊断；
  **不走共享 IncrementalJsonlScanner**（适用性判断见 Dev Log），每次全量重读（I-009 SQLite 系同口径）
- [x] 单元测试 ×22（`GeminiCliAdapterTests`：.json/.jsonl 发现、env 设置即不回退（含空串）、antigravity
  不串扰、cached 拆分旗舰场景（loader 内联测试）、sessionId/model 滚动、id 就地替换、total 兜底两分支、
  全零丢弃、type 判别（untrimmed/非字符串）、messages 数组路径、stats models 明细、无 models 回退
  unknown、result.stats 嵌套、stats:null 回退 result.stats / result.stats 缺失、stats 与 result.stats
  皆无（含 result.stats:null）不产出、坏文件/坏行计数、跨文件排序、快照定价、缺省目录无数据）
- [x] 对账：合成 fixture（双格式 + cached 拆分 + id 替换 + stats 双路径 + user/note 行 + unknown 模型）
  入仓 + CLI golden 录制（fixtures/golden/gemini-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasGeminiData 数据存在性 Skip）
- [x] 管道接线：`IGeminiCliAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**新增 google 段**（gemini-2.5-pro / gemini-2.5-flash，取自蓝本
  rust/crates/ccusage-core/src/models-dev-pricing.json 的 google/&lt;model&gt; 条目：1.25/10/0.125/0.375 与
  0.3/2.5/0.03/0.083333）——快照原无 google provider；gemini 的 cost_usage 恒 cache_creation=0，
  cache_write 价不参与，与 CLI（首个命中候选 google/ 前缀）逐位同价

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 .json/.jsonl（cached 拆分含 tool、total 兜底两分支、全零丢弃、type 带空白/数字、
   messages 数组路径、stats models 明细与整块回退、result.stats 嵌套、id 就地替换、sessionId/model 滚动、
   坏文件/坏行、env 空串），运行 `GeminiCliAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 双扩展名发现、路径排序去重；env 已设置即只用配置（空串生效为空、不回退默认）；cached 按 total 自洽性
  拆分（total==含 cached 四桶和才拆，tool 并入 input）；stats 路径恒 overlap 扣减且 total 双计 cached 时
  缺口并入 extra（桌面并入 OutputTokens）；output 为 0 时 total 缺口补 output；" gemini " 不命中、数字
  type 走 stats；坏 JSON 计 malformed（仓库口径显式计数，蓝本静默）；同 id 事件替换计 DuplicateRecords。

Result: 通过（Infrastructure 22/22：Discovers_json_and_jsonl_logs_only、
Gemini_data_dir_environment_variable_is_respected_without_default_fallback、
Set_but_empty_environment_variable_yields_no_data_without_fallback、
Antigravity_data_dir_does_not_leak_into_gemini_discovery、
Loads_jsonl_token_events_and_separates_cached_input、Jsonl_session_and_model_carry_over_across_lines、
Jsonl_event_id_is_replaced_in_place_by_later_occurrence、
Falls_back_to_total_tokens_when_gemini_parts_are_missing、Total_fallback_keeps_extra_when_output_is_known、
All_zero_events_are_dropped、Type_discriminator_is_untrimmed_and_tolerates_non_strings、
Parses_messages_array_with_session_level_timestamp_fallback、
Parses_stats_models_breakdown_with_overlap_subtraction、
Stats_without_models_falls_back_to_whole_block_with_unknown_model、
Result_nested_stats_is_used_when_top_level_missing、
Null_top_level_stats_falls_back_to_result_stats、
Missing_stats_with_result_stats_uses_nested_block、
Neither_stats_nor_result_stats_yields_no_events、
Malformed_files_and_lines_are_counted_and_do_not_stop_the_scan、
Records_are_sorted_by_timestamp_across_files、Prices_records_via_builtin_snapshot_google_section、
Missing_configured_directory_yields_no_data）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/gemini/`（合成数据，覆盖：.json/.jsonl 双格式、
   messages 数组路径（user 行跳过、cached 拆分 + tool 并入 input）、JSONL meta 行 + 同 id 事件替换 +
   非 gemini 行的 stats、stats models 明细（overlap 扣减）、无 models 整块回退（unknown）、note 行跳过）。
2. 录制 golden：`GEMINI_DATA_DIR=$PWD/$FX/gemini npx --yes ccusage@20.0.29 gemini daily --json --offline
   --timezone UTC > $FX/golden/gemini-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateGeminiAsync(GeminiRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型清单两侧同
  （含 unknown：CLI missingPricing 记 0、自研 Missing 聚合计 0）；自研定价未命中清单 = [unknown]；
  诊断 status=Ok、malformed=0、duplicate=1（id 替换）、records=7。

Result: 通过——两侧全量 **input=4,157 / output=285 / cacheCreation=0 / cacheRead=1,400 / total=5,842
tokens，totalCost=$0.00615925** 逐位一致（05-17：pro 1,407/80/0/800 cost 0.00265875、flash 150/10/0/0
cost 0.00007；05-18：pro 1,600/100/0/400 cost 0.00305、flash 800/55/0/100 cost 0.0003805、unknown
200/40/0/100 cost 0 missingPricing）；诊断 status=Ok、duplicate=1、records=7、UnpricedModels=[unknown]。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_gemini"`。
2. 数据存在性门控：定位器（GEMINI_DATA_DIR / ~/.gemini/tmp）下发现 .json/.jsonl 才运行（与 CLI 子进程
   同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 gemini daily --json --offline --timezone UTC`（golden）
   vs `AggregateGeminiAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（thoughts/extra 并入 output 的显示差异 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Gemini CLI 数据 → 测试按门控 Skip**（与 claude/copilot/opencode/goose/
hermes/kilo 门控同态）。门控链路本身已在本机验证：以 `GEMINI_DATA_DIR=<TC-002 fixture>` 开启门控运行，
真实调用 CLI 生成 golden 与自研全链路对账**通过**（`Real_gemini_data_reconciles_with_cli` Passed，1/1）。
有真实数据的机器上运行该门控即得真实数字。同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/gemini/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 gemini/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 GeminiLogLocator / GeminiLogParser / GeminiCliAdapter 文件头；
fixtures 全部合成，无真实数据入仓）：

- **共享引擎适用性判断**：本源为"整文件 JSON + 行式 JSONL 混合"，未硬套共享 `IncrementalJsonlScanner`——
  引擎的消息级去重器按 MessageId **跨文件**折叠，与蓝本"事件 id 仅在单 .jsonl 文件内就地替换、跨文件
  不去重"冲突（若置 MessageId 又会被误并）；Copilot 先例已证明"MessageId 置 null 直通"时引擎仅剩
  文件级 mtime 增量记账价值，对聊天记录收益有限。按 OpenCode/SQLite 系"每次全量重读"口径实现，
  重读幂等由全量扫描保证。蓝本通用件改为独立移植：`collect_files_with_extension` → 共享
  `JsonSource/SourceFileCollector`；env 目录列表语义 → 共享 `JsonSource/EnvDataDirResolver`；
  `parse_ts_timestamp` → 共享 `JsonSource/StrictRfc3339Timestamp`（严格长度检查——缺时区/非 3 位毫秒
  一律拒绝回退 mtime，宽松的 JsonlTimestamp 不满足蓝本口径）；`apply_total_token_fallback` → 共享
  `JsonSource/RawTokenUsage` + `TokenUsageFallback`。三件共享原语由本卡首建，I-014/I-015 复用。
- **目录发现**（paths.rs 逐条）：GEMINI_DATA_DIR 已设置（含空串）即只用配置、不回退默认；默认
  ~/.gemini/tmp 目录必须存在；.json 与 .jsonl 递归收集、合并后按完整路径排序去重；configuredDataDir
  参数为仓库测试钉定增强（Copilot configuredHome 同款，非蓝本）。antigravity（ANTIGRAVITY_DATA_DIR /
  SQLite conversations）归 I-021，本卡不触碰。
- **解析分支**（parser.rs 逐条）：整文件三分支与 JSONL 逐行的优先级、sessionId/model 滚动、id 就地
  替换（计 DuplicateRecords）、会话级与文件级 mtime 回退的差别（messages 路径回退 session_timestamp、
  顶层直接事件与 stats 路径回退 file mtime）均按蓝本；stats 的 result.stats 嵌套回退、顶层 "stats":null
  按 serde Option&lt;Value&gt; 语义为 None（同缺失、回退 result.stats）而 result.stats null 为
  Value::get 的 Some(&amp;Null)（解析为空、不再二次回退）等 Option 语义角落逐一对齐
  （2026-10-12 审查更正：初版把顶层 stats null 误作"已存在"，见审查修复小节）。
- **token 口径**：value_u64 浮点截断（Gemini 独有，Amp/Droid 为整数 lenient_u64）；total 键存在即优先
  （null 不回退 total_tokens）；normalize_session_input 的 total 自洽性判别（cached>0 且
  total==含 cached 四桶和且 ≠ 加 cached 后的和才拆）；stats 恒 subtract_cached_overlap；tool 并入
  input；total 兜底（output 为 0 补 output、否则缺口入 extra）。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎；reasoning/total 余量并入 OutputTokens（蓝本
  cost_usage 的 output+reasoning 同式，总量与成本守恒）。
- **诊断口径**（仓库 I-004 显式化）：FilesDiscovered = 发现的日志文件数、FilesRead = 打开且解析完成的
  文件数（打不开计 LinesSkipped 不计 FilesRead）、LinesSkipped = 无 type/stats 的行与无产出的
  行/消息/模型条目（蓝本静默跳过处显式计数）、MalformedLines = 坏 JSON 行与整文件（含顶层数组——
  serde 结构性失败同口径）、InvalidRecords = 无对应场景恒 0、DuplicateRecords = id 就地替换数；
  状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IGeminiCliAdapter` 单例 + LocalIngestionDataSource 第十路 concat；适配器无扫描状态
  （全量重读）。
- **定价快照**：新增 google 段（gemini-2.5-pro / gemini-2.5-flash），数据取自蓝本
  rust/crates/ccusage-core/src/models-dev-pricing.json 的 `google/<model>` 条目（录制日 2026-10-11）；
  gemini cost_usage 恒 cache_creation=0，cache_write 价不参与，CLI 首个命中候选为 google/ 前缀条目，
  与快照数值逐位同；fixture 用量远低于 models.dev 分档阈值（200k），按基础档计价两侧一致。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入**：蓝本 calculate_gemini_cost 逐候选（google/、gemini/、vertex_ai/、
  openrouter/google/ 前缀 + 裸名）查 PricingMap 计价，missing_pricing_model_for_candidates 报告缺口。
  自研 record.Model = 裸模型名，引擎按模型名 + 自身候选机制匹配（google/ 前缀候选不生成）。**对账后果**：
  仅挂在限定键下的模型自研侧记 0（fail-loud）；fixture 模型裸名可命中，golden 逐位一致。I-024 已登记
  同类共性欠账（Kilo/Hermes 同款），本卡按同口径不做 resolver 接线。
- ② **reasoning（extra_total_tokens）并入 OutputTokens**：CLI daily 的 outputTokens 不含 thoughts/extra、
  totalTokens 含之——并入 output 使 total 与 cost 一致（蓝本 cost_usage 同式）。两类真实数据形态会触发
  output 分量 fail-loud：thoughts&gt;0 的事件、stats 路径 total 双计 cached（缺口并入 extra）的事件。
  fixture 规避这两类行（thoughts 恒 0、stats 条目不带 total），该语义由单测覆盖（loader 旗舰场景 +
  total 兜底 + stats total 双计三处断言）。
- ③ **行级拒绝/空产出显式计数**（蓝本静默 continue）：无 type/stats 的行、无产出的消息/模型条目计
  LinesSkipped；坏 JSON 行/文件计 MalformedLines；id 替换计 DuplicateRecords——诊断不改变记录内容。
- ④ **共享引擎不适用**（见设计决策首条）：不走 IncrementalJsonlScanner，每次全量重读；文件枚举与
  事件顺序确定化（文件按路径序、同刻稳定排序），蓝本 read_dir 枚举序为 OS 定义（聚合结果不受影响，
  仅同刻记录的列表顺序可能不同）。
- ⑤ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，I-006 系已登记差异
  同款）；configuredDataDir 为仓库增强；蓝本 paths 不做 canonicalize（无此差异）。
- ⑥ **u64 → long 边界**：token 计数读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥同款）；
  u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑦ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构），
  日期过滤/分桶由下游聚合承担；蓝本 Display 模式（成本记 0）桌面无对应模式（Kilo 偏差③同款）。
- ⑧ **timestamp 理论边界**：蓝本 jiff 支持 year 0，DateTimeOffset 下限 year 1，year 0000 输入按不可解析
  处理（回退 mtime；真实日志不出现）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **438 通过 / 13 跳过**
（I-017 收口基线 417/12 + 新增 19 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账测试
（本机 Skip））；门控以 GEMINI_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 / TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 request-changes（本卡 1 项 P2 必修 + 2 项 P3 顺手修；Droid 卡可收口、Amp 卡另有 P1）。审查员
以 ccusage 20.0.29 原生二进制双向实证，修复如下：

- **[P2] 顶层 `"stats": null` 语义与蓝本相反**：初版按"键存在即生效"处理（`stats` null → Some(Null) →
  解析为空、不再回退 result.stats）；蓝本 serde 对 Option&lt;Value&gt; 的 JSON null 反序列化为
  **None**——null 同缺失，应回退 result.stats（审查员实证 `{"stats":null,"result":{"stats":{...}}}`
  CLI 用 result.stats 产出、初版 0 条）。修复 `GeminiRecord.ResolveStats`：顶层 stats 命中但
  ValueKind==Null 继续走 result.stats 回退；result.stats 为 null 时按 Value::get 语义（Some(&Null)）
  解析为空、不再二次回退（与蓝本一致）。代码注释与上面设计决策段的错误断言同步更正。
- **[P3b] ScanAsync 目录解析双跑**：`ResolveDataDirectories` 被求值两次（自身 + CollectLogFiles 内部）。
  `GeminiLogLocator.CollectLogFiles` 增加接受已解析 roots 的重载，适配器传入 roots（configuredDataDir
  便捷重载保留，门控数据存在性探测仍用）。
- **[P3c] I-024 观察项登记**：定价快照无 tiers 分档支持（google/gemini-2.5-pro 在 models-dev 带
  &gt;200k 档 2.5/15/0.25），超阈值真实数据 fail-loud——登记入 I-024 卡 Background 观察项。
- 新增单测 ×3：Null_top_level_stats_falls_back_to_result_stats（stats:null + result.stats → 用
  result.stats）、Missing_stats_with_result_stats_uses_nested_block（stats 缺失 + result.stats）、
  Neither_stats_nor_result_stats_yields_no_events（两者皆无 / result.stats:null → 不产出；顺带把整文件
  stats 分支"无产出计 1 条 LinesSkipped"与 JSONL 行口径对齐）。
- 复核：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **476 通过 / 15 跳过**
  （本卡审查修复新增 3 项单测：438/13 → 441/13；441/13 与 I-014 审查修复叠加后全仓 **481 通过 / 15
  跳过**，见 I-014 卡审查修复小节）；fixture 对账 2/2 与 CLI golden 逐位一致不回归；门控以
  GEMINI_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）；审查员最小 fixture 形态（stats:null +
  result.stats）单测实证语义对齐蓝本。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/GeminiCli/GeminiLogLocator.cs`、
  `src/Tokenmetry.Infrastructure/GeminiCli/GeminiLogParser.cs`、`src/Tokenmetry.Infrastructure/GeminiCli/GeminiCliAdapter.cs`、
  `src/Tokenmetry.Infrastructure/JsonSource/SourceFileCollector.cs`、`src/Tokenmetry.Infrastructure/JsonSource/EnvDataDirResolver.cs`、
  `src/Tokenmetry.Infrastructure/JsonSource/StrictRfc3339Timestamp.cs`、`src/Tokenmetry.Infrastructure/JsonSource/RawTokenUsage.cs`、
  `src/Tokenmetry.Core/Abstractions/IGeminiCliAdapter.cs`、`src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`、`src/Tokenmetry.Core/Pricing/model-pricing-snapshot.json`、
  `tests/Tokenmetry.Infrastructure.Tests/GeminiCli/GeminiCliAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/GeminiFixtureReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/gemini/`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/gemini-fixture-daily-v20.0.29.json`、
  `tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`（全绿）；fixture 对账
  `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Gemini"`；门控复跑
  `GEMINI_DATA_DIR=$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/gemini TOKENMETRY_RECONCILE_REAL=1
  dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_gemini"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 438/13，
  2026-10-12 审查修复后全仓 **481/15**）。
