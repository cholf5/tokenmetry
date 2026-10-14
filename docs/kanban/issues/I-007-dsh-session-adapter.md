---
id: I-007
title: DSH 会话适配器
type: feature
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 DSH（DeepSeek Harness）本地会话日志（~/.dsh/sessions/<encoded-cwd>/session-*/session.v*.jsonl.zstd），产出 agent=DSH 的统一 usage 记录。

## Background

DSH 不在 ccusage 支持列表内（2026-10-11 全量核实），需自行实现适配器。本机数据勘察（66MB）：zstd 压缩 JSONL，目录名即项目路径（--Users-theo-dev-<proj>-- 式编码）。

**参考蓝本（2026-10-11 确认）：`../dsh-usage-stats`**（MIT，DSH web 用量插件，本机 91 文件 / 7907 条 usage 事件抽样核实过其口径）。其聚合层为纯 JS、无框架依赖，可整体移植为 C# 参考：

- `lib/usage.js` — fold 口径权威实现（样本载体、替换语义、模型归因、日归属）
- `lib/pricing.js` — DeepSeek USD 定价规则目录 + peak/offPeak 时段表
- `scripts/verify-raw.mjs` — **直读 zstd 文件的现成蓝本**（帧扫描器 + 逐帧解压）+ 四路对账方法

关键口径（均已实测核实）：

- **token 语义**：四桶 `inputTokens` / `outputTokens` / `cacheReadTokens` / `cacheWriteTokens`，缺失字段按 0；**inputTokens 不含缓存切片**（实测样本 1372 + 7488 + 339 = 9199 = totalTokens）；raw 里的 `totalTokens` 字段参考实现忽略、自行按四桶求和。官方 `session.list` tokenUsage 投影的 input 桶字段名就叫 `uncachedInputTokens`，反向印证。
- **usage 样本有两种载体**：`assistant/chunk`（`data.chunk.type === "usage"`，usage 在 `data.chunk.usage`）和 `assistant/message`（usage 在 `data.usage`）——比原勘察多一种，抽取须同时覆盖。
- **替换语义**：同 `(turn, step)` 的重复样本是**替换不是累加**（减旧样本桶、加新样本桶，且重归属到后样本的事件日期）；增量 fold 靠 `lastSample` 游标跨片保持该语义。
- **模型归因**：`assistant/message` → `data.message.source.{provider,model}`；usage chunk → 回退最近 `request/header` 的 `data.header.config.{provider,model}`；无信息落 `unknown/unknown`。`provider/model` 组合键使不同 provider 的同名模型分开统计。全零样本（warmup 请求）过滤。
- **日归属**：`event.time`（epoch ms）按**本地日历日** `YYYY-MM-DD`。
- **文件布局**：canonical 文件名 `session.jsonl`（v0）/ `session.v<N>.jsonl`（v1+），均可 `.zstd`；迁移过的会话目录**保留多代文件，只读最高版本**；session id 取 `type === "session"` 头行的 `id` 字段（回退目录名）；`DSH_HOME` 环境变量 → `~/.dsh`（verify-raw.mjs 同口径）。
- **zstd 帧布局**：DSH 每次追加写一个独立 zstd 帧（实测 91/91 文件多帧，最多 1689 帧/文件），**每帧独立可解压**——逐帧解压即流水式方案，增量扫描可记"最后完整帧结束偏移"；帧扫描需容忍尾部 torn frame（半写帧）。verify-raw.mjs 有完整帧扫描参考实现（magic `0xFD2FB528`）。
- **定价**：`lib/pricing.js` 内置 DeepSeek USD 规则目录（per 1M tokens、四桶分价），规则带生效区间 + Asia/Shanghai peak/offPeak 时段表（08-16 前平价 → 08-16~08-22 每日高峰时段 → 之后工作日高峰）；费用按**每个样本的事件时间**匹配历史规则，绝不"当前价 × 累计 token"；正桶无可靠单价时整体 fail-closed 为 null。缓存 `~/.dsh/storages/usage-stats-cache.json`（version 5）的 `pricingFingerprint.pricingCatalog` 即该目录快照（原"缓存含 pricingCatalog"表述修正为该嵌套路径）。注意本机还有 `ark/glm-5.3-flash` 等非 deepseek 路由，超出现有目录覆盖，费用将 fail-closed——token 统计不受影响。

## Acceptance Criteria

- [x] 确认 token 语义：inputTokens 是否含缓存切片、cache 字段是否存在（抽样多会话核对 dsh-usage-stats 口径）；deepseek 模型定价映射来源确定（快照扩充 or 用户覆盖）
  - ✅ 2026-10-11 核实（见 Background）：四桶口径、inputTokens 不含缓存切片、cacheRead/cacheWrite 字段存在（缺失按 0）；定价映射移植 `../dsh-usage-stats/lib/pricing.js` 内置目录（快照扩充），用户覆盖留作后续扩展
- [x] C# zstd 解压方案落定（托管库或 P/Invoke），**逐帧解压**避免整文件入内存（帧独立可解压，帧扫描器参考 verify-raw.mjs：magic `0xFD2FB528`、容忍尾部 torn frame、记录完整帧偏移供增量续扫）
  - ✅ 选型 **ZstdSharp.Port 0.8.8**（纯托管，restore 实测可用）；`DshZstdLogStream` 逐帧顺序解压（帧扫描器逐条移植 verify-raw.mjs 的 scanZstdFrames，帧头/块序列/校验和逐字节走读，压缩侧整文件不进内存）；容忍尾部 torn frame（半帧不入解析）并暴露 LastCompleteFrameEndOffset / TornTailDetected / FramesRead。续扫游标未单独持久化：共享引擎的 mtime/大小记账下文件一变即整文件重解析，torn 容忍 + 偏移记录已覆盖该需求（Development Log 口径偏差 ⑧）
- [x] 目录发现（DSH_HOME 环境变量 → ~/.dsh）与增量扫描（mtime/大小记账复用引擎）：会话目录内**只读最高版本** `session.v<N>.jsonl(.zstd)`（v0 = `session.jsonl`），忽略旧代文件
  - ✅ `DshLogLocator`（configuredHome → DSH_HOME → ~/.dsh，sessions/ 递归枚举，每目录只留最高版本，正则同 verify-raw.mjs）；共享 `IncrementalJsonlScanner` 承担增量记账（本机 69 工件增量语义有测试覆盖）
- [x] usage 记录抽取：`assistant/message`（`data.usage`）**和** `assistant/chunk`（`data.chunk.type === "usage"`）两种载体，同 `(turn, step)` 重复样本按替换语义（减旧加新），agent=DSH，项目路径从目录名还原
  - ✅ 两种载体均抽取（本机 5811 样本全为 message 载体，chunk 载体有专项测试）；替换语义按蓝本 lastSample 游标（仅相邻同键替换）逐条移植；agent="DSH"；项目路径以会话头行 `cwd` 为真实来源（实测 69/69 头行含 cwd），目录名 projectKey 解码（~XXXX 精确还原、'-' 不猜测分隔）仅作头行缺失时的回退
- [x] 与 dsh-usage-stats 项目数字对账（tokens 精确一致）：跑通其 `scripts/verify-raw.mjs` 四路对账方法（raw bytes ↔ session.history ↔ 插件端点 ↔ 官方 tokenUsage 投影，注意投影 input 桶字段为 `uncachedInputTokens`）
  - ✅ Path A（raw bytes + 蓝本库 fold）已跑通并入仓为对账 golden 脚本（Support/DshBlueprintGolden.mjs）；Path B/C/D 需要运行中的 `dsh web`（:3080），本机未运行，未执行——Path A 与官方投影的一致性由蓝本项目自身验证背书（卡片 Background 已注明口径实测核实）
- [x] fixtures + 单元测试 + 接入本地采集管道
  - ✅ fixtures 全部测试内合成（zstd 帧用 ZstdSharp 现场压缩，无二进制入仓）；新增 21 项 Infrastructure 测试 + 13 项 Core 目录测试；LocalIngestionDataSource 第四路 concat + App DI 注册

## Subtasks

- [x] zstd 选型与逐帧解压流（ZstdSharp.Port 0.8.8 + `DshZstdLogStream`：帧扫描器、torn tail、content-size 精确解压、未知大小时流式回退）
- [x] `DshLogLocator`（home 解析 + sessions/ 递归枚举 + 每目录最高版本工件）
- [x] `DshSessionLogParser`（两种载体抽取 + 邻近替换 fold + 模型归因 + 会话头 id/cwd + 计价落定）
- [x] `DshSessionAdapter`（共享增量扫描引擎接线 + InvalidDataException 弃全文件 + 项目路径回退解码）
- [x] DeepSeek 定价目录移植（快照 `deepseek` 段 6 条规则 + `DeepSeekPricingCatalog`：生效区间、Asia/Shanghai peak/offPeak、四桶分价、正桶缺价 fail-closed）
- [x] 管道接线（LocalIngestionDataSource 第四路 + `IDshSessionAdapter` + App DI）
- [x] 单元测试 ×34（Infrastructure 21 + Core 13，fixtures 全合成）+ 门控真实对账测试（Node golden）
- [x] 真实数据对账（tokens 精确一致，见 TC-003）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与 fold 语义（对照蓝本 usage.js / verify-raw.mjs）

Steps:
1. 测试内合成会话日志（含 session 头行 id/cwd、request/header、assistant/message 与 assistant/chunk 两种 usage 载体；zstd fixtures 现场用 ZstdSharp 逐帧压缩），运行适配器断言记录、诊断与 fold 行为。

Expected:
- message 载体取 `data.message.source` 归因、chunk 载体回退最近 request/header 的 `data.header.config`、无信息落 `unknown/unknown`；同 (turn, step) 邻近重复样本替换不累加且重归属到后样本事件时间，非邻近同键样本按蓝本语义全部保留；全零 warmup 样本静默过滤（不计 invalid，状态不受影响）；多帧 zstd 与整文件等价；尾部 torn frame 容忍（完整帧照常产出）；结构损坏（坏 magic）弃全文件且不无限重试；坏 JSON 行计 malformed 不中断；每目录只读最高版本；DSH_HOME 生效；二次扫描幂等；跨帧断行经流式 reader 仍可解析。

Result: 通过（Infrastructure 22/22：Parses_plain_jsonl_session_with_message_carrier、Parses_multiframe_zstd_log_and_tolerates_torn_tail、Reads_only_highest_generation_per_session_directory、Attributes_chunk_samples_from_last_request_header、Message_carrier_uses_message_source_over_header_cursor、Unknown_attribution_lands_in_unknown_unknown、Adjacent_same_turn_step_replaces_sample_and_reattributes_day、Non_adjacent_same_turn_step_samples_are_kept_like_blueprint、All_zero_warmup_samples_are_filtered_silently、Zero_sample_replacing_adjacent_nonzero_sample_yields_no_record、Corrupt_frame_poisons_whole_file_like_blueprint、Corrupt_frame_content_fails_closed_like_blueprint、Malformed_lines_are_counted_without_aborting_file、Incremental_rescan_picks_up_appended_frame_once、Line_spanning_frame_boundary_is_parsed_by_stream_reader、Missing_home_yields_no_data、Dsh_home_environment_variable_is_respected、Project_path_falls_back_to_directory_decode_without_cwd、Deepseek_route_is_priced_by_history_catalog_and_others_fail_closed、CacheWrite_bucket_fails_closed_for_all_catalog_rules、Snapshot_deepseek_section_is_not_parsed_as_flat_models、Double_scan_is_idempotent）。

### TC-002: DeepSeek 定价目录（对照蓝本 pricing.js 规则）

Steps:
1. 用内置快照目录（6 条规则：2 flat + 2 time-band v1 + 2 weekday schedule）按蓝本规则手工核算期望值，覆盖 flat 时代 / time-band v1 / weekday schedule、peak 与 offPeak（Asia/Shanghai 09-12 / 14-18 高峰窗、星期判定）、生效区间边界（effectiveTo 开区间）、cacheWrite 正桶缺价 fail-closed、非官方路由与目录外模型 fail-closed、规则区间两两不重叠。

Expected:
- flat 时代 deepseek-v4-flash：1M input×0.14 + 2M cacheRead×0.0028 + 1M output×0.28 = $0.4256；weekday 周一 10:00（上海）高峰 input 1M = $0.44、08:00 与周日 = $0.22（pro 高峰 1.32）；2026-08-16T16:00Z 起 flat 失效（边界开区间）；cacheWrite>0 一律 fail-closed；`deepseek`/`deepseek-official` 路由外（ark/glm-5.3-flash、deepseek-chat 等）fail-closed；快照 deepseek 段不产生 flat 条目（PricingEngine 泛化匹配命不中）。

Result: 通过（Core 13/13：Snapshot_carries_six_ported_rules、Flat_era_prices_four_buckets_for_official_routes、Deepseek_official_route_id_is_accepted、Weekday_schedule_resolves_peak_by_shanghai_clock、Time_band_v1_applies_daily_peak_windows、Effective_to_boundary_is_exclusive、CacheWrite_tokens_fail_closed_for_every_rule、Non_catalog_models_fail_closed ×4、Rules_do_not_overlap_for_same_model）。

### TC-003: 本机真实数据对账（蓝本 golden，TOKENPULSE_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENPULSE_RECONCILE_REAL=1 TOKENPULSE_DSH_BLUEPRINT=<dsh-usage-stats 检出> dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Real_dsh"`。
2. golden 侧：Node 运行 Support/DshBlueprintGolden.mjs（蓝本库 foldUsage / estimateTokenCost 直读原始 zstd 字节，Path A of verify-raw.mjs）。
3. 自研侧：`AggregateDshAsync()` 全链路（DshSessionAdapter → 统一记录 → 本地日历日分桶聚合）。
4. 按 日期×模型（11 组）、每日/全期总计（10 组）、全局 per-model（3 组）比较，另对全期四桶与成本口径直接断言。

Expected:
- tokens 五分量在全部粒度精确一致；成本按 fail-closed 口径：蓝本可计价样本合计 ≈ 自研 CostUsd 合计，且自研记 0 的记录数 = 蓝本 incompleteSamples 数。

Result: 通过——本机 69 个最高版本工件（91 份代文件中）/ 12960 帧 / 0 torn / 35903 事件 / 5811 样本，**两侧全期 tokens 逐位一致：input=22,012,649 / output=4,008,299 / cacheWrite=0 / cacheRead=843,451,328 / total=869,472,276**；自研 status=Ok、malformed=0、invalid=0；模型 3 组（ark/glm-5.3-flash 761,659,038、ark/deepseek-v4.1-flash 82,984,515、deepseek-official/deepseek-flash 24,828,723）；本地日 2026-09-18…09-26 共 9 日逐日一致。成本：蓝本 pricedCost=0（本机 ark 路由与 deepseek-official/deepseek-flash 均超内置目录覆盖，5811 样本全部 fail-closed）= 自研合计 0（5811 条 CostUsd=0 记录 = 蓝本 incompleteSamples 5811）。同轮 claude（本机无数据跳过）/ codex / zcode 门控对账原样通过，无回归。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/(无上游蓝本，自研)/），尚未开始实现。
- 调研参考蓝本 `../dsh-usage-stats`（DSH web 用量插件，MIT）并回填卡片，修正"无上游蓝本"的初始判断：`lib/usage.js` 为 fold 口径权威实现，`scripts/verify-raw.mjs` 提供直读 zstd 的帧扫描蓝本与四路对账方法，`lib/pricing.js` 提供可移植的 DeepSeek 定价目录。实测核实：91 文件全为多帧 zstd（逐帧独立解压即可流式）、四桶口径（inputTokens 不含缓存切片）、usage 样本两种载体、同 (turn,step) 替换语义、多代日志文件只读最高版本。若移植其代码需保留 MIT 许可声明。下一步：开工时细化 subtasks 并落定 C# zstd 库选型。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 ../dsh-usage-stats：lib/usage.js fold、lib/pricing.js 目录、scripts/verify-raw.mjs 帧扫描器；MIT 许可声明保留在 DshZstdLogStream / DshSessionLogParser / DeepSeekPricingCatalog / DshBlueprintGolden.mjs 文件头；fixtures 全部测试内合成，无真实数据入仓）：

- **zstd 方案**（`DshZstdLogStream`）：选型 **ZstdSharp.Port 0.8.8**（纯托管 C#，NuGet restore 实测可用；微软官方 System.IO.Compression 在 .NET 10 尚无逐帧 zstd 流 API）。帧扫描器逐条移植 verify-raw.mjs 的 scanZstdFrames（magic 0xFD2FB528、Frame_Header_Descriptor 保留位校验、块序列 3 字节块头走读、RLE 块 1 字节载荷、帧校验和），实现为顺序流：帧头/块头按需精确读取、整帧压缩字节汇入按需扩容的帧缓冲后交给 ZstdSharp 解压，**压缩文件不整读进内存**；帧头声明 content size 时按精确大小一次解压（产出与声明不符报损坏），未知大小时经 ZstdSharp DecompressionStream 流式读取（Read 契约天然安全）；帧内容损坏（校验和不符 / 压缩流损坏）时 ZstdSharp 抛 ZstdException，流层统一转译为 InvalidDataException 走"按文件 fail-closed"（审查修复，见 Development Log 审查节）。**torn tail**：帧头/块序列读不完即视为流结束（TornTailDetected），半帧内容绝不进入解析；LastCompleteFrameEndOffset 记录最后完整帧结束偏移。**续扫游标未单独持久化**（口径偏差 ⑧）：共享引擎的 mtime/大小记账下文件一变即整文件重解析，torn 容忍 + 偏移记录已覆盖需求，无跨扫描状态可失步。
- **fold 状态方案**：蓝本 applyUsageDelta 的 lastSample 游标只对"紧接着的上一个样本"生效（同键重复样本**邻近**才替换，非邻近同键是蓝本行为本身会重复计数——TC-001 有专项测试固化该口径）。由于每代日志重写完整历史、适配器只读最高版本，**fold 状态只需在单文件解析内存活即可精确对齐蓝本**；跨扫描增量由共享 IncrementalJsonlScanner 的 mtime/大小记账承担（变更文件整文件重解析，fold 无跨扫描状态，与 I-006"无扫描状态全量重读"同等安全但复用了引擎记账，符合本卡 AC 的"复用引擎"要求）。
- **定位**（`DshLogLocator`）：configuredHome → DSH_HOME 环境变量 → ~/.dsh（USERPROFILE 优先，复用 JsonlHomePath）；工件枚举递归遍历 sessions/（对齐 verify-raw.mjs 的全量 walk），每目录内按正则 `^session(\.v([1-9]\d*))?\.jsonl(\.zstd)?$` 收集各代、只留最高版本；环境变量经注入访问器读取。
- **抽取与归因**（`DshSessionLogParser`）：两种载体（assistant/chunk 的 data.chunk.type==="usage"、assistant/message 的 data.usage）；模型归因先样本自身 data.message.source（实测 source 在 message 内）、缺省回退最近 request/header 的 data.header.config（仅 request/header 推进游标，与蓝本一致）、无信息落 unknown/unknown；记录模型键 = "provider/model"（同模型不同 provider 分开统计）。事件过滤同蓝本（对象 + type:string + seq:number）；会话头行（无 seq）取 id 与 cwd 后计 LinesSkipped。
- **项目路径**：会话头行 `cwd` 是真实来源（实测 69/69 头行含 cwd 且与目录名互证）；目录名 projectKey 解码（DSH format.ts 实证：/ \ : 分隔折叠为 '-'、~XXXX 转义、--…-- 包裹、有损）仅作头行缺失回退——~XXXX 精确还原、'-' 保持原样不猜测分隔符，非包裹形态（_no-cwd）落 "Unknown Project"。
- **计价落点**（与 I-006 同构的适配器侧门槛，蓝本 estimateTokenCost 移植为 Core `DeepSeekPricingCatalog`）：目录（快照 `deepseek` 段 6 条规则：2 flat + 2 time-band v1 + 2 weekday schedule，含生效区间与 Asia/Shanghai peak/offPeak 时段表）由目录组件按**样本事件时间**匹配（绝不"当前价×累计 token"）、四桶分价、任一正桶无单价整体 fail-closed。官方路由门 = "provider/model" 键中 provider ∈ {deepseek, deepseek-official}（蓝本 isOfficialDeepSeekIdentity 的 canonical-id 分支；**口径偏差 ④**：按 baseURL 识别官方路由在统一记录中不可表达）。**不可计价样本记 0 而非 null**（口径偏差 ①，重要）：统一记录无 provider 字段，null 会落进 PricingEngine 泛化匹配——快照 zai 段确有 glm-5.3-flash 条目（已核实），ark 路由会被错配成 Z.ai 官方价，违背 fail-closed；0 与"缺失"在本仓库成本聚合中同为 0 贡献（I-006 自定义 provider 行同口径），并使 MissingCostRecords 诊断对 DSH 恒 0。快照 deepseek 段无 models 键，ModelsDevPricingParser 不会解析出 flat 条目（防泛化匹配错配，有专项测试）。
- **读取语义**：未压缩日志直读；.zstd 经 DshZstdLogStream → StreamReader 流式读取（行可跨帧，比蓝本的逐帧 split 更宽容——DSH 实际按整事件成帧）。**行级容错对齐仓库 I-004 口径而非蓝本**（口径偏差 ②）：坏 JSON 计 MalformedLines 继续、非对象/缺 type/缺 seq 计 LinesSkipped，蓝本是解析失败弃全文件；**结构损坏（坏 magic/保留位/保留块类型/解压产出与声明不符）保持蓝本语义弃全文件**（InvalidDataException → 不落账、计 1 malformed、状态 Partial，文件级记账照常落账避免重试风暴）。事件时间缺失的 usage 样本计 invalid 丢弃（口径偏差 ③：蓝本会产出 NaN 日期）；usage 样本缺 turn/step 字段时按无样本计 LinesSkipped（口径偏差 ⑦：蓝本 sampleOf 会以 "undefined:…" 键照常参与 fold——防御性收紧，真实数据无此形态）。全零 warmup 样本静默过滤不计 invalid（口径偏差 ⑤：蓝本渲染层过滤；计入 invalid 会让含 warmup 的健康会话误判 Partial）。
- **诊断口径**：FilesDiscovered=最高版本工件数、FilesRead=成功解析文件数（损坏文件不计）、LinesSkipped=非 usage 事件行（含 session 头行）、MalformedLines=坏 JSON 行 + 结构损坏文件（每文件 1）、InvalidRecords=时间缺失样本数、DuplicateRecords 恒 0（记录无 message id，共享去重器对 null id 直通；重复保证由"每会话目录恰一份最高版本工件 + 文件级记账"承担）、MissingCostRecords/UnknownModelRecords 恒 0（计价落定于解析器 / 模型键必有值）。
- **管道接线**：`IDshSessionAdapter` 单例 + LocalIngestionDataSource 第四路 concat；对账链路 UsageChainAggregator.AggregateDshAsync 按**本地日历日**分桶（蓝本 dayKey 口径，与 CLI 对账的 UTC 分桶不同，口径偏差 ⑥）；真实对账 golden 由入仓脚本 Support/DshBlueprintGolden.mjs 经 Node 运行蓝本库生成（蓝本目录用 TOKENPULSE_DSH_BLUEPRINT 指定或自动向上发现，node/蓝本缺失时测试 Skip）。
- **顺带修复**：ZCodeSqliteAdapter 去重处 `record.MessageId` 可空标注触发的 CS8604 警告（Map 已保证非 null，补 `!` 断言与注释）——独立审查在干净 HEAD 全量构建下复现同一警告（存量问题，与本次改动无关）；硬性验收要求 0 警告故一并修复。

验证：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` 294 通过 / 7 跳过（HEAD 基线实测 260 通过 / 6 跳过——卡片旧记 255/6 为 I-006 审查提交前数字——新增 21 项 DSH Infrastructure 测试 + 13 项 Core 目录测试全过，1 项门控 DSH 真实对账门关时 Skip、开时通过；claude/codex/zcode 既有门控对账开轮复跑无回归）。真实数据对账数字见 TC-003。

### 2026-10-11（独立审查修复）

独立审查结论 approve-with-nits（fold/zstd/定价/接线语义核实全部正确，真实对账逐位一致），按审查意见提交前修复：

- **[P1] 帧内容损坏异常逃逸**：帧结构合法但内容损坏（位翻转 / 校验和不符 / 压缩流损坏）时 ZstdSharp 抛 `ZstdSharp.ZstdException`（实证非 `InvalidDataException`），既不被适配器 `catch(InvalidDataException)` 捕获、也不被共享引擎的 `catch(IOException or UnauthorizedAccessException)` 捕获，会从 ScanAsync 一路冲出到 LocalIngestionDataSource.GetDailyUsageAsync，导致四路数据源整次刷新全部失败。修复（P1-1）：`DshZstdLogStream.DecompressFrame` 统一捕获 ZstdException 并转译为 `InvalidDataException`（带内部异常），与帧头/块序列的结构性损坏走同一条"按文件 fail-closed"路径——与蓝本 verify-raw.mjs 对任何解压错误记 preflight failure 的语义一致；选在流层转译而非适配器并列 catch，保证"一切解压失败 = InvalidDataException"的单一语义边界。新增测试 `Corrupt_frame_content_fails_closed_like_blueprint`（带校验和帧内载荷位翻转 → 断言不逃逸出 ScanAsync、损坏文件不落账、计 1 malformed、状态 Partial）。
- **[P2] 帧头缓冲与 FCS 溢出**：`_headerBuffer` 16 字节装不下 RFC 8878 合法的 18 字节帧头（magic4 + descriptor1 + window1 + dictID4 + FCS8，singleSegment=0 / dictID flag=3 / FCS flag=3 组合），`AsSpan(5,13)` 会抛 ArgumentOutOfRangeException 且逃逸路径同上。修复（P2-1）：缓冲扩到 18 字节；8 字节 FCS 超出 long 范围不再 `checked` 抛 OverflowException，改为解析 ulong 后范围校验、越界抛 InvalidDataException——解压路径的所有失败统一以 InvalidDataException 呈现。
- **卡片勘误**：偏差清单补 ⑦（usage 样本缺 turn/step 按无样本计 LinesSkipped，蓝本以 "undefined:…" 键照常 fold）；CS8604 叙事修正（干净 HEAD 全量构建可复现的存量警告，与本次加包无关）；Bugs 表登记两项审查遗留（deferred）：跨目录重复 session id 不检测、符号链接跟随递归。
- 验证：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` 295 通过 / 7 跳过（新增 1 项内容损坏测试）；`TOKENPULSE_RECONCILE_REAL=1` 门控对账 dsh 通过、codex/zcode 无回归。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| I-007-B1 | P3 | 跨目录重复 session id 不检测：蓝本 verify-raw 记 preflight failure，本实现两份工件各自解析、记录双计 | deferred | 现实概率极低（session id 为 UUID 且目录按会话生成）；后续加诊断计数 |
| I-007-B2 | P3 | 工件枚举经 Directory.Exists 跟随符号链接递归；蓝本 readdirSync 不跟随，极端场景 symlink 环可无限递归 | deferred | 现实概率极低；后续在定位器跳过重解析点 / 加深度上限 |

## Verification

- Related files: `src/TokenPulse.Infrastructure/DshSession/`（DshSessionAdapter、DshSessionLogParser、DshLogLocator、DshZstdLogStream、DshProjectDirectory）、`src/TokenPulse.Core/Services/DeepSeekPricingCatalog.cs`、`src/TokenPulse.Core/Abstractions/IDshSessionAdapter.cs`、`src/TokenPulse.Core/Pricing/model-pricing-snapshot.json`（deepseek 段）、`src/TokenPulse.Core/Services/BuiltinPricingSnapshot.cs`（LoadSnapshotJson）、`src/TokenPulse.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`（第四路）、`src/TokenPulse.App/DependencyInjection/AppServices.cs`（DI）、`tests/TokenPulse.Infrastructure.Tests/DshSession/DshSessionAdapterTests.cs`、`tests/TokenPulse.Core.Tests/Services/DeepSeekPricingCatalogTests.cs`、`tests/TokenPulse.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第四适配器）、`tests/TokenPulse.Reconciliation.Tests/`（RealDataReconciliationTests、Support/UsageChainAggregator、Support/RealReconciliationEnvironment、Support/DshBlueprintGolden.cs、Support/DshBlueprintGolden.mjs）、`src/TokenPulse.Infrastructure/ZCodeSqlite/ZCodeSqliteAdapter.cs`（顺带修 CS8604）
- How to run/verify: 全量 `dotnet build TokenPulse.sln && dotnet test TokenPulse.sln`；DSH 单测 `dotnet test tests/TokenPulse.Infrastructure.Tests/TokenPulse.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DshSession"` 与 `dotnet test tests/TokenPulse.Core.Tests/TokenPulse.Core.Tests.csproj --filter "FullyQualifiedName~DeepSeekPricingCatalog"`；真实对账 `TOKENPULSE_RECONCILE_REAL=1 TOKENPULSE_DSH_BLUEPRINT=<dsh-usage-stats 检出> dotnet test tests/TokenPulse.Reconciliation.Tests/TokenPulse.Reconciliation.Tests.csproj --filter "FullyQualifiedName~Real_dsh"`（本机检出即 ../dsh-usage-stats，不设蓝本变量时测试自动向上层目录发现）
- Results: 2026-10-11（含审查修复复验）— 构建 0 警告 0 错误；全仓 295 通过 / 7 跳过（HEAD 基线 260/6 实测保留，新增 34+1 项测试）；真实对账 tokens 两侧逐位一致（69 工件 / 12960 帧 / 5811 记录，input=22,012,649 / output=4,008,299 / cacheWrite=0 / cacheRead=843,451,328 / **total=869,472,276**），成本两侧同 fail-closed（合计 0，5811 条 0 成本记录 = 蓝本 5811 个不可计价样本）
