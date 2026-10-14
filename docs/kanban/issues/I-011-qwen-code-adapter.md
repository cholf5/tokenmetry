---
id: I-011
title: Qwen Code 适配器
type: feature
priority: P2
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Qwen Code 会话数据（QWEN_DATA_DIR 或 ~/.qwen/projects/**/*.jsonl 与 chats/*.jsonl），产出 agent=Qwen Code 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/qwen/。日志布局与 Claude Code projects/ 同构，预期可大量复用 I-001 的扫描引擎与解析骨架。本机暂无数据。

## Acceptance Criteria

- [x] 目录发现（projects/chats 双布局）与增量扫描接入
  - ✅ 2026-10-11：`QwenLogLocator` 逐条移植 paths.rs——QWEN_DATA_DIR **已设置即只用配置**（含空串，
    EnvDataDirResolver 同口径：逗号分隔/去空白/仅存目录/按序去重），默认 ~/.qwen（目录必须存在）；
    configuredDataDir 参数为仓库共有测试钉定增强（Gemini 同款）；聊天文件仅取
    projects/&lt;project&gt;/chats/&lt;file&gt;.jsonl 三段布局（蓝本 is_chat_file：相对 projects/ 恰 3 段、
    第 2 段恰为 "chats"），按完整路径排序；接入共享 IncrementalJsonlScanner 增量记账（mtime/大小）
- [x] token/模型口径映射对齐蓝本
  - ✅ 仅含 "usageMetadata" 标记的行进入解析（LinePrefilter 同口径）；type 恰为 "assistant"
    （non_empty_string trim 后比较）且 usage 块存在才产出；input←promptTokenCount、
    output←candidatesTokenCount、cache_read←cachedContentTokenCount（lenient_u64：仅整数生效）；
    apply_total_token_fallback 移植（thoughts 作 extra、total 为目标：output 为 0 补 output、
    否则缺口并入 extra）；四桶与 extra 全零不产出；严格 RFC3339（parse_ts_timestamp）可解析才采用
    行内 timestamp，否则回退文件 mtime（mtime 不可得回退当前时间——蓝本 SystemTime::now 同口径）；
    sessionId 缺失回退 &lt;project&gt;-&lt;文件名主干&gt;、模型缺失 "unknown"；project 从路径
    [projects, project, chats, file] 滑窗提取
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 19 项单测（临时目录合成数据，fixtures 全合成）+ 合成 fixture（4 个聊天文件
    + 2 个不发现诱饵）入仓 + CLI golden 录制逐位一致 + `TOKENPULSE_RECONCILE_REAL` 门控真实对账测试
    （数据存在性 Skip，门控以 QWEN_DATA_DIR 钉 fixture 复跑通过 1/1）；`IQwenCodeAdapter` +
    LocalIngestionDataSource 第十三路 concat + App DI 双注册

## Subtasks

- [x] 共享引擎去重钩子：`IncrementalJsonlScanner` 增加可选 `dedupe` 委托参数（缺省仍为
  ClaudeUsageRecordDeduper，既有四适配器零改动）——Qwen 蓝本无 message id，去重键为内容键
  （蓝本 entry_id JSON 数组 [session, timestamp, model, in, out, cache_read, extra]），first-wins
  跨文件折叠
- [x] `QwenUsageRecordDeduper`（parser.rs entry_id 移植）：JSON 数组序列化键（serde_json 转义同规则，
  冒号/逗号字段不串键）；桌面记录已把 extra 并入 OutputTokens，键取 (session, 时间戳毫秒, model,
  input, output[含 extra], cache_read)
- [x] `QwenLogLocator`（paths.rs 移植）：configuredDataDir → QWEN_DATA_DIR（已设置即不回退默认）→
  ~/.qwen；projects/ 子树递归收集 .jsonl 后按三段 chats 布局过滤；共享 `EnvDataDirResolver` /
  `SourceFileCollector` / `JsonlHomePath`
- [x] `QwenLogParser`（parser.rs 移植）：行预过滤；serde 字段语义等价移植（non_empty_string trim、
  lenient_u64、usageMetadata 严格 Option&lt;结构体&gt;：null/缺失→None、非对象→整行拒绝）；
  apply_total_token_fallback（共享 `TokenUsageFallback`）；严格 RFC3339（共享 `StrictRfc3339Timestamp`）
- [x] `QwenCodeAdapter`（loader.rs 移植）：发现 → 共享引擎增量扫描 → 内容键去重 → 时间戳稳定排序
  （蓝本 entries.sort_by_key）→ 统一记录与诊断
- [x] 单元测试 ×19（`QwenCodeAdapterTests`：旗舰场景（蓝本 loader 内联测试）、total 兜底两分支、
  全零拒绝、结构/语义拒绝计数、跨文件 first-wins 去重、重复扫描幂等、内容键冒号不串键（蓝本
  entry_id_is_unambiguous_for_colon_fields 同场景）、mtime 回退（缺失/不可解析）、sessionId/模型回退、
  三段布局过滤、env 设置即不回退（含空串）、缺省目录无数据、跨文件排序、快照定价、project 滑窗）
- [x] 对账：合成 fixture 入仓 + CLI golden 录制（fixtures/golden/qwen-fixture-daily-v20.0.29.json）+
  fixture 对账测试 ×2 + `TOKENPULSE_RECONCILE_REAL` 门控真实对账测试（HasQwenData 数据存在性 Skip）
- [x] 管道接线：`IQwenCodeAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十三路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**新增 qwen 段**（qwen3-coder-plus：input 1 / output 5，无 cache_read/cache_write 价、
  由解析器按 input×0.1/×1.25 推导），数据取自蓝本 rust/crates/ccusage-core/src/models-dev-pricing.json
  的裸名键 `qwen3-coder-plus`（录制日 2026-10-11；CLI qwen_model_candidates 首个命中候选即裸名键，
  黄金对账逐位一致实证 cache 价推导口径与 CLI 相同）

## Dependencies

- I-001（共享引擎）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成聊天 JSONL（user 行、旗舰 assistant 行、total 兜底两分支、全零、坏 JSON、
   usageMetadata null/非对象、user 带 usageMetadata、跨文件同内容键、mtime 回退、sessionId/模型回退、
   三段布局诱饵、env 空串），运行 `QwenCodeAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 仅 projects/&lt;p&gt;/chats/&lt;f&gt;.jsonl 三段布局被发现；无标记行计 LinesSkipped；坏 JSON 计
  MalformedLines；结构/语义拒绝（user 带 usageMetadata、usageMetadata null/非对象、顶层非对象、全零）
  计 InvalidRecords（仓库口径显式计数，蓝本静默）；extra（thoughts/total 缺口）并入 OutputTokens；
  同内容键跨文件 first-wins 折叠计 DuplicateRecords；QWEN_DATA_DIR 设置即不回退默认（空串生效为空）。

Result: 通过（Infrastructure 19/19：Loads_qwen_jsonl_usage_entries、
Falls_back_to_total_token_count_when_qwen_parts_are_missing、Total_fallback_keeps_extra_when_output_is_known、
Zero_and_missing_counts_are_invalid、Structural_and_semantic_rejections_are_counted_and_do_not_stop_the_scan、
Entry_id_dedupes_across_files_first_wins、Repeated_scan_is_idempotent、
Entry_key_is_unambiguous_for_colon_fields、Missing_timestamp_falls_back_to_file_mtime、
Unparseable_timestamp_falls_back_to_file_mtime、Missing_session_id_falls_back_to_project_and_file_stem、
Missing_model_defaults_to_unknown、Discovers_only_projects_chats_layout_files、
Qwen_data_dir_environment_variable_is_respected_without_default_fallback、
Set_but_empty_environment_variable_yields_no_data_without_fallback、
Missing_configured_directory_yields_no_data、Records_are_sorted_by_timestamp_across_files、
Prices_records_via_builtin_snapshot_qwen_section、Project_from_path_extracts_project_between_projects_and_chats）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/TokenPulse.Reconciliation.Tests/fixtures/qwen/`（合成数据，双项目双日，覆盖：
   user 行预过滤、total 恰等四桶和 no-op、total 缺口补 output（output 为 0）、坏 JSON 行、
   usageMetadata null / 非对象（"oops"）/ 全零 / user 带 usageMetadata 拒绝、同内容键跨文件去重、
   布局诱饵 transcripts/ 与 chats/deep/）。
2. 录制 golden：`QWEN_DATA_DIR=$PWD/$FX/qwen npx --yes ccusage@20.0.29 qwen daily --json --offline
   --timezone UTC > $FX/golden/qwen-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateQwenAsync(QwenRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；自研定价未命中清单
  为空；诊断 status=Partial、malformed=1、invalid=4、duplicate=1、linesSkipped=1、records=4。

Result: 通过——两侧全量 **input=610 / output=330 / cacheCreation=0 / cacheRead=125 / total=1,065
tokens，totalCost=$0.0022725** 逐位一致（02-23：qwen3-coder-plus 300/150/0/25 cost 0.0010525；
02-24：310/180/0/100 cost 0.00122）；诊断 status=Partial、FilesDiscovered=4、records=4、
UnpricedModels=[]。

### TC-003: 本机真实数据对账（TOKENPULSE_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENPULSE_RECONCILE_REAL=1 dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Real_qwen"`。
2. 数据存在性门控：定位器（QWEN_DATA_DIR / ~/.qwen/projects）下发现聊天 .jsonl 才运行（与 CLI 子进程
   同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 qwen daily --json --offline --timezone UTC`（golden）
   vs `AggregateQwenAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（thoughts/extra 并入 output 的显示差异 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Qwen Code 数据 → 测试按门控 Skip**（与 claude/copilot/gemini/droid
门控同态）。门控链路本身已在本机验证：以 `QWEN_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用
CLI 生成 golden 与自研全链路对账**通过**（`Real_qwen_data_reconciles_with_cli` Passed，1/1）。
有真实数据的机器上运行该门控即得真实数字。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/qwen/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 qwen/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 QwenLogLocator / QwenLogParser / QwenCodeAdapter /
QwenUsageRecordDeduper 文件头；fixtures 全部合成，无真实数据入仓）：

- **共享引擎复用与去重钩子**：日志布局与 Claude Code projects/ 同构，走 I-001 共享
  `IncrementalJsonlScanner`（文件 mtime/大小增量记账 + 变更文件整文件重解析 + 诊断状态），与蓝本
  load_entries 的"并行读 + 发现序 first-wins 去重"等价（蓝本并行读后仍按发现序串行去重）。差异处：
  Qwen 无 message id，蓝本去重键为内容键 entry_id（JSON 数组），与 Claude 的 MessageId/RequestId
  去重语义不同——为扫描器增加可选 `dedupe` 委托参数（缺省仍为 ClaudeUsageRecordDeduper，既有四
  适配器零改动），注入新移植的 `QwenUsageRecordDeduper`。蓝本通用件复用既有共享移植：
  `collect_files_with_extension` → `JsonSource/SourceFileCollector`、env 目录列表语义 →
  `JsonSource/EnvDataDirResolver`、`parse_ts_timestamp` → `JsonSource/StrictRfc3339Timestamp`、
  `apply_total_token_fallback` → `JsonSource/RawTokenUsage` + `TokenUsageFallback`、lenient 字段
  读取 → `SqliteSource/LenientJson`。
- **目录发现**（paths.rs 逐条）：QWEN_DATA_DIR 已设置（含空串）即只用配置、不回退默认（is_dir 过滤
  与去重对默认目录同样生效，EnvDataDirResolver 同口径）；默认 ~/.qwen 目录必须存在；聊天文件仅取
  projects/&lt;p&gt;/chats/&lt;f&gt;.jsonl 三段布局（is_chat_file 恰 3 段、第 2 段 "chats"），按完整
  路径排序；configuredDataDir 为仓库测试钉定增强（Gemini 同款，非蓝本）。
- **解析分支**（parser.rs 逐条）：行预过滤 `"usageMetadata"`（LinePrefilter 同口径）；serde 字段
  语义等价——type/sessionId/timestamp/model 为 non_empty_string（trim 后非空，比较用 trim 值）、
  计数字段 lenient_u64（仅整数生效，浮点/字符串/null/负数/缺失→0，u64 溢出钳 long.MaxValue 为仓库
  既有偏差）、usageMetadata 为**严格** Option&lt;结构体&gt;（null/缺失→None 即拒绝该行；标量/数组→
  serde 结构性失败，整行拒绝）；type 恰为 "assistant"、四桶与 extra 全零拒绝。
- **时间戳口径**：行内 timestamp 经严格 RFC3339（parse_ts_timestamp）校验通过才采用，否则整体回退
  文件 mtime（毫秒截断）；mtime 不可得回退当前时间（蓝本 SystemTime::now 分支；Kimi 蓝本同函数回退
  纪元，两蓝本不同，各自保真）。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎；extra（thoughts / total 缺口余量）并入
  OutputTokens（蓝本 billable_usage 的 output+extra 同式，总量与成本守恒）——桌面统一记录无
  extra 字段，I-010 系偏差②同款。
- **诊断口径**（仓库 I-004 显式化，Claude 系口径）：LinesSkipped = 无 usageMetadata 标记的行（含
  打不开的文件）；MalformedLines = 含标记但 JSON 解析失败的行；InvalidRecords = JSON 合法但未产出
  记录的行（顶层非对象、usageMetadata 非对象/null、type 非 assistant、全零）；DuplicateRecords =
  内容键跨文件折叠数；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IQwenCodeAdapter` 单例 + LocalIngestionDataSource 第十三路 concat；适配器实例持有
  扫描状态（共享引擎记账），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：新增 qwen 段（qwen3-coder-plus：input 1 / output 5），数据取自蓝本
  models-dev-pricing.json 的裸名键（CLI 候选序 [裸名, qwen/, alibaba/] 首个命中即裸名键）；cache
  价缺省由解析器按 input×0.1/×1.25 推导——golden 录制实证 CLI 对裸名键同样按推导 cache 价计费
  （02-23 差值 25×0.1/M=0.0000025、02-24 差值 100×0.1/M=0.00001 全部对上），两侧逐位一致。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入**：蓝本 calculate_qwen_cost 逐候选（裸名、qwen/、alibaba/ 前缀）查
  PricingMap 计价、missing_pricing_model_for_candidates 报告缺口。自研 record.Model = 裸模型名，引擎
  按模型名 + 自身候选机制匹配（provider 前缀候选不生成）。**对账后果**：仅挂在限定键下的模型自研侧
  记 0（fail-loud）；fixture 模型裸名可命中，golden 逐位一致。I-024 已登记同类共性欠账，本卡按同
  口径不做 resolver 接线。
- ② **extra（thoughts/total 缺口）并入 OutputTokens**：CLI daily 的 outputTokens 不含 extra（单列）、
  totalTokens 含之——并入 output 使 total 与 cost 一致（蓝本 billable_usage 同式）。两类真实数据形态
  会触发 output 分量 fail-loud：thoughts&gt;0 的行、total&gt;known 且 output 已知的行。fixture 规避这两类
  行（thoughts 恒 0、total 恰等四桶和或走 output 补齐分支），该语义由单测覆盖（total 兜底两分支）。
- ③ **去重键时间戳取毫秒时刻**：蓝本 entry_id 用 timestamp 原始文本，同刻不同 RFC3339 文本（Z 与
  +00:00）算两条；自研统一记录只有时刻（DateTimeOffset），键取毫秒 epoch——同刻异文本合并为一条。
  另 extra 已并入 output 分量，(out, extra) 折叠等价（display output 不同但 out+extra 相同）的记录
  在自研侧同样合并、计 DuplicateRecords（QwenUsageRecordDeduper.cs 注释同口径）。
  仅影响 DuplicateRecords 计数的角落场景，token 口径不变。
- ④ **行级拒绝/空产出显式计数**（蓝本静默 continue）：无标记行计 LinesSkipped、坏 JSON 计
  MalformedLines、结构/语义拒绝计 InvalidRecords——诊断不改变记录内容（I-004 显式化口径）。
- ⑤ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，I-006 系已登记差异
  同款）；configuredDataDir 为仓库增强；蓝本 paths 不做 canonicalize（无此差异）。
- ⑥ **u64 → long 边界**：lenient_u64 读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥
  同款）；u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑦ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构），
  日期过滤/分桶由下游聚合承担；蓝本 Display 模式（成本记 0）桌面无对应模式（Kilo 偏差③同款）；
  蓝本 session 报表的 ISO 日期边界过滤（filter_session_summaries）属 CLI 报表层，桌面不涉及。
- ⑧ **timestamp 理论边界**：蓝本 jiff 支持 year 0，DateTimeOffset 下限 year 1，year 0000 输入按不可
  解析处理（回退 mtime；真实日志不出现）。

验证：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` **502 通过 / 16 跳过**
（I-015 收口基线 481/15 + 新增 19 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 QWEN_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 /
TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（两卡均可收口、无阻塞项；本卡修复 P2-1 / P2-3 / P2-7，Kimi 卡
P2-2 / P2-4 / deferred 登记同批），修复如下：

- **[P2-1] fixture 补 usageMetadata 非对象拒绝行**：README 与对账测试注释声称 fixture 覆盖
  "usageMetadata 非对象"但实际没有——选"补行入 fixture"方案（保真优先）：`chat-b.jsonl` 追加
  `{"type":"assistant","usageMetadata":"oops"}`（serde 严格 Option&lt;结构体&gt; 整行拒绝场景入仓）。
  该行两侧均不产出记录，**重录 golden 逐位不变**（diff 实证）；自研 InvalidRecords 3→4
  （QwenFixtureReconciliationTests 断言与 TC-002 Expected 同步）。
- **[P2-3] 共享引擎去重钩子优先级注释**：`IncrementalJsonlScanner` 构造参数文档补——`dedupe` 委托
  非null 时静默胜出、`deduper` 被忽略（两者互斥使用、不同时传入；不抛异常，保持构造简单）。
- **[P2-7] 偏差③补句**：补 "(out, extra) 折叠等价的记录在自研侧同样合并、计 DuplicateRecords"
  （QwenUsageRecordDeduper.cs 注释已有，卡片漏了）。
- 复核：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` **523 通过 / 17 跳过**
  （与 I-013 收口同数——本批修复无新增测试，仅 fixture 行与断言计数调整）；qwen fixture 对账 2/2
  与 CLI golden 逐位一致不回归；门控以 QWEN_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.Infrastructure/QwenCode/QwenLogLocator.cs`、
  `src/TokenPulse.Infrastructure/QwenCode/QwenLogParser.cs`、`src/TokenPulse.Infrastructure/QwenCode/QwenCodeAdapter.cs`、
  `src/TokenPulse.Infrastructure/QwenCode/QwenUsageRecordDeduper.cs`、
  `src/TokenPulse.Infrastructure/Jsonl/IncrementalJsonlScanner.cs`（dedupe 委托钩子）、
  `src/TokenPulse.Core/Abstractions/IQwenCodeAdapter.cs`、
  `src/TokenPulse.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/TokenPulse.App/DependencyInjection/AppServices.cs`、`src/TokenPulse.Core/Pricing/model-pricing-snapshot.json`、
  `tests/TokenPulse.Infrastructure.Tests/QwenCode/QwenCodeAdapterTests.cs`、
  `tests/TokenPulse.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/QwenFixtureReconciliationTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/fixtures/qwen/`、
  `tests/TokenPulse.Reconciliation.Tests/fixtures/golden/qwen-fixture-daily-v20.0.29.json`、
  `tests/TokenPulse.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/TokenPulse.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test TokenPulse.sln`（全绿）；fixture 对账
  `dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Qwen"`；门控复跑
  `QWEN_DATA_DIR=$PWD/tests/TokenPulse.Reconciliation.Tests/fixtures/qwen TOKENPULSE_RECONCILE_REAL=1
  dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Real_qwen"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；本卡收口 **502/16**，
  2026-10-12 审查修复后全仓 **523/17**）。
