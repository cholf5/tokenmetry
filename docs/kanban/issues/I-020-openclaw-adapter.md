---
id: I-020
title: OpenClaw 适配器
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 OpenClaw 数据（OPENCLAW_DIR 或 ~/.openclaw 及 ~/.clawdbot、~/.moltbot、~/.moldbot 的 JSONL；2026.9.x 起的 agents/*/agent/openclaw-agent.sqlite），产出 agent=OpenClaw 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/openclaw/。双格式：JSONL 直读（共享引擎）+ SQLite transcript_events（复用 I-006 骨架）；同数据的 SQLite 行优先于迁移的 JSONL 副本（去重）。本机暂无数据。

## Acceptance Criteria

- [x] 多 home 发现与双格式解析；SQLite 行与 JSONL 去重优先级对齐蓝本
- [x] token 映射对齐蓝本；fixtures + 单元测试

## Subtasks

- [x] 蓝本逐条研读（paths.rs / parser.rs / loader.rs / sqlite.rs；report.rs 归 CLI 报告层不在移植范围）
- [x] `OpenClawLogLocator`（paths.rs）：OPENCLAW_DIR / configuredDataDir **trim 非空才生效**（设置但空白回退默认四根，蓝本 env 分支 `!trim().is_empty()` 条件同口径；与 Kimi/Gemini "空串即配置生效"不同）→ 逗号分隔 / 逐段去空白 / 仅存目录 / 按序去重 → 主目录 .openclaw、.clawdbot、.moltbot、.moldbot 四根（存在者保留按序）；会话文件 = **整根递归**（非 sessions/ 子树限定）、符号链接不跟随、文件名首段 ".jsonl" 后缀恰为 ".jsonl" 或 ".jsonl.deleted.*" / ".jsonl.reset.*"（归档/重置会话）、根内按路径排序
- [x] `OpenClawLogParser`（parser.rs）：行预过滤 any（"model_change" / "model-snapshot" / "usage"）→ 结构解析（顶层非对象、type/customType 非(null/字符串) → Malformed；其余字段全 lenient）→ model_change / model-snapshot（data 块对象才读 data 内字段、否则根级；model ← modelId ?? model）状态跟踪（单文件内存活）→ message 行（role 恰 "assistant"、usage 块存在非全零；input/output/cacheRead→cache_read/cacheWrite→cache_creation；total 兜底共享 TokenUsageFallback）→ 记录（"[openclaw] " 前缀模型、cost.total 直采、时间戳 message ?? 根级 ?? mtime ?? 纪元）
- [x] `OpenClawSqliteReader`（sqlite.rs）：agents/*/agent/openclaw-agent.sqlite 发现（目录与文件均非符号链接、按路径排序）→ 只读打开（共享 SqliteReadonly）→ transcript_events 表存在性检查（缺失静默跳过不算问题）→ `SELECT session_id, seq, event_json, created_at ORDER BY session_id, seq` → 逐行复用同一行映射、跟踪状态**按会话边界重置**（JSONL 逐文件状态的等价物）
- [x] `OpenClawAdapter`（loader.rs）：每根先 SQLite 后 JSONL → JSONL 走共享 `IncrementalJsonlScanner`（内容键 = migration_id + 成本 first-wins）→ **SQLite 优先合并**：事件 id "openclaw:&lt;session&gt;:&lt;id&gt;" 去重 → ①会话+事件 id、②迁移身份（migration_id：会话+时间戳+模型+四桶+extra，**不含成本**）匹配替换 JSONL 副本、③追加 → 时间戳稳定排序
- [x] 接线：`IOpenClawAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十八路 concat + AppServices 单例
- [x] 单元测试 49 项（tests/Tokenmetry.Infrastructure.Tests/OpenClaw/，蓝本 paths/parser/loader/sqlite 内联测试同名场景逐一对齐 + 载荷类型角落 + env 语义 + 双格式优先级）
- [x] fixture（合成 JSONL 会话 + python3 生成合成 transcript_events 库，无真实数据）+ golden 录制（ccusage 20.0.29 openclaw daily --json --offline --timezone UTC，OPENCLAW_DIR 钉 fixture）+ 对账测试 2 项
- [x] 门控真实对账就绪（RealDataReconciliationTests.Real_openclaw_data_reconciles_with_cli；本机无数据验证 Skip）

## Dependencies

- I-001（共享引擎）、I-006（SQLite 骨架）

## Test Cases

### TC-001: 蓝本语义单元测试（tests/Tokenmetry.Infrastructure.Tests/OpenClaw/OpenClawAdapterTests.cs，49 项）

Steps:
1. 归档会话文件判定与会话 id 推导（.jsonl / .jsonl.deleted.* / .jsonl.reset.* / 非 .jsonl 后缀；首段 ".jsonl" 前缀推导）。
2. parser.rs 内联测试同名场景：malformed_message_does_not_drop_model_tracking_line（lenient message 不弃状态行）、falls_back_to_total_tokens_when_openclaw_parts_are_missing（total 222 → output 222）、装饰模型与内建成本/无成本两分支。
3. 载荷类型角落：lenient_u64 对字符串/负数/浮点/null 记 0、data 非对象回退根级字段、data 空对象阻断根级回退、message 块可用性三态（缺失/字段类型不符/可用）先于 role/usage 语义分支、结构失败面（坏 JSON / 顶层非对象 / type/customType 类型错）。
4. 时间戳解析链：数值毫秒、严格 RFC3339 文本（含 ±偏移）、message null → 根级、message 不可解析 → 文件 mtime（根级不参与）、负毫秒 → mtime。
5. loader.rs 内联测试同名场景：loads_assistant_usage_and_uses_model_change_events、deduplicates_repeated_openclaw_records、creates_sqlite_records_when_no_jsonl_sessions_exist、**prefers_sqlite_over_migrated_jsonl_duplicates**（成本 0.50 → 0.51 逐位替换）。
6. sqlite.rs 内联测试同名场景：坏行不弃会话、缺 transcript_events 表静默跳过、库发现排序、符号链接三态不发现、会话边界重置状态、created_at 回退（REAL 截断 / 负值归纪元）、非 TEXT 身份列拒绝。
7. env 语义：OPENCLAW_DIR 逗号列表（去空白/仅存目录/按序去重）、设置但空白回退默认四根、设置即不回退、目录发现递归与排序、跨根内容键去重。

Expected:
- 全部断言与蓝本内联测试/注释语义一致；0 警告 0 错误。

Result: ✅ 49 通过 / 0 失败（2026-10-11，含蓝本 paths.rs detects_archived_session_files、parser.rs 三例、loader.rs 四例、sqlite.rs 五例全部同名移植）。

### TC-002: fixture 对账（tests/Tokenmetry.Reconciliation.Tests/OpenClawFixtureReconciliationTests.cs，2 项）

Steps:
1. fixture：fixtures/openclaw/（合成 agents/main、agents/worker 双 agent 的 JSONL 会话含归档后缀 + python3 生成的两份 transcript_events 库，含迁移副本、SQLite-only 会话、坏 JSON 载荷行、not-a-database.sqlite 干扰项；全部合成数据）。
2. `AggregateOpenClawAsync(OpenClawRoot)` → PricingEngine 内置快照 → UTC 日聚合，与 golden（ccusage 20.0.29 openclaw daily --json --offline --timezone UTC，OPENCLAW_DIR 钉 fixture）经 ReconciliationComparator 比较（日期 × 模型 + 每日 + 全期三个粒度）。
3. 链路健康检查断言：FilesDiscovered=6、FilesRead=6、LinesSkipped=4、MalformedLines=1、InvalidRecords=1、DuplicateRecords=2（重复行折叠 + 迁移副本替换）、RecordCount=9、Status=Partial、UnpricedModels=[openclaw] mystery-model-x。

Expected:
- tokens 五分量全粒度逐位一致；成本经比较器容差一致（唯一差异为 CLI 浮点累加 0.30000000000000004 vs decimal 0.30，3e-17 量级）。

Result: ✅ 2 通过 / 0 失败（2026-10-11）。对账数字：3 天 × 6 模型键 + 全期——input 1,002,331 / output 457 / cache_creation 5 / cache_read 109,028 / total 1,111,821 / cost 3.68（golden 3.6799999999999997）；01-30 迁移副本替换实证：gpt-5.2 该日 cost 0.51（SQLite 行）而非 JSONL 0.50，与 golden 逐位一致。

### TC-003: 门控本机真实数据对账（RealDataReconciliationTests.Real_openclaw_data_reconciles_with_cli）

Steps:
1. `TOKENMETRY_RECONCILE_REAL=1` 且本机存在 OpenClaw 数据（OPENCLAW_DIR / 四默认根下有会话 JSONL 或 agent 库）时运行：CLI openclaw daily --json --offline 与自研全链路对账。
2. 任一条件不满足时 Skip 而非 Fail。

Expected:
- 本机（无 OpenClaw 数据）Skip；有数据机器全链路 tokens 精确一致。

Result: ✅ Skip 验证通过（2026-10-11，TOKENMETRY_RECONCILE_REAL=1 下 Skipped: 1；数据探测复用产品定位器与 CLI 同一套逻辑）。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/openclaw/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 openclaw/ 的 paths.rs / parser.rs / loader.rs / sqlite.rs 逐条对齐；蓝本许可 MIT License，Copyright (c) 2025 ryoppippi，声明保留在 OpenClawLogLocator / OpenClawLogParser / OpenClawSqliteReader / OpenClawAdapter 文件头；fixtures 全部合成——JSONL 手写 + python3 sqlite3 生成两份 transcript_events 库，无真实数据入仓）：

- **双格式与共享件复用**：JSONL 侧走 I-001 共享 `IncrementalJsonlScanner`（文件 mtime/大小增量记账 + 变更文件整文件重解析 + dedupe 委托钩子注入内容键 first-wins），与蓝本 read_files_parallel + 顺序 first-wins 等价（去重按发现序，串行读取保序——I-001 同款论证）；SQLite 侧复用 I-006 系骨架（SqliteReadonly / LenientJson / JsonlScanDiagnostics 口径）。蓝本双格式共用 OpenClawLine 结构——移植为 ParseLine（结构）+ MapLine（语义）单行映射，JSONL 与 SQLite 逐行复用同一代码路径（蓝本 parse_transcript_message 双侧复用同构）。`apply_total_token_fallback` → 共享 TokenUsageFallback、lenient 字段读取 → 共享 SqliteSource/LenientJson、严格 RFC3339 → 共享 StrictRfc3339Timestamp、纪元回退 → StrictRfc3339Timestamp.Epoch。
- **env 语义（本源特有，逐字对照 paths.rs）**：OPENCLAW_DIR / configuredDataDir **trim 后非空才生效**——设置但空白时回退默认四根（蓝本 env 分支条件含 `!trim().is_empty()`；与 Kimi/Gemini 的"空串即配置生效不回退"、pi/Grok 的"trim 非空"族同款但**默认根为四个**）；逗号分隔、逐段去空白、仅保留存在目录、按序去重（蓝本 existing_path_list 同构）。默认四根 .openclaw / .clawdbot / .moltbot / .moldbot 存在者保留按序（蓝本无排序、固定四名无需去重）。
- **会话文件发现（蓝本特有，非扩展名匹配）**：整根递归（不限定 sessions/ 子树），文件名**首个 ".jsonl" 起的后缀**恰为 ".jsonl" 或以 ".jsonl.deleted." / ".jsonl.reset." 开头（归档/重置会话同账）；会话 id = 首段 ".jsonl" 前缀（空主干/无后缀回退整个文件名）；根内按路径排序（files.sort()）、根间按根序。
- **模型跟踪双语义**：JSONL 跟踪状态在单文件内存活（parse_session_file 局部变量）；SQLite 行按 session_id 分组连续到达，会话边界重置（蓝本 current_session 同构）。model_change（type 恰为）与 model-snapshot（type=="custom" 且 customType 恰为）双形态；data 块**为对象时只读 data 内字段**（缺失不回退根级——蓝本 match Some 分支）、非对象/缺失才落根级；model ← modelId ?? model，Some 语义（缺失不覆盖当前状态）。
- **usage 行映射**：input/output 原名、cacheRead→cache_read、cacheWrite→cache_creation（lenient_u64）；total 兜底（共享 TokenUsageFallback：output 为 0 优先补 output，否则缺口并入 extra；四桶+extra 全零跳过）；total_tokens_final = max(total, 四桶+extra)。模型 = message.modelId ?? message.model ?? 跟踪状态 ?? "unknown"，统一记录带 "[openclaw] " 前缀（蓝本 format!("[openclaw] {model}")）。时间戳 = message.timestamp ?? 根级 timestamp（**元素选取先于解析**——message 值不可解析直接回退 fallback，根级不参与；蓝本 .or() 选取 + unwrap_or 同构）→ 数值毫秒（负值/越界不可用）/ 严格 RFC3339 文本 → fallback（JSONL 文件 mtime / SQLite created_at REAL 截断负值归 0）→ UNIX 纪元。NaN → 0、±∞ 饱和转换逐字保真（Rust `as i64` 语义）。
- **成本落点**：usage.cost.total 存在即直采（含 0，蓝本 CostMode::Auto；f64→decimal 走 I-017 同款 R 往返换算），缺失交定价引擎（CostUsd null）。蓝本 pricing_model 判定（装饰名 find_exact 命中用装饰名、否则裸模型名现算）未接线——见偏差①。
- **双格式优先级去重（本卡核心，蓝本 load_entries_inner 收尾循环逐条对齐）**：JSONL 记录先经内容键（蓝本 entry_id = migration_id + ":" + cost）first-wins 去重；SQLite 记录随后合并——(a) 事件 id（non_empty_string）按 "openclaw:&lt;session&gt;:&lt;id&gt;" 去重；(b) ①会话+事件 id 精确匹配替换（JSONL 侧记录无消息 id，恒不命中，保真保留）、②迁移身份匹配替换（migration_id = "openclaw" + 会话 + 时间戳 + 模型 + 四桶 + extra，**成本不参与**——迁移副本可带修正后的 provider 结算成本 0.50→0.51）、③追加；(c) 无事件 id 的 SQLite 行按内容键 first-wins。**等价性论证**：蓝本 migration_id 的时间戳分量为 format_rfc3339_millis 文本（毫秒精度、UTC）——与自研毫秒时刻一一对应；(out, extra) 分列改合并为 I-013 偏差③同款理论角落（同会话同毫秒同模型同 input/cc/cr 且 output/extra 拆分互补才可能碰撞，token 口径不变）；fixture 以同会话/同毫秒/同用量/异成本副本实证两侧替换结果逐位一致（golden gpt-5.2 02-02 cost 0.51）。
- **诊断口径**（仓库 I-004 显式化，行级归因登记）：LinesSkipped = 无预过滤标记的行（含打不开的文件）+ 非 message 型行 + 非 assistant role + 缺 usage 块 + 四桶+extra 全零；MalformedLines = 含标记但 JSON 解析失败 / 顶层非对象 / type 或 customType 非(null/字符串)（serde 结构性失败同拒绝面）+ SQLite 载荷坏 JSON/结构失败；InvalidRecords = type=="message" 但 message 块缺失/非对象/role 或 usage 字段类型不符（lenient_object → None，可用性判据**先于** role/usage 语义分支）+ SQLite session_id/event_json 非 TEXT（rusqlite 严格类型同拒绝面）；DuplicateRecords = JSONL 内容键折叠 + SQLite 事件 id 去重 + 迁移副本替换；状态 Partial 于 Malformed/Invalid&gt;0 或库打开/查询失败（问题标记）；missing transcript_events 表按蓝本 debug **静默跳过不算问题**。FilesDiscovered/FilesRead = JSONL 文件数 + agent 库数。
- **管道接线**：`IOpenClawAdapter` 单例 + LocalIngestionDataSource 第十八路 concat；适配器实例持有 JSONL 增量扫描状态（SQLite 侧无状态每次全量重读），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：无需新增——可计价模型行恒带内建成本（LogCost 直采），无内建成本行为未定价模型（CLI missingPricing 双侧 0 成本）；"[openclaw] " 装饰名计价欠账见偏差①（**OC-1**，已登记 I-024，不私自接线）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **候选计价未端到端接入（装饰模型定价欠账）**：蓝本 openclaw_entry_to_loaded_inner 的 pricing_model 判定——装饰名 "[openclaw] m" 被 pricing.find_exact 命中则按装饰名计价，否则回退**裸模型名**（raw pricing identity，蓝本内联测试 calculates_decorated_deepseek_models_with_the_raw_pricing_identity：1M input deepseek-v4-flash → 0.44）。自研统一记录仅 Model（装饰名），引擎按装饰名不命中 → 记 0 + UnpricedModels（fail-loud）。fixture 规避两类行（可计价模型恒带内建成本走 LogCost；无内建成本行用 missingPricing 模型双侧 0），装饰名/裸名计价语义由单测覆盖（CostUsd 直采与 null 分支）。该欠账已作为 **OC-1** 登记 I-024（2026-10-12 审查补录——初稿"Grok GK-2 同类"表述不准确：GK-2 是剥前缀方向，OpenClaw 是"装饰名 find_exact 命中用装饰名、否则回退裸名"的两级候选链，非同一笔欠账），本卡按同口径不做 resolver 接线。
- ② **provider（蓝本 data.version = provider）无处落点**：统一记录无 provider/version 字段，模型跟踪侧读后即弃（蓝本仅作 loaded entry 显示口径；daily JSON 输出不含该字段，对账无影响）。
- ③ **去重身份键时间戳取毫秒时刻 + extra 并入 OutputTokens**：蓝本 migration_id 用 RFC3339 毫秒文本且 output/extra 分列；自研键取毫秒值、extra 并入 output 分量（统一记录无独立 extra 字段，I-013 偏差③同款）——仅影响同会话同毫秒同模型同 input/cc/cr 且 (out,extra) 拆分互补的理论角落的 DuplicateRecords 计数，token 口径不变；蓝本冒号连接无转义，自研同口径。
- ④ **行级坏数据显式计数**（蓝本 records() filter_map 静默丢弃 / SQLite 逐行 continue）：归因表见上文"诊断口径"；语义不改变记录内容（I-004 显式化口径）。
- ⑤ **目录/库级失败处理**：蓝本 JSONL 收集 read_dir 错误向上传播致**整次加载失败**（Err）、SQLite 打开/查询失败 debug 后返回空列表；桌面采集 JSONL 按仓库口径静默跳过读不了的子树（SourceFileCollector 同款）、SQLite 按"该库本轮 0 条 + 问题标记"降级 Partial（Kilo 同款登记）——真实数据不可达域的 fail-loud 程度差异。
- ⑥ **home 解析**：共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 home.rs HOME 优先，I-006 系已登记差异同款）；configuredDataDir / env 路径 ~ 展开 + GetFullPath 为仓库定位器共有增强（蓝本按原文 + is_dir 过滤）。
- ⑦ **u64 → long 边界**：lenient_u64 读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥同款）；u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑧ **时间戳理论边界**：蓝本 TimestampMs 为裸 i64（可表示任意 i64 毫秒；f64 +∞ 经 `as i64` 饱和为 i64::MAX 后继续携带）；DateTimeOffset 表示域外的毫秒值（&gt;253402300799999）按"时间不可用"回退 fallback 链（真实数据不可达；NaN→0 与 ±∞ 饱和转换本身逐字保真）。
- ⑨ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构；蓝本 Display 模式成本记 0 桌面无对应模式，Kilo 偏差③同款）。
- ⑩ **JSONL 收集错误传播**：蓝本 collect_session_files 的 read_dir/entry 错误 `?` 传播使单根损坏拖垮整个加载；桌面静默跳过该子树（与⑤同源的目录级差异，单列以便审查）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

### 2026-10-12（独立审查修复）

独立审查结论：源码 approve 水准（migration_id 九分量、合并序四步、JSONL/SQLite 单代码路径、替换 fixture 双侧实证全部核对通过；golden 重录逐字节一致），唯一阻塞项为文档级——偏差①声称"I-024 已登记（GK-2 同类）"不实（GK-2 是同类但非同一笔：OpenClaw 蓝本候选链是"装饰名 find_exact 命中用装饰名、否则回退裸名"两级，GK-2 是剥前缀方向）。修复：

- **[OC-1 登记]** I-024 卡 Background 补 OpenClaw 条目（OC-1，注明与 GK-2 的方向差异）、Dependencies 追加 I-020、接线 AC 扩为八源；本卡偏差①措辞同步更正（见上）。
- **[注释消歧]** OpenClawLogLocator.ExistingPathList 文档注释：路径规范化（~ 展开 + GetFullPath）为仓库共有增强，"./x" 与 "x" 的去重差异已属偏差⑥——原注释"路径不做规范化"与实现矛盾，已更正。
- **[备案（不修）]** 库打开失败但 records==0 时 Status=NoData 掩盖问题标记（与 KiloSqliteAdapter 同 precedence，family-consistent）；EntryId 成本分量用内嵌成本而非 Auto 计算后成本（双重理论角落，偏差①/③讨论域覆盖）。

## Verification

- Related files: `src/Tokenmetry.Core/Abstractions/IOpenClawAdapter.cs`、`src/Tokenmetry.Infrastructure/OpenClaw/OpenClawLogLocator.cs`、`src/Tokenmetry.Infrastructure/OpenClaw/OpenClawLogParser.cs`、`src/Tokenmetry.Infrastructure/OpenClaw/OpenClawSqliteReader.cs`、`src/Tokenmetry.Infrastructure/OpenClaw/OpenClawAdapter.cs`、`src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第十八路）、`src/Tokenmetry.App/DependencyInjection/AppServices.cs`、`tests/Tokenmetry.Infrastructure.Tests/OpenClaw/OpenClawAdapterTests.cs`、`tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（十八参构造）、`tests/Tokenmetry.Reconciliation.Tests/OpenClawFixtureReconciliationTests.cs`、`tests/Tokenmetry.Reconciliation.Tests/fixtures/openclaw/`（合成双格式 fixture）、`tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/openclaw-fixture-daily-v20.0.29.json`、`tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/README.md`（录制命令）、`tests/Tokenmetry.Reconciliation.Tests/Support/{UsageChainAggregator,FixturePaths,RealReconciliationEnvironment}.cs`、`tests/Tokenmetry.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`、`tests/Tokenmetry.Reconciliation.Tests/RealDataReconciliationTests.cs`（门控真实对账）
- How to run/verify: `dotnet build Tokenmetry.sln`（0 警告 0 错误）；`dotnet test Tokenmetry.sln`（全绿）；单测 `dotnet test tests/Tokenmetry.Infrastructure.Tests --filter FullyQualifiedName~OpenClaw`（49 项）；fixture 对账 `dotnet test tests/Tokenmetry.Reconciliation.Tests --filter FullyQualifiedName~OpenClawFixture`（2 项）；golden 重录：`OPENCLAW_DIR="$PWD/tests/Tokenmetry.Reconciliation.Tests/fixtures/openclaw" npx --yes ccusage@20.0.29 openclaw daily --json --offline --timezone UTC > tests/Tokenmetry.Reconciliation.Tests/fixtures/golden/openclaw-fixture-daily-v20.0.29.json`（命令已登记 fixtures/golden/README.md）
- Results: 2026-10-12（含审查修复复验）——`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` **674 通过 / 21 跳过 / 0 失败**（基线 623 通过 / 20 跳过 + 本卡新增 51 通过 = 49 单测 + 2 对账，+1 跳过为门控真实对账；审查修复为文档/注释级，无代码行为变化、测试数字不变）；fixture 对账与 CLI golden 全粒度一致——tokens 五分量逐位一致（全期 input 1,002,331 / output 457 / cache_creation 5 / cache_read 109,028 / total 1,111,821），成本经容差一致（唯一浮点累加差异 3e-17 量级），SQLite 优先去重实证（02-02 gpt-5.2 cost 0.51 逐位替换 0.50）；门控真实对账本机无数据 Skip 验证通过。仓库内无真实用户数据（fixtures 全合成）。
