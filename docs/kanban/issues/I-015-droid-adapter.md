---
id: I-015
title: Droid (Factory) 适配器
type: feature
priority: P3
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Factory Droid 会话数据（DROID_SESSIONS_DIR 或 ~/.factory/sessions/**/*.json），产出 agent=Droid 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/droid/。本机暂无数据，fixture 依蓝本合成。

## Acceptance Criteria

- [x] 目录发现与 JSON 解析；token/模型口径映射对齐蓝本
  - ✅ 2026-10-11：`DroidLogLocator` 逐条移植 paths.rs——DROID_SESSIONS_DIR **已设置即只用配置**（含
    空串：Rust env::var 对空串返回 Ok，配置生效、不回退默认），逗号分隔/去空白/仅存目录/按序去重；默认
    ~/.factory/sessions（目录必须存在）；configuredDataDir 参数为仓库共有测试钉定增强（Copilot 同款）；
    树下递归收集 .json 后仅保留文件名以 .settings.json 结尾的会话快照（其余 json 忽略）、按完整路径排序；
    解析（load_settings_file）：tokenUsage 四桶（整数 lenient_u64）+ thinkingTokens + totalTokens 经
    apply_total_token_fallback（I-010 移植共享件）、五桶全零不产出；model 链 = settings.model 归一化
    （normalize_droid_model_name：剥 custom: 前缀、深度感知剥 [..] 段、去首尾空白/尾 '-'、ASCII 小写、
    '.'/空白/'-' 折叠）→ 同目录 &lt;stem&gt;.jsonl 侧车前 500 行 "Model:" 提取 → provider 默认模型；
    providerLock 归一化 + unknown 按模型名推断；时间戳 providerLockTimestamp（严格 RFC3339）优先、
    否则文件 mtime；session id = 文件名剥 .settings.json
- [x] fixtures + 单元测试；接入本地采集管道
  - ✅ Infrastructure 14 项单测（临时目录合成数据，fixtures 全合成）+ 合成 fixture（*.settings.json +
    侧车 + 非快照 json）入仓 + CLI golden 录制逐位一致 + `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试
    （数据存在性 Skip，门控以 DROID_SESSIONS_DIR 钉 fixture 复跑通过 1/1）；`IDroidAdapter` +
    LocalIngestionDataSource 第十二路 concat + App DI 双注册

## Subtasks

- [x] `DroidLogLocator`（paths.rs 移植）：configuredDataDir → DROID_SESSIONS_DIR（设置即不回退默认）→
  ~/.factory/sessions（不存在即空）；递归 .json → *.settings.json 过滤 → 路径排序（蓝本 files.sort()）
- [x] `DroidSettingsParser`（parser.rs 移植）：ParseTokenUsage（四桶 + thinking + total 兜底、全零 null）；
  NormalizeDroidModelName / NormalizeDroidProvider / InferDroidProviderFromModel / DefaultModelFromProvider
  纯函数；侧车提取（Model: 行截断至引号/反斜杠/方括号之前）；时间戳链（providerLockTimestamp → mtime）；
  session id 剥后缀
- [x] `DroidAdapter`（loader.rs 移植）：发现 → 逐文件解析 → 时间戳稳定排序 → **倒序 latest-wins**
  同 session 去重（旧快照计 DuplicateRecords）→ 升序输出；每次全量重读（I-010 同款判断）
- [x] 单元测试 ×14（`DroidAdapterTests`：模型归一化蓝本三例、total 兜底、全量加载（含 thinking 并入
  output）、侧车回退、provider 默认模型、纯方括号模型名回退、latest-wins 去重、providerLockTimestamp
  优先、坏文件/无 tokenUsage 计数、快照定价、.settings.json 过滤（含嵌套）、env 设置即不回退（含空串）、
  缺省目录无数据）
- [x] 对账：合成 fixture（模型归一化 + 侧车回退 + archive 去重 + 全零跳过 + 非快照 json）入仓 +
  CLI golden 录制（fixtures/golden/droid-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENMETRY_RECONCILE_REAL` 门控真实对账测试（HasDroidData 数据存在性 Skip）
- [x] 管道接线：`IDroidAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十二路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无新增段**——claude-sonnet-4 / gpt-5 直接命中现有段；侧车提取的
  claude-opus-4-5-thinking 经快照家族模式（claude-opus-4-5）匹配，价格（5/25/0.5/6.25）与 CLI 解析
  逐位同（golden cost 0.00055 验证）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 *.settings.json（蓝本三例模型名、total 兜底、thinkingTokens、侧车 Model: 行、
   纯方括号模型名、archive 同名快照、providerLockTimestamp、坏 JSON、顶层数组、无 tokenUsage、
   全零 tokenUsage、env 空串），运行 `DroidAdapter.ScanAsync` 断言记录、去重、诊断与目录发现行为。

Expected:
- 模型归一化三例逐条（claude-opus-4-5-thinking-0 / claude-sonnet-4 / gemini-2-5-pro）；total 456 →
  output 456；thinking 5 → OutputTokens 55、TotalTokens 185；侧车 "Model: Claude Opus 4.5 Thinking
  [Anthropic]" → claude-opus-4-5-thinking；纯方括号模型名 → claude-unknown；archive 旧快照被 latest-wins
  去重（DuplicateRecords=1）；providerLockTimestamp 可解析即优先；坏 JSON 计 malformed（仓库口径显式
  计数，蓝本 Err 丢弃）、顶层数组计 invalid、无/全零 tokenUsage 计 skipped。

Result: 通过（Infrastructure 14/14：Normalizes_droid_model_names、
Falls_back_to_total_tokens_when_droid_parts_are_missing、Loads_usage_from_droid_settings_files、
Falls_back_to_sidecar_jsonl_model、Falls_back_to_provider_default_model_without_settings_or_sidecar、
Bracket_only_model_name_falls_back_to_provider_default、
Keeps_latest_snapshot_for_duplicate_session_ids、
Provider_lock_timestamp_takes_precedence_over_file_mtime、
Malformed_and_invalid_snapshots_are_counted_and_do_not_stop_the_scan、
Prices_records_via_builtin_snapshot、Discovers_only_settings_snapshot_files、
Droid_sessions_dir_environment_variable_is_respected_without_default_fallback、
Set_but_empty_environment_variable_yields_no_data_without_fallback、
Missing_configured_directory_yields_no_data）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/Tokenmetry.Reconciliation.Tests/fixtures/droid/`（session-a：settings.model
   "Claude-Sonnet-4-[Anthropic]" 100/50/20/10；session-b：无 model + 侧车 Model: 行 → opus-4-5-thinking
   10/20；archive/session-c 05-01 旧快照 + session-c 05-02 最新 100/200（gpt-5）；zero.settings.json
   全零跳过；meta.json 非快照不发现；所有快照均带 providerLockTimestamp——mtime 回退分支依赖检出时间，
   fixture 不覆盖）。
2. 录制 golden：`DROID_SESSIONS_DIR=$PWD/$FX/droid npx --yes ccusage@20.0.29 droid daily --json --offline
   --timezone UTC > $FX/golden/droid-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateDroidAsync(DroidRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同——含 opus-4-5-thinking
  的家族价 5/25/0.5/6.25 两侧同）；模型清单两侧同；自研定价未命中清单为空；诊断 status=Ok、skipped=1
  （全零快照）、duplicate=1（archive 旧快照）、records=3。

Result: 通过——两侧全量 **input=210 / output=270 / cacheCreation=20 / cacheRead=10 / total=510 tokens，
totalCost=$0.003803** 逐位一致（05-01：claude-sonnet-4 100/50/20/10 cost 0.001128；05-02：gpt-5
100/200/0/0 cost 0.002125、claude-opus-4-5-thinking 10/20/0/0 cost 0.00055）；诊断 status=Ok、
skipped=1、duplicate=1、records=3、UnpricedModels=∅。

### TC-003: 本机真实数据对账（TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_droid"`。
2. 数据存在性门控：定位器（DROID_SESSIONS_DIR / ~/.factory/sessions）下有 *.settings.json 才运行
   （与 CLI 子进程同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 droid daily --json --offline --timezone UTC`（golden）
   vs `AggregateDroidAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（thinkingTokens 并入 output 的显示差异 fail-loud，见偏差②）。

Result: 本机（2026-10-11）**无 Droid 数据 → 测试按门控 Skip**（与 gemini/amp 等门控同态）。门控链路
本身已在本机验证：以 `DROID_SESSIONS_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与
自研全链路对账**通过**（`Real_droid_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行
该门控即得真实数字。同轮其他源门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/droid/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 droid/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 DroidLogLocator / DroidSettingsParser / DroidAdapter 文件头；
fixtures 全部合成，无真实数据入仓）：

- **共享引擎适用性判断**：单会话单快照 JSON（每文件至多一条记录），去重是**会话级 latest-wins**
  （蓝本 rev + seen_sessions）而非消息级——`IncrementalJsonlScanner` 的消息级去重器与 mtime 增量记账
  均无对应语义锚点，不硬套；按 I-010/I-014 同款"每次全量重读"口径实现。蓝本通用件复用 I-010 移植的
  共享原语：`JsonSource/SourceFileCollector`（.settings.json 过滤 + 排序在定位器内完成，蓝本
  discover_settings_files + files.sort() 同构）、`JsonSource/EnvDataDirResolver`（DROID_SESSIONS_DIR
  设置即不回退语义）、`JsonSource/StrictRfc3339Timestamp`（providerLockTimestamp 严格解析）、
  `JsonSource/TokenUsageFallback` + `RawTokenUsage`（apply_total_token_fallback）。
- **快照语义**（parser.rs 逐条）：tokenUsage 四桶整数口径 + thinkingTokens + totalTokens 兜底
  （output 为 0 补 output、否则缺口入 thinking）；五桶全零整文件不产出；model 链三级回退
  （settings.model 归一化 → 侧车 .jsonl 前 500 行 "Model:" 提取归一化 → provider 默认）；
  normalize 后空串回退 provider 默认；providerLock 归一化 + unknown 按模型名推断（provider 仅服务
  默认模型选择与蓝本计价候选）。
- **latest-wins 去重**（loader.rs 逐条）：文件按路径排序 → 解析 → 记录按时间戳稳定排序 → 倒序遍历
  同 session id 首见者胜（= 时间戳最新，相等时间戳取文件序靠后者）→ 旧快照计 DuplicateRecords；
  产物升序输出（蓝本为最新者优先降序，聚合等价，见偏差④）。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎；thinkingTokens 并入 OutputTokens（蓝本
  cost_usage 的 output+reasoning 同式，总量与成本守恒）；蓝本 pricing_timestamp
  （providerLockTimestamp vs mtime 分支）桌面无按记录计价时间戳对应物，不采集（见偏差①）。
- **诊断口径**（仓库 I-004 显式化）：FilesDiscovered = *.settings.json 数、FilesRead = 打开且解析完成
  的文件数（打不开计 LinesSkipped）、LinesSkipped = 无/全零 tokenUsage 与 mtime 不可得的快照、
  MalformedLines = 坏 JSON（蓝本 Err → debug_log 丢弃）、InvalidRecords = 顶层非对象（蓝本
  from_str::&lt;Value&gt; 成功后 as_object 失败 → Ok(None)，与 Amp 的 serde 结构性失败归 malformed
  不同——两蓝本行为本就不同，逐条保真）、DuplicateRecords = latest-wins 弃用的旧快照；状态
  Ok/Partial/NoData 同 I-006。
- **管道接线**：`IDroidAdapter` 单例 + LocalIngestionDataSource 第十二路 concat；适配器无扫描状态。
- **定价快照**：无新增段——claude-sonnet-4 / gpt-5 直接命中现有段；侧车提取的 claude-opus-4-5-thinking
  经快照家族模式（claude-opus-4-5）匹配（5/25/0.5/6.25），与 CLI 对该模型的解析逐位同
  （golden cost 0.00055 验证）；fixture 用量远低于 models.dev 分档阈值，按基础档计价两侧一致。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价与 pricing_timestamp 未端到端接入**：蓝本 calculate_droid_cost 逐候选
  [model, &lt;provider&gt;/model, openrouter/&lt;provider&gt;/model] 计价（首个 cost&gt;0 胜出），且
  pricing_timestamp 仅在 providerLockTimestamp 分支携带（mtime 分支为 None——避免把文件元数据时间
  当计价时间）。自研 record.Model = 归一化裸模型名，引擎按模型名 + 自身候选机制匹配（精确 + 快照家族
  模式），无按记录计价时间戳。**对账后果**：fixture 模型均可命中且基础档价格与 CLI 逐位同（golden
  成本逐位一致）；仅挂 provider 限定键的模型自研侧可能记 0（fail-loud）；DeepSeek 类时间敏感价目由
  引擎侧独立承担（本源 fixture 不涉及）。I-024 已登记同类共性欠账，本卡按同口径不做 resolver 接线。
- ② **thinkingTokens（extra）并入 OutputTokens**：CLI daily 的 outputTokens 不含 thinkingTokens、
  totalTokens 含之——并入 output 使 total 与 cost 一致（蓝本 cost_usage 同式）；含 thinkingTokens 的
  真实数据 output 分量 fail-loud（fixture 恒 thinking 0，该语义由单测覆盖：thinking 5 → OutputTokens
  55、TotalTokens 185，与蓝本 report_total_includes_thinking_tokens 场景同数字）。
- ③ **行级拒绝/空产出显式计数**（蓝本静默 Ok(None)/Err）：坏 JSON 计 MalformedLines、顶层非对象计
  InvalidRecords、无/全零 tokenUsage 与时间戳不可得计 LinesSkipped、latest-wins 弃用旧快照计
  DuplicateRecords——诊断不改变记录内容。
- ④ **产物顺序确定化**：蓝本 loader 产物为最新者优先的降序（rev 遍历 push），桌面统一时间戳升序
  （与其他源适配器一致；daily 聚合与顺序无关，仅同刻记录的列表顺序不同）。文件枚举按完整路径排序
  （蓝本 files.sort() 同口径，无差异）。
- ⑤ **home 缺失降级**：蓝本 droid_session_paths 在无 env 且无 home 时返回 Err；桌面按无数据处理
  （Roots 空，与其他源一致）。home 解析沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，
  I-006 系已登记差异同款）；configuredDataDir 为仓库增强。
- ⑥ **u64 → long 边界**：token 计数读取层把 u64 溢出钳到 long.MaxValue（saturating，I-017 偏差⑥
  同款）；u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑦ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构）；
  蓝本 Display 模式桌面无对应（Kilo 偏差③同款）。
- ⑧ **timestamp 理论边界**：蓝本 jiff 支持 year 0，DateTimeOffset 下限 year 1，year 0000 输入按不可
  解析处理（回退 mtime；真实日志不出现）。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **473 通过 / 15 跳过**
（I-014 收口基线 457/14 + 新增 14 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 DROID_SESSIONS_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见
TC-002 / TC-003。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/Droid/DroidLogLocator.cs`、
  `src/Tokenmetry.Infrastructure/Droid/DroidSettingsParser.cs`、`src/Tokenmetry.Infrastructure/Droid/DroidAdapter.cs`、
  `src/Tokenmetry.Core/Abstractions/IDroidAdapter.cs`、
  `src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/Tokenmetry.App/DependencyInjection/AppServices.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/Droid/DroidAdapterTests.cs`、
  `tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/DroidFixtureReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/droid/`、
  `tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/droid-fixture-daily-v20.0.29.json`、
  `tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test Tokenmetry.sln`（全绿）；fixture 对账
  `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Droid"`；门控复跑
  `DROID_SESSIONS_DIR=$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/droid TOKENMETRY_RECONCILE_REAL=1
  dotnet test tests/Tokenmetry.Reconciliation.Tests --filter "FullyQualifiedName~Real_droid"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 473/15，
  2026-10-12 审查修复后全仓 **481/15**——本卡无必修项，数字随 Gemini/Amp 审查修复的新增单测更新）。
