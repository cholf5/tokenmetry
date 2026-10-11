---
id: I-013
title: Kimi Code 适配器
type: feature
priority: P3
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Kimi CLI 会话数据（KIMI_DATA_DIR 或 ~/.kimi、~/.kimi-code 下 sessions/**/wire.jsonl），产出 agent=Kimi 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/kimi/。wire.jsonl 走共享 JSONL 扫描引擎即可。本机暂无数据。

## Acceptance Criteria

- [x] 目录发现（双 home）与 wire.jsonl 解析
  - ✅ 2026-10-11：`KimiLogLocator` 逐条移植 paths.rs——KIMI_DATA_DIR **已设置即只用配置**（含空串
    生效为空、不回退双默认；蓝本 env 分支提前 return 同口径），默认 ~/.kimi 与 ~/.kimi-code
    **两个**目录（存在者保留、按序去重）；configuredDataDir 参数为仓库共有测试钉定增强；wire 文件
    仅取 sessions/ 子树下文件名恰为 wire.jsonl 且相对段恰为 **3 或 5** 段者（旧布局
    sessions/&lt;group&gt;/&lt;session&gt;/wire.jsonl、新布局 sessions/&lt;ws&gt;/&lt;session&gt;/agents/&lt;agent&gt;/
    wire.jsonl），按完整路径排序去重；接入共享 IncrementalJsonlScanner 增量记账（mtime/大小）
- [x] token/模型口径映射对齐蓝本；fixtures + 单元测试
  - ✅ 旧格式（message.type=="StatusUpdate"）token_usage 五桶 input_other / output /
    input_cache_creation / input_cache_read / total（lenient_u64），total 兜底（output 为 0 补
    output、否则缺口并入 extra）；时间戳 lenient_f64 秒（毫秒 = 截断(seconds×1000)，非有限/越界回退
    文件 mtime，mtime 不可得回退 UNIX 纪元）；新格式（type=="usage.record"）仅取 usageScope=="turn"
    的四桶（session 级为累计总量跳过），时间戳 time 为 lenient_i64 毫秒，模型剥 "kimi-code/" 前缀；
    显示模型来自 Kimi 根目录 config.json 的 model 字段（agents 布局向上 5 级、旧布局 3 级定位根），
    任一环不可得回退 "kimi-for-coding"；会话 id 从 wire 路径推导。19 项单测全绿

## Subtasks

- [x] `KimiLogLocator`（paths.rs 移植）：configuredDataDir → KIMI_DATA_DIR（已设置即不回退双默认，
  空串生效为空）→ ~/.kimi + ~/.kimi-code 双目录；sessions/ 子树 3/5 段 wire.jsonl 发现、排序去重；
  RootFromWirePath / SessionIdFromWirePath 路径推导（agents 5 级 / 旧 3 级）；共享
  `EnvDataDirResolver` / `SourceFileCollector` / `JsonlHomePath`
- [x] `KimiLogParser`（parser.rs 移植）：KimiWireLine 信封等价移植（type/message/timestamp/model/
  usage/usageScope/time；message → payload → token_usage 严格 Option&lt;结构体&gt; 链：null/缺失→None、
  非对象→整行拒绝）；行预过滤 any("token_usage", "usage.record")；新旧双格式分流（usage.record →
  turn 级新格式、metadata → 跳过、其余 → StatusUpdate 旧格式）；旧格式 message_id 记录到统一记录
  MessageId（仅作内容键分量，不接 Claude 消息级去重）
- [x] `KimiUsageRecordDeduper`（parser.rs kimi_entry_key 移植）：冒号连接内容键
  [session, message_id, 时间戳毫秒, model, in, out(含 extra), cc, cr]，跨文件 first-wins
- [x] `KimiCodeAdapter`（loader.rs 移植）：发现 → 共享引擎增量扫描 → 内容键去重 → 时间戳稳定排序 →
  统一记录与诊断；共享 `IncrementalJsonlScanner` 去重委托钩子（I-011 首建，本卡复用）
- [x] 单元测试 ×19（`KimiCodeAdapterTests`：旧格式旗舰场景（蓝本 loader 内联测试同数据）、新格式
  turn/session 分流与 kimi-code/ 剥离、total 兜底、坏行与全零拒绝、结构性拒绝计数（usage/message/
  payload 非对象、顶层非对象）、同 message_id 去重、根目录/会话双布局推导（蓝本
  kimi_root_resolves_correctly_for_both_path_layouts）、mtime 回退、config 缺失/损坏回退缺省模型、
  3/5 段发现（蓝本 paths 内联测试两场景）、env 设置即不回退（含空串）、缺省目录无数据、跨文件
  排序、快照定价（cache_write 推导口径）、缺省模型显示名不变 + 定价欠账 fail-loud）
- [x] 对账：合成 fixture（config.json + 双格式双日 + 4 段/non-wire 诱饵）入仓 + CLI golden 录制
  （fixtures/golden/kimi-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `CCUSAGE_RECONCILE_REAL` 门控真实对账测试（HasKimiData 数据存在性 Skip）
- [x] 管道接线：`IKimiCodeAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十四路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**新增 moonshot/kimi-k2.5 条目**（input 0.6 / output 3 / cache_read 0.1，cache_write
  缺省按 input×1.25 推导），数据取自蓝本 rust/crates/ccusage-core/src/models-dev-pricing.json 的
  `moonshot/kimi-k2.5` 条目（录制日 2026-10-11）；golden 对账实证 CLI 对该键同样按推导 cache_write
  计价（cache_creation 20 tokens × 0.75/M 差值对上），两侧逐位一致

## Dependencies

- I-001（共享引擎）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 wire.jsonl（旧格式 StatusUpdate 五桶、total 兜底、重复 message_id、新格式
   turn/session 级、kimi-code/ 前缀、metadata/TurnBegin 行、坏 JSON、usage/message 非对象、顶层
   非对象、全零、config.json 三态、mtime 回退、3/5/4 段布局诱饵、env 空串），运行
   `KimiCodeAdapter.ScanAsync` 断言记录、诊断与目录发现行为。

Expected:
- 仅 sessions/ 下 3/5 段 wire.jsonl 被发现；无标记行计 LinesSkipped；坏 JSON 计 MalformedLines；
  结构/语义拒绝（session 级、全零、非对象链、顶层非对象）计 InvalidRecords（仓库口径显式计数）；
  total 缺口 output 为 0 时补 output；同 message_id 同刻 first-wins 折叠计 DuplicateRecords；
  KIMI_DATA_DIR 设置即不回退双默认（空串生效为空）；config 缺失/损坏回退 kimi-for-coding 且该显示
  名在本仓库口径下不参与定价（I-024 欠账，fail-loud）。

Result: 通过（Infrastructure 19/19：Loads_status_update_token_usage_from_wire_files、
Loads_kimi_code_usage_record_format、Strips_kimi_code_prefix_from_real_model_names、
Falls_back_to_total_tokens_when_kimi_parts_are_missing、Skips_malformed_and_zero_token_wire_lines、
Structural_rejections_are_counted_and_do_not_stop_the_scan、Bad_json_lines_are_counted_as_malformed、
Entry_key_dedupes_same_message_id_first_wins、Root_and_session_resolve_correctly_for_both_path_layouts、
Missing_timestamp_falls_back_to_file_mtime、Missing_or_broken_config_falls_back_to_default_model、
Missing_config_file_falls_back_to_default_model、Discovers_wire_jsonl_files_under_sessions_group_session、
Discovers_both_old_and_new_layouts_and_skips_non_wire_files、
Kimi_data_dir_environment_variable_is_respected_without_default_fallback、
Set_but_empty_environment_variable_yields_no_data_without_dual_default_fallback、
Missing_configured_directory_yields_no_data、Prices_records_via_builtin_snapshot_moonshot_section、
Default_display_model_stays_unpriced_candidate_pricing_debt）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/CCUsage.Reconciliation.Tests/fixtures/kimi/`（合成数据，config.json
   model=kimi-k2.5，覆盖：旧格式 StatusUpdate 五桶（cache_creation 计价）、total 兜底 output 补齐、
   同 message_id 同刻重复去重、metadata/TurnBegin 行、新格式 turn 级 + session 级拒绝 + 全零拒绝 +
   坏 JSON 行、kimi-code/ 前缀剥离、4 段与 non-wire 诱饵）。
2. 录制 golden：`KIMI_DATA_DIR=$PWD/$FX/kimi npx --yes ccusage@20.0.29 kimi daily --json --offline
   --timezone UTC > $FX/golden/kimi-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregateKimiAsync(KimiRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；自研定价未命中清单
  为空；诊断 status=Partial、files=2、malformed=1、invalid=2、duplicate=1、linesSkipped=2、records=3。

Result: 通过——两侧全量 **input=3,164 / output=558 / cacheCreation=20 / cacheRead=14,858 /
total=18,600 tokens，totalCost=$0.0050732** 逐位一致（02-13：kimi-k2.5 100/482/20/10 cost
0.001522（含 cache_creation 20×0.75/M 推导价）；06-22：3064/76/0/14848 cost 0.0035512）；诊断
status=Partial、FilesDiscovered=2、records=3、UnpricedModels=[]。

### TC-003: 本机真实数据对账（CCUSAGE_RECONCILE_REAL=1 门控）

Steps:
1. `CCUSAGE_RECONCILE_REAL=1 dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Real_kimi"`。
2. 数据存在性门控：定位器（KIMI_DATA_DIR / ~/.kimi、~/.kimi-code 的 sessions/）下发现 wire.jsonl 才
   运行（与 CLI 子进程同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 kimi daily --json --offline --timezone UTC`（golden）
   vs `AggregateKimiAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（extra 并入 output 的显示差异 fail-loud，见偏差②）；缺省模型
  （kimi-for-coding）行的成本分量 fail-loud（见偏差①）。

Result: 本机（2026-10-11）**无 Kimi 数据 → 测试按门控 Skip**（与 claude/copilot/gemini/droid/qwen
门控同态）。门控链路本身已在本机验证：以 `KIMI_DATA_DIR=<TC-002 fixture>` 开启门控运行，真实调用
CLI 生成 golden 与自研全链路对账**通过**（`Real_kimi_data_reconciles_with_cli` Passed，1/1）。
有真实数据的机器上运行该门控即得真实数字。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/kimi/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 kimi/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；蓝本许可 MIT License，
Copyright (c) 2025 ryoppippi，声明保留在 KimiLogLocator / KimiLogParser / KimiCodeAdapter /
KimiUsageRecordDeduper 文件头；fixtures 全部合成，无真实数据入仓）：

- **共享引擎复用**：wire.jsonl 为行式 JSONL，走 I-001 共享 `IncrementalJsonlScanner`（文件 mtime/
  大小增量记账 + 变更文件整文件重解析 + 诊断状态），与蓝本 load_entries 的"并行读 + 发现序
  first-wins 去重"等价。去重用 I-011 首建的扫描器 `dedupe` 委托钩子注入 `KimiUsageRecordDeduper`
  （蓝本 kimi_entry_key 冒号连接内容键，非 Claude MessageId 语义）。蓝本通用件复用既有共享移植：
  `collect_files_with_extension` → `JsonSource/SourceFileCollector`、env 目录列表语义 →
  `JsonSource/EnvDataDirResolver`、`apply_total_token_fallback` → `JsonSource/RawTokenUsage` +
  `TokenUsageFallback`、lenient 字段读取 → `SqliteSource/LenientJson`；纪元时间戳 → 共享
  `StrictRfc3339Timestamp.Epoch`。
- **目录发现**（paths.rs 逐条）：KIMI_DATA_DIR 已设置（含空串）即只用配置、**不回退双默认**（蓝本
  env 分支提前 return，与 Qwen/Gemini 的"空串不回退"同规则但更严格——连默认目录列表都不进）；默认
  ~/.kimi 与 ~/.kimi-code 两个目录都探测（存在者保留、按序去重）；wire 文件 = sessions/ 子树下
  文件名恰为 wire.jsonl 且相对段（Normal 组件）恰 3 或 5 段；排序 + 去重（files.sort() +
  files.dedup() 同口径）；configuredDataDir 为仓库测试钉定增强（非蓝本）。
- **解析分支**（parser.rs 逐条）：行预过滤 any（旧 `"token_usage"` / 新 `"usage.record"`）；分流
  type=="usage.record" → 新格式（usageScope 恰为 "turn"、四桶 camelCase 键、time lenient_i64 毫秒、
  模型剥 "kimi-code/" 前缀、无 message id）、type=="metadata" → 跳过、其余 → 旧格式（message.type
  恰为 "StatusUpdate"、token_usage 五桶 snake_case 键、timestamp lenient_f64 秒截断毫秒、
  message_id 入键与记录）；message/payload/token_usage/usage 为严格 Option&lt;结构体&gt;（null/缺失→
  None 拒绝该行；标量/数组→serde 结构性失败拒绝）；total 兜底（共享 TokenUsageFallback）；全零
  拒绝。config.json 模型：RootFromWirePath（agents 布局 5 级 / 旧布局 3 级——蓝本 old 链为
  file_path.parent ×4，即 wire 所在目录再 3 级）→ 读 config.json model（non_empty_string），任一环
  失败回退 "kimi-for-coding"（蓝本 DEFAULT_MODEL）；会话 id 从 wire 路径推导（agents 布局取 session
  目录名、旧布局取 wire 所在目录名，空白回退 "unknown"）。时间戳回退为文件 mtime（毫秒截断）、
  mtime 不可得回退 **UNIX 纪元**（蓝本 unwrap_or(UNIX_EPOCH)；Qwen 蓝本同函数回退 now，两蓝本不同，
  各自保真）。
- **成本落点**：统一记录 CostUsd 恒 null 交定价引擎；extra 并入 OutputTokens（仓库口径，蓝本 CLI
  列不含 extra——注意与 Qwen 不同：Kimi 蓝本 calculate_kimi_cost 计价用**不含 extra** 的 display
  四桶，故 extra&gt;0 的行在本口径下 output 分量与成本都会 fail-loud，见偏差②）。
- **诊断口径**（仓库 I-004 显式化，Claude 系口径）：LinesSkipped = 无预过滤标记的行（含打不开的
  文件）；MalformedLines = 含标记但 JSON 解析失败的行；InvalidRecords = JSON 合法但未产出记录的行
  （顶层非对象、嵌套结构非对象、metadata、usageScope 非 turn、非 StatusUpdate、缺 usage 块、全零）；
  DuplicateRecords = 内容键跨文件折叠数；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IKimiCodeAdapter` 单例 + LocalIngestionDataSource 第十四路 concat；适配器实例持有
  扫描状态（共享引擎记账），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：新增 moonshot/kimi-k2.5 条目（input 0.6 / output 3 / cache_read 0.1，cache_write
  缺省推导 0.75），取自蓝本 models-dev-pricing.json `moonshot/kimi-k2.5`；fixture 用 cache_creation
  20 tokens 实证 CLI 侧 cache_write 同为 input×1.25 推导（0.001522 逐位对上）。moonshot/kimi-k2.6
  已在快照（I-003 系录入），但其数值（0.95/4/0.16）为旧版 models.dev 数据，与 20.0.29 CLI 内嵌快照
  （1/4.16/0.18/cache_write 1）不同——fixture 规避 kimi-k2.6 计价行，该差异登记为定价快照版本欠账
  （I-024 观察项口径，真实 kimi-k2.6 数据成本 fail-loud）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入（含缺省模型时间戳定价档）**：蓝本 calculate_kimi_cost 逐候选（缺省模型
  按时间戳选 moonshot/kimi-k2.5|k2.6 定价档，再 moonshot/、kimi/ 前缀、裸名）查 PricingMap 计价。
  自研 record.Model = 显示模型（缺省 "kimi-for-coding"），引擎按模型名匹配——"kimi-for-coding" 与
  "kimi-k2.6" 均不按 CLI 口径命中，成本记 0（fail-loud）。fixture 规避两类行（config 恒设
  kimi-k2.5、新格式行内模型用 kimi-code/kimi-k2.5），缺省模型显示名语义由单测覆盖
  （Default_display_model_stays_unpriced_candidate_pricing_debt）。I-024 已登记同类共性欠账，本卡
  按同口径不做 resolver 接线。
- ② **extra（total 缺口）并入 OutputTokens**：CLI daily 的 outputTokens 不含 extra（单列）、
  totalTokens 含之；且蓝本 kimi 计价**不含** extra（Qwen 蓝本含，见 I-011 偏差②的对比）。extra&gt;0
  的行在 output 分量与成本上都 fail-loud；fixture 规避（total 恰走 output 补齐分支或缺失），该语义
  由单测覆盖（total 兜底）。
- ③ **去重键时间戳取毫秒时刻 + extra 并分量**：蓝本 kimi_entry_key 用格式化 timestamp 文本且
  output/extra 分列；自研键取毫秒 epoch、extra 并入 output 分量——同刻异文本、(out,extra) 折叠等价
  的记录在自研侧合并。仅影响 DuplicateRecords 计数的角落场景，token 口径不变。蓝本冒号连接无转义，
  自研同口径（碰撞面与蓝本一致）。
- ④ **行级拒绝/空产出显式计数**（蓝本静默 continue）：无标记行计 LinesSkipped、坏 JSON 计
  MalformedLines、结构/语义拒绝计 InvalidRecords——诊断不改变记录内容（I-004 显式化口径）。
- ⑤ **home 解析**：沿用共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 HOME 优先，I-006 系已登记差异
  同款）；configuredDataDir 为仓库增强。
- ⑥ **u64 → long 边界**：lenient_u64 读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥
  同款）；u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑦ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构）；
  蓝本 Display 模式（成本记 0）桌面无对应模式（Kilo 偏差③同款）。
- ⑧ **timestamp 理论边界**：蓝本 jiff 支持 year 0，DateTimeOffset 下限 year 1——新格式 time 为
  i64 毫秒直接换算不受影响；秒级 timestamp 域检查与蓝本一致（i64 域内），year 0000 仅在
  i64 毫秒负端出现（DateTimeOffset.UnixEpoch.AddMilliseconds 接受负值，表达 year < 1970，无拒绝
  差异；真实日志不出现）。

验证：`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` **523 通过 / 17 跳过**
（I-011 收口基线 502/16 + 新增 19 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 KIMI_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 /
TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（两卡均可收口、无阻塞项；本卡修复 P2-2 / P2-4 + deferred 两项
登记，Qwen 卡 P2-1 / P2-3 / P2-7 同批），修复如下：

- **[P2-2] I-024 观察项登记**：I-024 卡观察项段追加"快照数据版本刷新"——moonshot/kimi-k2.6 快照为
  旧版 models.dev 数据（0.95/4/0.16），CLI 20.0.29 内嵌为 1/4.16/0.18 + cache_write 1（来源
  ../ccusage/rust/crates/ccusage-core/src/models-dev-pricing.json）；真实 kimi-k2.6 数据当前 cost
  分量 fail-loud；随 I-024 一并处理或独立小卡。
- **[P2-4] 滑级注释口径统一**：KimiLogParser（类文档）/ KimiLogLocator（RootFromWirePath 文档）/
  KimiCodeAdapterTests 三处混用参考点（5/4 级、5/3 级、"向上 4 级"）统一为单一表述——"旧布局自
  wire 文件 4 次 GetDirectoryName / 自 wire 所在目录 3 级；agents 布局自 wire 所在目录 5 级"。
  代码不动（WalkUp 5:3 与蓝本一致）。
- **[deferred 登记]** Bugs 表新增两行 Status=deferred：KIMI-1（ReadModelFromConfig 只捕获
  IOException/UnauthorizedAccessException，蓝本全错误路径回退缺省模型——理论边界：root 来自刚
  枚举成功的 wire 路径前缀）；KIMI-2（会话 id 空白判定 IsNullOrWhiteSpace vs 蓝本仅 is_empty，
  纯空白目录名两侧结果不同的理论角落）。
- 复核：`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` **523 通过 / 17 跳过**
  （数字不变——本批修复为注释/文档/登记，无代码与测试变更）；kimi fixture 对账 2/2 与 CLI golden
  逐位一致不回归；门控以 KIMI_DATA_DIR 钉 fixture 复跑通过（1/1 Passed）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| KIMI-1 | P3 | `ReadModelFromConfig` 只捕获 IOException/UnauthorizedAccessException 回退缺省模型；蓝本对 config.json 读取的**全部**错误路径都回退（fs::read_to_string 任一 Err）。理论边界：root 来自刚枚举成功的 wire 路径前缀，仅并发删除/权限突变可触发其它异常类型 | deferred | 理论边界（审查登记），随对账回归或下一次触碰本文件时收口为 catch Exception |
| KIMI-2 | P3 | 会话 id 空白判定用 IsNullOrWhiteSpace，蓝本仅 `!name.is_empty()`——纯空白目录名（如 " "）两侧结果不同：蓝本保留空白名、自研回退 "unknown" | deferred | 理论角落（审查登记）；真实 wire 路径的目录名非空白，随真实数据对账回归再对齐 |

## Verification

- Related files: `src/CCUsage.Infrastructure/KimiCode/KimiLogLocator.cs`、
  `src/CCUsage.Infrastructure/KimiCode/KimiLogParser.cs`、`src/CCUsage.Infrastructure/KimiCode/KimiCodeAdapter.cs`、
  `src/CCUsage.Infrastructure/KimiCode/KimiUsageRecordDeduper.cs`、
  `src/CCUsage.Core/Abstractions/IKimiCodeAdapter.cs`、
  `src/CCUsage.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/CCUsage.App/DependencyInjection/AppServices.cs`、`src/CCUsage.Core/Pricing/model-pricing-snapshot.json`、
  `tests/CCUsage.Infrastructure.Tests/KimiCode/KimiCodeAdapterTests.cs`、
  `tests/CCUsage.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/CCUsage.Reconciliation.Tests/KimiFixtureReconciliationTests.cs`、
  `tests/CCUsage.Reconciliation.Tests/fixtures/kimi/`、
  `tests/CCUsage.Reconciliation.Tests/fixtures/golden/kimi-fixture-daily-v20.0.29.json`、
  `tests/CCUsage.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/CCUsage.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/CCUsage.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test CCUsage.sln`（全绿）；fixture 对账
  `dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Kimi"`；门控复跑
  `KIMI_DATA_DIR=$PWD/tests/CCUsage.Reconciliation.Tests/fixtures/kimi CCUSAGE_RECONCILE_REAL=1
  dotnet test tests/CCUsage.Reconciliation.Tests --filter "FullyQualifiedName~Real_kimi"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 **523/17**，
  2026-10-12 审查修复复核后数字不变）。
