---
id: I-021
title: Antigravity 适配器
type: feature
priority: P3
size: L
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 Antigravity 会话 SQLite（~/.gemini/antigravity*/conversations/ 与 ~/.config/antigravity/conversations/ 下的 .db），产出 agent=Antigravity 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/antigravity/。多默认根、ANTIGRAVITY_DATA_DIR 多根支持；依赖 gen_metadata 表，steps schema 与 trajectory_metadata_blob（protobuf blob）提供时间戳回退——开工前先做 spike 验证 blob 解析的最小实现（或按蓝本裁剪不必要字段），否则拆卡。本机暂无数据。

## Acceptance Criteria

- [x] 多根发现与 schema 门槛（gen_metadata 必需）
- [x] usage/时间戳提取（含 protobuf blob 回退方案验证）
- [x] 解析失败显式上报而非静默空报（对齐蓝本）
- [x] fixtures + 单元测试；接入本地采集管道

## Subtasks

- [x] Spike：trajectory_metadata_blob 最小 protobuf 解码验证（结论入 Dev Log，蓝本即手写解码 → C# 手写 varint/length-delimited 解码器、无 protobuf 库依赖）
- [x] 蓝本逐条研读（paths.rs / parser.rs / loader.rs / report.rs；report.rs 归 CLI 报告层不在移植范围）
- [x] `AntigravityLogLocator`（paths.rs）：ANTIGRAVITY_DATA_DIR 多根（逗号分隔、去空白、空段丢弃、**设置即只用配置根**——env::var 成功即 return，空值不回退默认，Kilo 同款）→ 默认六根 ~/.gemini/{antigravity,antigravity-cli,antigravity-ide,antigravity-backup,antigravity-acp} 与 ~/.config/antigravity（固定清单非 glob，存在者不做过滤交给收集后去重）→ 每根 conversation_dir 双形态（&lt;root&gt;/conversations 存在即用它否则根本身）→ 递归收集 ".db"（共享 SourceFileCollector：符号链接不跟随、读不了目录静默跳过、扩展名恰 ".db"）→ 规范化路径跨根去重（蓝本 fs::canonicalize；C# ResolveLinkTarget 终点解析 + GetFullPath 兜底）→ 全体路径排序（蓝本 paths.sort()）
- [x] `AntigravityProtoReader`（parser.rs decode_fields / read_varint / field_varint / field_bytes / field_bytes_all / field_text 逐字移植）：wire 0/1/2/5、varint ≤10 字节（第 10 字节 payload&gt;1 溢出）、字段选取语义 varint/text 最后重复胜出 / bytes 第一个 / repeated 全量保序 / 文本须 UTF-8 且去空白非空
- [x] `AntigravityParser`（parser.rs 主体）：gen_metadata（必需表，SELECT idx,data ORDER BY idx ASC）→ steps（可选表，table_exists 门 + WHERE metadata IS NOT NULL）→ trajectory_metadata_blob（可选表，ORDER BY rowid ASC 首个有效候选）→ GeneratorMetadata（chat_model field 1 内：usage 4 / retries 17 / generation_info 9→4 时间戳 / model 文本 19,21 / model_id 3）与 StepMetadata（usage 9 / retries 28 / model_info 24 [1,7,12,8] / 时间戳 8 回退 1）→ ModelUsage（字段 1/2/3/4/5/6/7/9/10/11/12）→ 事件构建（token 桶换算 u64 saturating 语义、身份键 response:/provider:/message:、时间戳 rank 3/1/0 链、身份键时间戳回查）→ merge_usage_event / deduplicate_events（loader.rs 同构：共享任一身份键合并到首槽、链式传递）→ model_name_from_id / normalize_antigravity_model 归一化表逐字移植
- [x] `AntigravityAdapter`（loader.rs）：库发现 → 逐库只读解析（SqliteReadonly + 托管 DEFERRED 事务快照）→ 跨库身份去重 → 时间戳稳定排序 → 统一记录（Agent/ProjectPath "Antigravity"、CostUsd 恒 null 交定价引擎、缺省模型 "gemini-internal-model"）
- [x] 接线：`IAntigravityAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十九路 concat + AppServices 单例
- [x] 单元测试 52 项（tests/CCUsage.Infrastructure.Tests/Antigravity/，蓝本 paths/parser/loader 内联测试同名场景逐一对齐 + 载荷角落 + env 语义 + 连接池幂等回归守卫 + 仅坏库无有效库显式计数）
- [x] fixture（全合成 conversations/**&#47;*.db，protobuf blob 按 spike 验证的蓝本 test_support 编码器 python 手工构造）+ golden 录制（ccusage 20.0.29 antigravity daily --json --offline --timezone UTC，ANTIGRAVITY_DATA_DIR 钉 fixture）+ 对账测试 2 项
- [x] 门控真实对账就绪（RealDataReconciliationTests.Real_antigravity_data_reconciles_with_cli；本机无数据验证 Skip）
- [x] I-024 补录 **AG-1**（候选计价链欠账，按 I-020 教训实现时直接登记不声称既有）

## Dependencies

- I-006（SQLite 骨架）

## Test Cases

### TC-001: 蓝本语义单元测试（tests/CCUsage.Infrastructure.Tests/Antigravity/AntigravityAdapterTests.cs，52 项）

Steps:
1. 蓝本 paths.rs 内联测试同名场景：六独立默认根发现、conversation_dir 双形态 override（结果全体排序）、canonicalize 别名去重（conversations/.. 与根同库折叠）。
2. env 语义：ANTIGRAVITY_DATA_DIR 设置但空白 → 空根不回退默认（蓝本 env::var 成功即 return）、逗号列表去空白/空段丢弃。
3. 蓝本 parser.rs 内联测试同名场景：protobuf 字段重复语义（varint/text 最后胜出、bytes 第一个、repeated 全量保序）、steps retry 字段 28 生产形态、model_id/占位符/显示名归一化表（1318/1319/1320/1298/1299/1300/1071/1072/1073 九组 + 旧别名 12 组 + ≥1000 占位符映射）。
4. 蓝本 loader.rs 内联测试同名场景：代序 continuation（idx 2 先写、ORDER BY idx 还原序）、model_id 246 不计 input、多库一致性（env 多根 + 时间戳排序）、跨库 response 身份去重（token max 合并、response id rank 保持）、steps/retries 收集与源副本去重（同 rank 更早时间戳胜出）、step/retry 用量归属各自 model_id 模型、必改别名清单与未提供模型保持原名、trajectory 时间戳回退（generation 无时间戳时 2026-05-09T04:13:20.456Z）。
5. 空快照与坏数据：0 字节库 debug 跳过（FilesRead 计入、不降级）、仅 0 字节 → parse 返回 null + NoData、坏 blob（0x0a,0x01,0x80）与有效库共存 → MalformedLines 显式计数 + Partial、not-a-db → 库打开失败问题标记、缺 gen_metadata 表 → 查询失败、steps 存在但 schema 不符 → 整库 0 条、steps/trajectory 缺表无问题。
6. 载荷角落（仓库 I-004 显式化）：非 token-bearing 零用量行计 LinesSkipped、空白模型文本按缺失（field_text trim 过滤）走缺省 "gemini-internal-model"、trajectory 首个有效候选（首行无候选次行补位）、秒=0 时间戳回退 mtime、身份键时间戳回查（无时间戳 step 事件复用 gen 事件 rank 3 时刻）、非 .db 后缀不发现。
7. 仅坏库无有效库（蓝本 propagates_malformed_metadata_errors 同名场景）：0 记录 + MalformedLines=1 + NoData（family precedence，与"坏 blob + 有效库共存 → Partial"互为对照）。
8. 回归守卫：重复扫描幂等（蓝本 BEGIN DEFERRED 裸 SQL 移植在连接池下二次扫描全库报 "cannot start a transaction within a transaction" 的回归——托管事务修复后守卫）。

Expected:
- 全部断言与蓝本内联测试/注释语义一致；0 警告 0 错误。

Result: ✅ 52 通过 / 0 失败（2026-10-11 首收口 51 项；2026-10-12 独立审查补 N3 用例后 52 项，含蓝本 paths.rs 三例、parser.rs protobuf 语义/归一化表、loader.rs 八例与 propagates_malformed_metadata_errors 同名移植；另 1 项既有 GeminiCli 防泄漏测试命中同名过滤，合计 53）。

### TC-002: fixture 对账（tests/CCUsage.Reconciliation.Tests/AntigravityFixtureReconciliationTests.cs，2 项）

Steps:
1. fixture：fixtures/antigravity/conversations/（全合成 session.db / second.db / unpriced.db / empty.db——gen_metadata 代序续行、"Gemini 2.5 Pro" 文本归一化、model_id 312 映射、steps 同身份副本合并、provider 24 行、trajectory 回退、零用量行、0 字节库、claude-4-sonnet cache_creation、未定价模型；protobuf blob 按蓝本 test_support 编码器 python 手工构造）。
2. `AggregateAntigravityAsync(AntigravityRoot)` → PricingEngine 内置快照 → UTC 日聚合，与 golden（ccusage 20.0.29 antigravity daily --json --offline --timezone UTC，ANTIGRAVITY_DATA_DIR 钉 fixture）经 ReconciliationComparator 比较（日期 × 模型 + 每日 + 全期三个粒度）。
3. 链路健康检查断言：FilesDiscovered=4、FilesRead=4、LinesSkipped=1（零用量行）、MalformedLines=0、DuplicateRecords=1（steps 副本合并）、RecordCount=7、Status=Ok、UnpricedModels=[mystery-model-x]。

Expected:
- tokens 五分量全粒度逐位一致；成本经比较器容差一致（fixture 规避后两侧计价公式完全同式，实测逐位一致）。

Result: ✅ 2 通过 / 0 失败（2026-10-11）。对账数字：3 天 × 5 模型键 + 全期——input 2,550 / output 1,165 / cache_creation 40 / cache_read 2,500 / total 6,255 / cost 0.00796（05-05 gemini-2.5-pro 0.00355 + gemini-2.5-flash 0.00111 = 0.00466；06-09 claude-4-sonnet 0.0033；06-15 mystery-model-x 双侧 0），与 golden 逐位一致。

### TC-003: 门控本机真实数据对账（RealDataReconciliationTests.Real_antigravity_data_reconciles_with_cli）

Steps:
1. `CCUSAGE_RECONCILE_REAL=1` 且本机存在 Antigravity 数据（ANTIGRAVITY_DATA_DIR / 六默认根下有 .db）时运行：CLI antigravity daily --json --offline 与自研全链路对账。
2. 任一条件不满足时 Skip 而非 Fail。

Expected:
- 本机（无数据）Skip；有数据机器全链路 tokens 精确一致（含 reasoning 的行在 totalTokens/cost 上 fail-loud——统一记录无 extra 字段，见偏差⑤）。

Result: ✅ Skip 验证通过（2026-10-11，CCUSAGE_RECONCILE_REAL=1 下 Skipped: 1；数据探测复用产品定位器与 CLI 同一套逻辑）。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/antigravity/），尚未开始实现。

### 2026-10-11（Spike：trajectory_metadata_blob 最小解析）

- **方法**：按蓝本 parser.rs `#[cfg(test)] test_support` 的 blob 构造器（metadata_blob / step_metadata_blob / trajectory_metadata_blob）用 python 逐字构造 protobuf 字节，再以拟移植 C# 的最小手写解码器（wire 0/1/2/5 + 字段号过滤）解码并断言字段还原（脚本含重复字段语义、timestamp 钳位、畸形 blob 拒绝面、10 字节 varint 溢出边界，全部通过）。
- **结论**（实现按此执行）：
  - blob 是标准 protobuf wire format；蓝本本身即**手写解码、只读所需字段号**（无 prost 派生类型），C# 侧按蓝本字段号逐字段手写解码是首选且零新依赖（不引 protobuf-net）。
  - **trajectory_metadata_blob**：仅 field 2（wire 2，length-delimited）→ google.protobuf.Timestamp 形状消息（field 1 varint 秒、field 2 varint 纳秒）；秒 &gt; 0 才有效、纳秒钳 999_999_999、毫秒 = 秒×1000 + 纳秒/1e6。最小实现 = DecodeFields + FieldBytes(2) + ParseTimestampMessage。
  - **gen_metadata（GeneratorMetadata）**：field 1（chat_model 消息，缺失即错）内 usage=4、retries=17（每项 field 2 → ModelUsage）、时间戳=9（generation_info 内 field 4 → Timestamp）、model 文本=19/21、model_id=3（非零才生效）。
  - **steps（StepMetadata）**：usage=9、retries=28（同 retry 形状）、model_info=24（model_id=1、provider=7、model=12/8）、时间戳=8 回退 1。
  - **ModelUsage**：1=model_id（非零）、2=input、3=total_output、4=cache_creation、5=cache_read、6=provider（非零）、7=message_id、9=reasoning、10=visible_output、11=response_id、12=provider_assigned_message_id。
  - 字段选取语义：varint/text 取最后一个重复（蓝本 .rev().find_map）、bytes 消息取第一个（find_map）、repeated bytes 全量保序（field_bytes_all）、文本须 UTF-8 可解码且去首尾空白非空（蓝本 from_utf8().ok() + trim 过滤）。
  - 畸形数据（蓝本测试 vec![0x0a,0x01,0x80]）：外层合法、报错发生在内层 chat_model varint 截断——与蓝本 "truncated protobuf varint" 同拒绝面。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 antigravity/ 的 paths.rs / parser.rs / loader.rs 逐条对齐；蓝本许可 MIT License，Copyright (c) 2025 ryoppippi，声明保留在 AntigravityLogLocator / AntigravityProtoReader / AntigravityParser / AntigravityAdapter 文件头；fixtures 全部合成——conversations/ 下四份 .db 由 python3 sqlite3 生成、blob 按蓝本 test_support 编码器手工构造，无真实数据入仓）：

- **多根与库发现（蓝本 paths.rs 逐字对照）**：ANTIGRAVITY_DATA_DIR 逗号分隔、逐段去空白、空段丢弃、**无存在性过滤、无根级去重**（去重在收集后按规范化路径做）；**设置即只用配置根**（蓝本 `env::var` 成功即 return，空值 → 空根列表不回退默认——Kilo 同款，与 OpenClaw "空白回退默认"相反）。默认六根为固定清单（卡片 Background 的 "antigravity*" 是描述性写法，蓝本无 glob）：~/.gemini/antigravity、-cli、-ide、-backup、-acp 与 ~/.config/antigravity。conversation_dir 双形态（&lt;root&gt;/conversations is_dir 则用之，否则根本身）→ collect_files_with_extension 递归 ".db"（共享 SourceFileCollector）→ 蓝本 fs::canonicalize 去重（C# 用 `ResolveLinkTarget(returnFinalTarget: true)` 近似 + GetFullPath 兜底，失败回退原路径）→ 蓝本 paths.sort() 全体排序（跨根统一 Ordinal，非根序拼接）。
- **schema 门槛**：gen_metadata 为**必需表**（无 table_exists 检查，直接固定列查询——表缺失/列不符由 SqliteException 拒绝，蓝本查询失败同拒绝面）；steps 与 trajectory_metadata_blob 为可选表（SqliteTableSchema.TableExists 门控，蓝本 table_exists 同式）；steps 存在但 schema 不符 → 查询失败整库 0 条（蓝本报错含 "steps"，测试 propagates_present_optional_table_schema_errors 同场景）。
- **时间戳回退链（逐字保真）**：metadata timestamp（gen 的 field 9→4、step 的 field 8 回退 1，rank 3）→ **身份键回查**（usage.response/provider/message 身份键在 identity_timestamps 表中找已见时刻——蓝本 append_usage_event 的 or_else 分支）→ trajectory blob（rank 1，按 rowid ASC 逐行、首个有效候选生效——蓝本 timestamp.is_none() 才写入，前序行无候选时后续行可补位）→ 文件 mtime（rank 0，蓝本 file_modified_timestamp）。
- **token 桶换算（蓝本 append_usage_event u64 saturating 语义）**：total_output = max(total_output, visible + reasoning)；output = max(visible, total_output − reasoning)；reasoning = max(reasoning, total_output − output)；total = input + cc + cr + total_output（事件内部）。模型链：usage.model_id 映射 → context.model 归一 → "gemini-internal-model"。消息 id 优先级 response(3) &gt; provider_assigned(2) &gt; message(1)。身份键时间戳更新规则：rank 更高或同 rank 更早者胜。
- **跨库身份去重（蓝本 loader.rs deduplicate_events 逐字移植）**：事件按共享任一身份键（response:/provider:/message:）合并到首个槽位、合并链式传递（槽位 A 吸收槽位 B 时 B 置空）、无身份键事件独立成槽；合并规则（蓝本 merge_usage_event）：四桶取 max、total 重算、缺省模型让位具名模型、provider 补缺、时间戳高 rank/同 rank 更早者胜、消息 id 高 rank 胜、身份键并集。
- **架构偏差外的移植裁剪**：蓝本 parse_sqlite_file 的 `BEGIN DEFERRED TRANSACTION` 快照用**托管事务**移植（Microsoft.Data.Sqlite `BeginTransaction()` 默认即 DEFERRED；裸 SQL BEGIN 不被托管连接跟踪、连接池归还时不回滚——复现实证：第二次扫描全部库报 "cannot start a transaction within a transaction" 0 记录，fixture 对账第 2 个用例与重复扫描回归守卫双覆盖；托管事务 Dispose 干净归还连接池，快照语义与蓝本等价）。
- **诊断口径**（仓库 I-004 显式化，行级归因登记）：FilesDiscovered = 发现的 .db 数、FilesRead = 打开并完成检查的库数（含空快照库）、LinesSkipped = 非 token-bearing usage 计数（蓝本静默丢弃）、MalformedLines = protobuf blob 解析失败的库数、库打开/查询/列类型失败仅记问题标记（Kilo/Goose 同款）、DuplicateRecords = 身份键合并折叠的事件数（events.Count − deduped.Count）；Status 仅在 records&gt;0 时降级 Partial（family precedence，I-020 备案同款——仅失败无记录时 NoData，失败面经 FilesRead/MalformedLines 显式可见）。
- **管道接线**：`IAntigravityAdapter` 单例 + LocalIngestionDataSource 第十九路 concat；适配器实例无扫描状态（每次全量重读），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：无需新增——fixture 模型（gemini-2.5-pro / gemini-2.5-flash / claude-4-sonnet）两侧快照同价（cache_creation 走 claude 行：CLI 对缺失 cache_write 按 input×1.25 推导，gemini 行规避）；未定价行 mystery-model-x 双侧 0 成本。裸模型名定价候选链欠账见偏差④（**AG-1**，本卡实现时按 I-020 教训直接补录 I-024，不私自接线）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **report.rs 未移植**：summarize_entries / report_from_rows / run（CLI 报告层）不在桌面适配器范围（I-020 同款边界）；daily/weekly/monthly/session 聚合由桌面 facts 管道承担。
- ② **计价端到端未接线（AG-1）**：蓝本 calculate_antigravity_cost 的三层候选链——①flash effort 变体 exact-with-fallback（含模型别名归一重试）→ ②基础名 gemini-3.x-flash → ③google provider（3/24/30）时拼 google/ · gemini/ · vertex_ai/ · openrouter/google/ 前缀——桌面引擎按裸模型名定价，不命中记 0 + UnpricedModels（fail-loud）。蓝本 missing_antigravity_pricing 的候选集缺失报告与模型别名解析（resolve_model_name）同属此欠账。已作为 **AG-1** 登记 I-024（Dependencies 追加 I-021、接线 AC 扩为九源），本卡不做 resolver 接线。
- ③ **计价 output 分量取 total_output（含 reasoning）**：蓝本 cost_usage.output = event.total_output_tokens（reasoning 按 output 价计费），桌面引擎按 UsageRecord.OutputTokens（visible output）计价——含 reasoning 的可计价行成本偏低（fail-loud）。随 AG-1 一并考虑（需要记录侧 reasoning 分量或引擎侧补偿）。
- ④ **reasoning 不落统一记录（I-009 OpenCode 同款）**：CLI daily 的 totalTokens 含 extra 分量（= total_output − output，一致数据下即 reasoning），桌面 UsageRecord 无 extra/reasoning 字段——含 reasoning 的真实数据在 totalTokens 上响亮失败（fail-loud）；fixture 全部行 reasoning=0（total_output = visible）使五分量对账可行，reasoning 桶换算语义由单元测试覆盖（蓝本 loader 首测 50/150/200 场景同款）。
- ⑤ **行级/库级坏数据显式降级**（蓝本 cli_error 整次加载失败）：桌面按"该库本轮 0 条 + 问题标记/计数"降级——protobuf 解析失败计 MalformedLines 并丢弃该库已解析事件（蓝本库内全弃语义等价）、打开/查询失败仅问题标记（Kilo/Goose 同款）、其他有效库不受拖累；Status 的 family precedence（仅失败无记录 → NoData）见上文诊断口径。
- ⑥ **canonicalize 近似**：蓝本 fs::canonicalize；C# 用 ResolveLinkTarget 终点解析 + GetFullPath 兜底、失败回退原路径（I-012 Goose 同款已登记差异——不解析时经链接指向同一物理库的候选依赖路径去重；身份键去重作为第二道防线）。
- ⑦ **home 解析**：共享 JsonlHomePath 的 USERPROFILE 优先（蓝本 home.rs HOME 优先，I-006 系已登记差异同款）；env 路径 ~ 展开 + GetFullPath 为仓库定位器共有增强。
- ⑧ **u64 → long 边界**：blob varint 读取层把 u64 溢出钳到 long.MaxValue（I-017 偏差⑥同款）；u64 saturating 算术改 long saturating（现实值远小于边界，结果一致）；时间戳毫秒超 DateTimeOffset 表示域按"时间不可用"归纪元（I-020 偏差⑧同款）。
- ⑨ **Unicode 大小写折叠 vs 蓝本 ASCII-only**（独立审查 N1 登记）：归一化表匹配层 C# 用
  `ToLowerInvariant()`（Unicode 折叠：U+212A KELVIN SIGN → 'k'、U+0130 → i+组合点），蓝本
  `to_ascii_lowercase()` 非 ASCII 大写原样保留——经 default 分支 converted 输出时大小写形式可能
  不同（例：模型文本含 U+212A 且命中 converted 分支，C# "…k…" vs 蓝本 "…K…(U+212A)"）。真实
  ASCII 模型名行为逐位一致；effort 变体/别名表/前缀判定（"gemini-"/"claude-"/"gpt-" 均为 ASCII）
  对非 ASCII 输入两侧同样不命中，仅 default 分支回显路径存在理论差异，未改代码按登记处置。
- ⑩ **provider 解码保真、读后即弃**：ModelUsage.field 6 / model_info.field 7 的 provider 参与事件（usage.provider ?? context.provider）但统一记录无 provider 字段（I-020 偏差②同款）；解码保真保留供 AG-1 接线使用，蓝本 model_candidates 的 provider 前缀分支桌面侧暂无落点。
- ⑪ **无 since/until 窗口 / Display 模式不复现**：桌面适配器统一全量出账（I-008 偏差⑦同款架构；蓝本 Display 模式成本记 0 桌面无对应模式）。
- ⑫ **进度条 / debug_log 不复现**：蓝本 progress::track_usage_load 与 debug_log("Skipping uninitialized...") 为 CLI 展示层；桌面以 FilesRead 计数 + 文档注释显式化同一信息。

### 2026-10-12（独立审查修复）

独立审查结论：**approve-with-nits，可收口，无阻塞项**（protobuf 字段号表逐个核对全对、AG-B1 事务论证成立、AG-1 登记完整）。三条 P3 顺手修复：

- **[N1 · 登记（不修代码）]** 归一化匹配层 `ToLowerInvariant()`（Unicode 折叠）vs 蓝本 `to_ascii_lowercase()`：非 ASCII 大写经 default 分支 converted 输出的大小写形式理论差异，真实 ASCII 模型名逐位一致——补入偏差清单⑨（原⑨⑩⑪顺延为⑩⑪⑫）。
- **[N2 · 注释]** ParseSqliteFile 三处模型归一三元链（generationModel 探测 / step 循环 / generation 循环）补不可达性注释：与蓝本 `.and_then(normalize).or_else(...)` 形式差异（Model 存在但归一为 null 时不回退 model_id）不可达——field_text 保证 trim 非空、非空输入归一恒非 null。
- **[N3 · 用例]** 补蓝本 loader.rs `propagates_malformed_metadata_errors` 同名场景（仅坏 blob 库、无有效库共存）：0 记录 + MalformedLines=1 + NoData（family precedence），与既有"坏 blob + 有效库 → Partial"用例互为对照；单测 51 → 52，全量 727 → 728。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| AG-B1 | high | 蓝本 BEGIN DEFERRED TRANSACTION 若按裸 SQL `BEGIN` 移植，Microsoft.Data.Sqlite 连接池归还时不回滚未跟踪事务——第二次扫描（应用第二次刷新）全部库报 "cannot start a transaction within a transaction"，0 记录静默空报（fixture 对账第 2 用例与重复扫描实测复现） | Resolved | 改用托管事务 `connection.BeginTransaction()`（默认 DEFERRED，Dispose 回滚、连接池干净归还）；`Repeated_scans_are_idempotent` 回归守卫 + fixture 对账双测试覆盖 |

## Verification

- Related files: `src/CCUsage.Core/Abstractions/IAntigravityAdapter.cs`、`src/CCUsage.Infrastructure/Antigravity/AntigravityLogLocator.cs`、`src/CCUsage.Infrastructure/Antigravity/AntigravityProtoReader.cs`、`src/CCUsage.Infrastructure/Antigravity/AntigravityParser.cs`、`src/CCUsage.Infrastructure/Antigravity/AntigravityAdapter.cs`、`src/CCUsage.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第十九路）、`src/CCUsage.App/DependencyInjection/AppServices.cs`、`tests/CCUsage.Infrastructure.Tests/Antigravity/AntigravityAdapterTests.cs`、`tests/CCUsage.Infrastructure.Tests/Antigravity/AntigravityTestSupport.cs`（蓝本 test_support blob 编码器移植）、`tests/CCUsage.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（十九参构造）、`tests/CCUsage.Reconciliation.Tests/AntigravityFixtureReconciliationTests.cs`、`tests/CCUsage.Reconciliation.Tests/fixtures/antigravity/`（全合成四库 fixture）、`tests/CCUsage.Reconciliation.Tests/fixtures/golden/antigravity-fixture-daily-v20.0.29.json`、`tests/CCUsage.Reconciliation.Tests/fixtures/golden/README.md`（录制命令）、`tests/CCUsage.Reconciliation.Tests/Support/{UsageChainAggregator,FixturePaths,RealReconciliationEnvironment}.cs`、`tests/CCUsage.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`、`tests/CCUsage.Reconciliation.Tests/RealDataReconciliationTests.cs`（门控真实对账）、`docs/kanban/issues/I-024-candidate-pricing-wiring.md`（AG-1 补录）
- How to run/verify: `dotnet build CCUsage.sln`（0 警告 0 错误）；`dotnet test CCUsage.sln`（全绿）；单测 `dotnet test tests/CCUsage.Infrastructure.Tests --filter FullyQualifiedName~Antigravity`（53 项 = 本卡 52 + 既有 GeminiCli 防泄漏 1）；fixture 对账 `dotnet test tests/CCUsage.Reconciliation.Tests --filter FullyQualifiedName~AntigravityFixture`（2 项）；golden 重录：`ANTIGRAVITY_DATA_DIR="$PWD/tests/CCUsage.Reconciliation.Tests/fixtures/antigravity" npx --yes ccusage@20.0.29 antigravity daily --json --offline --timezone UTC > tests/CCUsage.Reconciliation.Tests/fixtures/golden/antigravity-fixture-daily-v20.0.29.json`（命令已登记 fixtures/golden/README.md）
- Results: 2026-10-11——`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test CCUsage.sln` **728 通过 / 22 跳过 / 0 失败**（基线 674 通过 / 21 跳过 + 本卡新增 54 通过 = 52 单测 + 2 对账，+1 跳过为门控真实对账；2026-10-12 独立审查 N3 补用例后复验）；fixture 对账与 CLI golden 全粒度一致——tokens 五分量逐位一致（全期 input 2,550 / output 1,165 / cache_creation 40 / cache_read 2,500 / total 6,255），成本 0.00796 逐位一致（fixture 规避后两侧计价同式）；门控真实对账本机无数据 Skip 验证通过。仓库内无真实用户数据（fixtures 全合成）。未 git commit（PM 独立审查后提交）。
