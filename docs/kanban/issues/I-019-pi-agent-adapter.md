---
id: I-019
title: pi-agent 适配器
type: feature
priority: P3
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 pi-agent 会话数据（PI_AGENT_DIR 或 ~/.pi/agent/sessions，及 ccusage.json 的 pi.stores[] 附加存储），产出 agent=pi 的统一 usage 记录。

## Background

蓝本：../ccusage/rust/adapters/pi/。注意附加 stores 的发现机制（ccusage.json 配置）。本机暂无数据。

## Acceptance Criteria

- [x] 目录发现（含 pi.stores[] 附加根）与解析
  - ✅ 2026-10-11：`PiLogLocator` 逐条移植 paths.rs——PI_AGENT_DIR **trim 后非空才生效**（与
    Kimi/Amp/Codebuff 的「已设置即生效」相反：pi 蓝本空串**回退默认目录**）；逗号分隔/去空白/仅存
    存在目录/按序去重；<b>不做 ~ 展开</b>（蓝本注释明确保留既有语义；configuredDataDir 参数为仓库
    共有测试钉定增强，同规则）；默认 ~/.pi/agent/sessions（存在才保留）。命名存储清单（蓝本
    named_store_paths）<b>做 ~ 展开</b>（恰为 "~" 或 "~/前缀"）。pi.stores[] 经 ccusage.json 发现
    （CLAUDE_CONFIG_DIR 目录 → ~/.config/claude → ~/.claude 的 ccusage.json，首个可解析 JSON 对象
    生效），条目校验 name 模式 ^[a-z][a-z0-9_-]{0,31}$ / 保留名（all + 18 内置 agent）/ path 非空 /
    name 去重
  - ✅ `PiSessionParser` 逐条移植 parser.rs（三遍读取）：首行会话头（type=="session"、parentSession、
    畸形标记、严格 RFC3339）；usage 行（预过滤须**同时**含 "usage" 与 "message" 片段；type 缺省放行；
    message/usage 须对象——标量/数组为 serde 结构性失败整行拒绝；role=="assistant"；input/output/
    cacheRead/cacheWrite/totalTokens lenient_u64；total 兜底；cost lenient 对象非对象不拒行）；
    链接行（全行解析 type/id/parentId，type=="session" 排除；全无链接 → Linear；部分链接/重复 id/
    悬挂引用/环 → Invalid；叶子回溯 parentId 得活动分支序）
- [x] token 映射对齐蓝本；fixtures + 单元测试
  - ✅ 重放抑制（`PiReplayPlan` 逐条移植 loader.rs）：子→父血缘（规范化路径直查 + 相对路径按子目录
    拼接），lineage 无效（缺头/缺时间戳/畸形 parentSession/父不在同批/自引用/环）fail-open；签名 =
    时间戳时刻 + 裸模型 + total 兜底后四桶 + 有效 total（四桶和+extra）+ 有效成本（存储成本缺省按
    内置快照现算）；仅抑制**前缀**。subagent-artifacts 路径段文件剔除（run-* 保留）；跨文件复合
    entry id（store:project:session:时间戳文本:模型:五计数:有效成本）first-wins 去重；时间戳稳定排序；
    27 项单测全绿（蓝本 loader 12 项内联测试同名场景全移植）

## Subtasks

- [x] `PiLogLocator`（paths.rs + ccusage-config 移植）：ResolveSessionDirectories（configuredDataDir →
  PI_AGENT_DIR（trim 非空才生效、不 ~ 展开）→ ~/.pi/agent/sessions）；ResolveNamedStoreDirectories
  （~ 展开同款清单规则）；LoadNamedStores（ccusage.json 发现 + pi.stores[] 校验解析）；共享
  `JsonSource/SourceFileCollector`、`Jsonl/JsonlHomePath`
- [x] `PiSessionParser`（parser.rs 移植）：PiSessionHeader / PiUsageSignature / PiReplayPath
  （Linear/Active/Invalid）/ PiEntry（统一记录 + 复合 id）；identityCostResolver 注入（缺省内置
  PricingEngine 快照，未定价模型记 0——与蓝本空 PricingMap 同塌缩语义）；诊断显式计数
- [x] `PiReplayPlan`（loader.rs 移植）：filesByPath（规范化、首个下标）→ invalidLineage →
  parentByChild（自引用/不可解析 → invalid）→ HasValidLineage（环/链上无效节点 fail-open）→
  MatchingReplayPrefix（父序列取 fork 时刻前、逐签名比对首异即止；任一侧 Invalid 不抑制）
- [x] `PiAgentAdapter`（loader.rs + adapter-all 装配移植）：默认作用域（store="pi"）+ 命名存储作用域
  （<b>独立 agent</b>：Agent = store 名、模型 "[store] " 前缀、项目名取相对存储根首段——蓝本
  adapter-all 同语义）；每作用域独立重放计划与去重集；命名存储路径与已占用路径重叠跳过（蓝本报
  配置错误终止，见偏差⑤）
- [x] 单元测试 ×27（`PiAgentAdapterTests`：蓝本 loader 内联测试 12 场景同名移植（重放前缀抑制/
  弃支保留/多断连根/fail-open 四态/自环/嵌套 fork/签名全字段比较/成本差保留/±0.0 等价/缺失与 0
  等价（未定价模型等价复现）/total 低报仍抑制/命名存储跨路径父解析）+ subagent-artifacts 双布局与
  判定 + parser 场景（total 兜底/cost 非对象/负成本重算 0 保留/"[pi] " 前缀定价/结构性拒绝计数/
  会话 id 与项目名推导）+ 目录发现（env 生效/空串回退默认/默认缺失无数据/命名存储 ~ 展开与去重/
  配置非法整组丢弃降级）+ 命名存储端到端（store 独立 agent、模型前缀、项目名、多路径父解析））
- [x] 对账：合成 fixture（sessions/project-a|b 六会话文件 + subagent-artifacts 诱饵）入仓 + CLI
  golden 录制（fixtures/golden/pi-fixture-daily-v20.0.29.json）+ fixture 对账测试 ×2 +
  `TOKENPULSE_RECONCILE_REAL` 门控真实对账测试（HasPiData 数据存在性 Skip）
- [x] 管道接线：`IPiAgentAdapter`（Core.Abstractions）+ LocalIngestionDataSource 第十六路 concat +
  App DI 双注册（具体类 + 接口）
- [x] 定价快照：**无需新增条目**——模型带 "[pi] "/"[store] " 前缀，引擎候选剥离（`[pi] x` 形态）后
  命中既有条目（openai/gpt-5、anthropic/claude-sonnet-4 家族）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 解析与映射语义（对照蓝本 parser.rs / loader.rs / paths.rs 内联测试）

Steps:
1. 测试内临时目录合成 sessions/**&#47;*.jsonl（蓝本 loader 内联测试 12 场景同数据：父子重放、树形
   活动分支、fail-open、签名全字段、命名存储跨路径；subagent-artifacts 双布局；total 兜底；cost 非
   对象；负成本；结构性拒绝；env 语义），运行 `PiAgentAdapter.ScanAsync` 断言记录、诊断与目录发现。

Expected:
- 仅预过滤（"usage"+"message" 双片段）通过的行进解析；message/usage 标量为结构性失败计 Malformed；
  缺/坏时间戳计 Invalid（仓库口径）；lineage 无效 fail-open（不抑制）；签名含全部有效字段（任一变异
  阻断抑制）；复合 entry id first-wins；PI_AGENT_DIR 空串回退默认目录（pi 特例）；命名存储独立
  agent 出账、模型 "[store] " 前缀、非法配置整组丢弃（偏差⑥）。

Result: 通过（Infrastructure 27/27，场景清单见 Subtasks）。

### TC-002: 合成 fixture 对账（CLI golden 录制，逐位一致）

Steps:
1. 仓库内 fixture `tests/TokenPulse.Reconciliation.Tests/fixtures/pi/`（sessions/project-a|b 六会话文件：
   父子重放（相对 parentSession）、subagent-artifacts 转录与 run-* 子会话、内建 cost 直采与负成本
   重算、无成本快照现算（含 cacheWrite）、坏行诱饵）。
2. 录制 golden：`PI_AGENT_DIR=$PWD/$FX/pi/sessions npx --yes ccusage@20.0.29 pi daily --json --offline
   --timezone UTC > $FX/golden/pi-fixture-daily-v20.0.29.json`（命令入仓 golden/README.md）。
3. 自研链路：`UsageChainAggregator.AggregatePiAsync(PiRoot)`（适配器 → PricingEngine 内置快照 →
   UTC 日聚合），按 日期×模型 与 每日/全期总计 两粒度比较。

Expected:
- tokens 五分量在全部粒度精确一致；cost 逐模型一致（比较器容差内，实测逐位同）；模型名带 "[pi] "
  前缀两侧一致；诊断 status=Ok、files=6（subagent-artifacts 不发现）、malformed=0、invalid=0、
  linesSkipped=5、records=8。

Result: 通过——两侧全量 **input=8,360 / output=3,475 / cacheCreation=241 / cacheRead=2,506 /
total=14,582 tokens，totalCost=$0.11825** 逐位一致（01-10 [pi] claude-sonnet-4-20250514
1000/200/40/300 cost 0.00624；01-13 [pi] gpt-5 3000/2400/0/1000 cost 0.07125（0.05 直采 +
负成本重算 0.02125）；01-14 含 cacheWrite 200 现算 cost 0.01911 两侧同价）；诊断 FilesDiscovered=6、
records=8、UnpricedModels=[]。

### TC-003: 本机真实数据对账（TOKENPULSE_RECONCILE_REAL=1 门控）

Steps:
1. `TOKENPULSE_RECONCILE_REAL=1 dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Real_pi"`。
2. 数据存在性门控：定位器（PI_AGENT_DIR / ~/.pi/agent/sessions）下发现 .jsonl 才运行（与 CLI 子进程
   同一套探测）；无数据 Skip 而非 Fail（TC-002 口径）。
3. 运行时两侧对账：CLI `npx --yes ccusage@20.0.29 pi daily --json --offline --timezone UTC`（golden）
   vs `AggregatePiAsync()`，按 日期×模型 / 每日/全期总计 两粒度断言无差异。

Expected:
- tokens 五分量在全部粒度精确一致（extra 并入 output 的显示差异 fail-loud，见偏差②）；本机
  ccusage.json 含 pi.stores[] 时的命名存储行统一 daily 口径 fail-loud（见偏差④）。

Result: 本机（2026-10-11）**无 pi-agent 数据 → 测试按门控 Skip**（与 codebuff/kimi 门控同态）。门控
链路本身已在本机验证：以 `PI_AGENT_DIR=<TC-002 fixture>` 开启门控运行，真实调用 CLI 生成 golden 与
自研全链路对账**通过**（`Real_pi_data_reconciles_with_cli` Passed，1/1）。有真实数据的机器上运行
该门控即得真实数字。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/pi/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 pi/ 的 loader.rs / parser.rs / paths.rs + crates/ccusage-config 的 pi.stores[]
解析 + crates/ccusage-adapter-all 的命名存储装配逐条对齐；蓝本许可 MIT License，Copyright (c) 2025
ryoppippi，声明保留在 PiLogLocator / PiSessionParser / PiReplayPlan / PiAgentAdapter 文件头；fixtures
全部合成，无真实数据入仓）：

- **三遍读取解析**（parser.rs 结构保真）：首行会话头（serde from_slice 失败/非 "session" → None）；
  usage 行预过滤 LinePrefilter::all(["\"usage\"", "\"message\""])（双片段齐备才解析——不含标记的坏行
  在蓝本同样静默跳过，桌面计 LinesSkipped）；链接行第二遍全行解析（无预过滤、排除 "session" 行）。
  message/usage 为严格 Option&lt;结构体&gt;（标量/数组 → serde 结构性失败整行拒绝，仓库口径计
  Malformed）；usage.cost 为 lenient 对象（**非对象不拒行**——蓝本 keeps_record_when_cost_is_not_an_object
  语义）。
- **重放抑制**（本卡核心，loader.rs PiReplayPlan + parser.rs PiReplayUsagePath 逐条）：树形会话
  （带 id/parentId 链接）的活动分支 = 自末位物理条目（叶子）沿 parentId 回溯；弃支与断连根不入候选；
  线性会话（v1，全无链接）用物理序。子会话（parentSession 指向同批发现文件，绝对路径直查、相对路径
  按子文件所在目录拼接）按「父候选序列（截断至子 fork 时间戳）逐签名比对」抑制**前缀**；签名 =
  时间戳时刻 + 裸模型 + total 兜底后四桶 + 有效 total（四桶和 + extra）+ 有效成本。血缘无效
  （缺头/缺时间戳/parentSession 畸形/父缺失/自引用/环）fail-open 不抑制。totalTokens 低报（≤ 四桶和）
  不影响签名（effective total 恒四桶和）——蓝本 dedupes_replay_when_raw_total_underreports 语义。
- **有效成本（identity cost）**：签名与复合 id 的成本分量 = 存储成本（usage.cost.total 有限且 ≥0，
  CLI auto 口径）缺省时按内置快照引擎现算（未定价 → 0）——对应蓝本 billed_cost_bits 的
  `cost.to_bits()`（auto/display）语义；±0.0 经 decimal 归一等价（蓝本 signed-zero 测试同语义）。
  蓝本 Calculate 模式（成本不入签名）桌面无对应模式（偏差③）。
- **成本落点**：统一记录 CostUsd = usage.cost.total（有限且 ≥0；负值/NaN/Inf 置 null 交引擎——蓝本
  auto 的 source_cost_for_mode 过滤同式；Display 模式保留负值/NaN 是 CLI 显示口径，桌面不复现）；
  引擎按模型名（"[pi] gpt-5"）匹配——PricingModelMatcher 候选剥离 "[pi] " 形态前缀命中裸模型
  （fixture 实证 gpt-5 / claude-sonnet-4-20250514 两侧同价，含 cacheWrite）。
- **命名存储**（pi.stores[]）：ccusage.json 发现（CLAUDE_CONFIG_DIR 逗号分隔目录 → ~/.config/claude →
  ~/.claude；首个可解析 JSON 对象生效）→ name 模式 ^[a-z][a-z0-9_-]{0,31}$ + 保留名（all + 18 内置
  agent）+ path 非空 + name 去重 → 命名存储作为<b>独立 agent</b> 出账（Agent = store 名、模型
  "[store] " 前缀、项目名取相对存储根首段、独立重放计划与去重集、跨该存储多路径解析父文件——蓝本
  adapter-all loader 同语义）；路径与默认存储/已占用路径重叠时跳过该路径。
- **诊断口径**（仓库 I-004 显式化）：LinesSkipped = 预过滤未过的行（含 session 头行与坏行）+ 打不开
  的文件；MalformedLines = 预过滤通过但 JSON/形状失败的行（顶层非对象、message/usage 标量）；
  InvalidRecords = 缺失/不可解析时间戳的 usage 行（仓库口径，Amp/Copilot 同款）；DuplicateRecords =
  复合 id 折叠数；状态 Ok/Partial/NoData 同 I-006。
- **管道接线**：`IPiAgentAdapter` 单例 + LocalIngestionDataSource 第十六路 concat；适配器实例无扫描
  状态（线程安全），并发由 LocalIngestionDataSource 信号量串行化。
- **定价快照**：无需新增（"[pi] "/"[store] " 前缀由引擎候选剥离命中既有条目）。

与蓝本的口径偏差清单（全部显式列出）：

- ① **identity cost 现算用内置快照**：蓝本签名成本的现算走 CLI 全量定价表（litellm + models.dev +
  别名 + deepseek 时间表），桌面用 PricingEngine 内置快照——定价差异会使「无存储成本的重复行」在
  两侧行为不同（蓝本认为相等/不等时桌面可能相反）；fixture 用同价模型规避，真实数据该场景
  fail-loud。
- ② **负成本/非有限成本重算口径**：桌面 CostUsd = 存储成本（有限且 ≥0）否则 null（CLI auto 口径）；
  CLI Display 模式保留负值/NaN/Inf（桌面无 Display 模式）；auto + 用户定价覆盖时 CLI 强制重算
  （source_cost 置 None），桌面覆盖命中前不动存储成本（LogCost 短路）——覆盖与存储成本并存的行
  成本 fail-loud（I-024 共性欠账同款）。
- ③ **无 Calculate/Display 模式开关**：蓝本签名成本分量随模式切换（Calculate 不入签名、Display 用
  原始存储值），桌面恒 auto 语义；CLI --mode 显示差异不复现（I-008 偏差⑦同款架构）。
- ④ **命名存储不在 fixture 对账面**：CLI 命名存储只在统一 daily 命令出账（会扫本机全部内置 agent
  默认目录，golden 不可复现），fixture 对账仅覆盖默认作用域；命名存储语义（store 独立 agent、模型
  前缀、项目名、跨路径父解析、独立去重）由单测覆盖。
- ⑤ **命名存储路径重叠 / 配置非法降级**：蓝本报配置错误终止整条命令（resolve_named_pi_store_paths
  Err / parse_named_pi_stores Err）；桌面无错误通道，降级为跳过重叠路径 / 整组丢弃非法 pi.stores[]
  （默认存储照常）——配置错误对桌面用户静默（诊断不可见，登记为观察项）。
- ⑥ **存储名 trim 差异**：蓝本 name 校验用原始字符串（" omp" 拒绝），桌面经 non_empty_string 取值
  trim 后校验（" omp" → "omp" 通过）——纯空白/带空白的存储名角落两侧不同。
- ⑦ **ccusage.json 发现去 CWD 变体**：蓝本先扫 $CWD/.ccusage/ccusage.json（CLI 进程 cwd 语义），
  桌面从应用安装目录扫 cwd 无意义，仅读 CLAUDE_CONFIG_DIR / ~/.config/claude / ~/.claude；
  --config 参数不复现。
- ⑧ **路径规范化差异**：重叠检测键蓝本用 canonicalize（解析符号链接），桌面 Path.GetFullPath（不解
  符号链接）——符号链接存储路径的重叠判定两侧不同的理论角落；normalize_path 的组件级折叠由
  GetFullPath 同语义承担。
- ⑨ **行级拒绝/空产出显式计数**（蓝本静默）：预过滤未过计 LinesSkipped、形状失败计 Malformed、
  时间戳缺失/不可解析计 Invalid——诊断不改变记录内容（I-004 显式化口径）。
- ⑩ **extra（total 缺口）并入 OutputTokens**：CLI daily 的 outputTokens 不含 extra（单列）、
  totalTokens 含之；且蓝本 pi 计价 = calculate_cost_from_pricing(**四桶**)（parser.rs 以 total 兜底后的
  四桶计价、extra 不参与，ccusage-core cost.rs 同式），桌面把 extra 并入 OutputTokens 交引擎 →
  extra&gt;0 行桌面成本 = CLI + extra×output 价——**output 与成本两个分量都 fail-loud**（与 Codebuff
  蓝本的 output+extra 计价式不同，两卡口径不可互推）；fixture 规避（total 恰走 output 补齐分支或
  缺失，extra 恒 0）。
- ⑪ **u64 → long 边界**：lenient_u64 读取层把 u64 溢出钳到 long.MaxValue（saturating；I-017 偏差⑥
  同款）；u64 wrapping 算术改 saturating（现实值远小于边界，结果一致）。
- ⑫ **configuredDataDir 为仓库增强**（非蓝本）：测试钉定入口（env 同规则：trim 非空才生效）。

验证：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` **589 通过 / 19 跳过**
（I-018 收口基线 560/18 + 新增 27 项 Infrastructure 单测 + 2 项 fixture 对账测试 + 1 项门控真实对账
测试（本机 Skip））；门控以 PI_AGENT_DIR 钉 fixture 复跑通过（1/1 Passed）。对账数字见 TC-002 /
TC-003。

### 2026-10-12（独立审查修复）

独立审查结论 **approve-with-nits**（三卡均可收口、代码无缺陷；本卡修复 P2-2 / P2-1 / P3-typo 三项）：

- **[P2-2] 偏差⑩"成本无损"断言更正**：蓝本 pi 计价 = calculate_cost_from_pricing(**四桶**)
  （parser.rs 以 total 兜底后的四桶入 PiCostInput、extra 不参与；ccusage-core cost.rs 同式），桌面把
  extra 并入 OutputTokens 交引擎 → extra&gt;0 行桌面成本 = CLI + extra×output 价——**output 与成本
  两个分量都 fail-loud**（原文本误写"蓝本计价式与桌面并桶后计价同值（成本无损）"；Codebuff 蓝本
  是 output+extra 计价式、两卡口径不可互推）。Real_pi 门控测试注释同步更正。fixture 规避策略不变
  （total 恰走 output 补齐分支或缺失，extra 恒 0），无测试变更。
- **[P2-1] I-024 卡补登记 PI-3**：I-024 卡 Background 增补"三源欠账"段（pi 签名/复合 id 的 identity
  cost 现算需 CLI 全量定价表口径——litellm + models.dev + 别名 + deepseek 时间表）、Dependencies
  追加 I-019，闭环偏差①的"I-024 已登记"声称。
- **[P3] TC-002 tokens 总数更正**：total **14,581 → 14,582**（分量和 8360+3475+241+2506；golden
  totals.totalTokens=14582）。
- 复核：`dotnet build TokenPulse.sln` 0 警告 0 错误；`dotnet test TokenPulse.sln` **623 通过 / 20 跳过**
  （数字随 I-022 收口增长，本卡相关项不回归——pi fixture 对账 2/2 与 CLI golden 逐位一致；门控以
  PI_AGENT_DIR 钉 fixture 复跑通过 1/1 Passed）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| PI-1 | P3 | 命名存储路径重叠 / pi.stores[] 配置非法时桌面静默降级（跳过/丢弃），蓝本报配置错误终止命令——用户配置错误对桌面不可见 | deferred | 无错误通道（IUsageDataSource 契约）；随诊断面增强（如扫描诊断 Status/计数扩展）一并收口 |
| PI-2 | P3 | 存储名经 non_empty_string trim 后校验，蓝本用原始字符串——" omp" 形态两侧判定不同 | deferred | 理论角落（ccusage.json 手写带空白存储名）；随真实配置对账回归再对齐 |
| PI-3 | P3 | 重放签名的现算成本用内置快照，蓝本用 CLI 全量定价表（含别名/deepseek 时间表）——无存储成本的重复行两侧抑制判定可能不同 | deferred | 随 I-024 定价快照版本刷新与候选计价接线一并评估 |

## Verification

- Related files: `src/TokenPulse.Infrastructure/PiAgent/PiLogLocator.cs`、
  `src/TokenPulse.Infrastructure/PiAgent/PiSessionParser.cs`、
  `src/TokenPulse.Infrastructure/PiAgent/PiReplayPlan.cs`、
  `src/TokenPulse.Infrastructure/PiAgent/PiAgentAdapter.cs`、
  `src/TokenPulse.Core/Abstractions/IPiAgentAdapter.cs`、
  `src/TokenPulse.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、
  `src/TokenPulse.App/DependencyInjection/AppServices.cs`、
  `tests/TokenPulse.Infrastructure.Tests/PiAgent/PiAgentAdapterTests.cs`、
  `tests/TokenPulse.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/PiFixtureReconciliationTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/fixtures/pi/`、
  `tests/TokenPulse.Reconciliation.Tests/fixtures/golden/pi-fixture-daily-v20.0.29.json`、
  `tests/TokenPulse.Reconciliation.Tests/RealDataReconciliationTests.cs`、
  `tests/TokenPulse.Reconciliation.Tests/Support/{FixturePaths,UsageChainAggregator,RealReconciliationEnvironment}.cs`、
  `tests/TokenPulse.Reconciliation.Tests/CCUsageCli/CcusageGoldenFiles.cs`
- How to run/verify: `dotnet test TokenPulse.sln`（全绿）；fixture 对账
  `dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~PiFixture"`；门控复跑
  `PI_AGENT_DIR=$PWD/tests/TokenPulse.Reconciliation.Tests/fixtures/pi/sessions TOKENPULSE_RECONCILE_REAL=1
  dotnet test tests/TokenPulse.Reconciliation.Tests --filter "FullyQualifiedName~Real_pi"`。
- Results: 全部通过（见 TC-001/002/003 Result 与 Development Log 验证段；收口数字 **589/19**，
  2026-10-12 独立审查修复复核后随批次收口数字 **623/20**，本卡项不回归）。
