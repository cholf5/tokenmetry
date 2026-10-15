---
id: I-006
title: ZCode SQLite 适配器
type: feature
priority: P1
size: M
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 ZCode 桌面版本地 SQLite 账本（~/.zcode/cli/db/db.sqlite 的 model_usage ⟕ session），产出 agent=ZCode 的统一 usage 记录并接入本地采集管道。

## Background

用户主力工具之一，此前完全没有接入。蓝本：../ccusage/rust/adapters/zcode/（loader/parser/paths）。关键口径：仅取 status='completed' 行；ZCode 的 input_tokens 含 cache 读写两个桶，需扣除后拆桶（各自截断到 input）；computed_total_tokens 超出分量和的余量并入 output 计价；started_at 为 epoch 毫秒。本机实测（2026-10-11）发布版 ccusage CLI v20.0.29 对本机数据全部报 missingPricing（provider_id 为内部 UUID，不被视为 Z.ai），故对账口径 = tokens 精确一致 + 成本两侧同为缺失/0。

## Acceptance Criteria

- [x] ZCodeLogLocator：ZCODE_HOME（逗号分隔多根，规范化去重、无效根跳过）→ ~/.zcode，库路径 <root>/cli/db/db.sqlite（配置根与 ZCODE_HOME 同语义支持逗号多根）
- [x] schema 检查：必需列缺失的库跳过；可选列（cache_creation/cache_read/computed_total/provider_id）按存在性动态拼查询，computed_total 缺失回退 input+output
- [x] token 归一化与蓝本 parser.rs 逐条对齐（含截断、全零行跳过、extra total 余量并入 output）
- [x] 计价门槛对齐蓝本：Z.ai 限定 provider（或无 provider 的 glm-* 行）走定价引擎且 cache_creation 按 input 价计（并入 input）；其余 provider 行 CostUsd=0（与 CLI 的 missingPricing/0 一致）
- [x] 只读打开（WAL 兼容，ZCode 运行中可读），按 usage id 去重、按时间排序
- [x] 接入 LocalIngestionDataSource 与 DI，fixture 单元测试 + 真实数据对账（Q-001 harness 扩展 zcode 源）

## Subtasks

- [x] ZCodeLogLocator（根解析 + 库文件定位 + 多根去重）
- [x] ZCodeSchemaInspector（PRAGMA table_info + 必需/可选列检查）
- [x] ZCodeRecordMapper（row_to_entry 移植：归一化 + zai 门槛 + 余量处理）
- [x] ZCodeSqliteAdapter（只读打开、动态查询、诊断计数、id 去重、时间排序）
- [x] DI 注册 + LocalIngestionDataSource 管道接入
- [x] fixture 单元测试 ×18（临时 SQLite 库，无真实数据入仓；含独立审查新增的 5 项对抗性用例）
- [x] Q-001 harness 扩展（AggregateZcodeAsync / LoadZcodeText / HasZcodeData）+ 真实数据对账通过
- [x] 独立第三方审计（subagent）+ 审查发现修复（见 Development Log）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: fixture 归一化与门槛（对照蓝本 parser.rs 内联测试）

Steps:
1. 临时 SQLite 库写入合成行（含双缓存桶、截断、余量、zai/自定义 provider、legacy schema、非 completed、无效身份、WAL 未提交事务等场景），运行适配器断言记录与诊断。

Expected:
- 双桶归一化 input=60/creation=15/read=25（zai 行 creation 并入 input=75、总量守恒）；截断与余量并入 output；仅 completed；无效/全零行计 InvalidRecords；自定义 provider CostUsd=0；WAL 下只读到已提交行。

Result: 通过（13/13 通过；独立审查后增补 5 项对抗性用例，共 18/18：归一化、zai 并桶、legacy glm 门槛、自定义 provider 0 成本、截断与余量、completed 过滤、无效身份、负值/文本 token 列钳 0、时间列 f64 兜底、NULL 身份静默跳过、跨库无效首现行去重、越界时间、legacy schema、坏 schema 跳过、多根去重、WAL 未提交事务、库缺失、排序）。

### TC-002: 本机真实数据对账（Q-001 harness，TOKENMETRY_RECONCILE_REAL=1 门控）

Steps:
1. `npx --yes ccusage@20.0.29 zcode daily --json --offline --timezone UTC` 取 CLI 口径。
2. `AggregateZcodeAsync()`（无 configuredHome，与 CLI 读同一套探测逻辑）全链路聚合。
3. ReconciliationComparator 按 日期×模型 与 每日/全期总计比较。

Expected:
- tokens 精确一致；成本两侧同为 0（本机数据全部为自定义 provider）。

Result: 通过——本机 22,315 条记录，自研 input=76,028,203 / output=18,539,131 / cacheCreation=0 / cacheRead=2,323,656,640 / total=2,418,223,974 / cost=0，与 CLI 逐位一致；per-model 36 组、每日 23 组 tokens 全等。

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/zcode/），尚未开始实现。

### 2026-10-11（实现完成，当卡收口）

移植与设计决策（蓝本 zcode/ 的 loader.rs / parser.rs / paths.rs 逐条对齐；fixtures 全部合成，无真实数据入仓）：

- **定位**（`ZCodeLogLocator`）：配置根优先（与 ZCODE_HOME 同语义，逗号分隔多根、~ 展开、GetFullPath 规范化、按序去重）→ ZCODE_HOME → ~/.zcode（USERPROFILE 优先，复用 JsonlHomePath）；库固定 <root>/cli/db/db.sqlite，根无效或库缺失即跳过、不回退默认目录（对齐蓝本"do not silently fall back"）；多根解析出同一库文件按序去重。
- **schema 检查**（`ZCodeSchemaInspector`，PRAGMA table_info）：必需列 model_usage[id, session_id, started_at, model_id, status, input_tokens, output_tokens] + session[id, directory]；可选列 cache_creation/cache_read/computed_total/provider_id 按存在性拼查询（缺失列以常量 0/NULL 占位保持列位固定），computed_total 缺失回退 `input + output`。蓝本的 session.version 仅用于诊断标签，本实现不读取（统一记录无对应字段）。
- **行映射**（`ZCodeRecordMapper`）：门槛（id/session_id/model_id 非空 + started_at>0）→ cache_read=min(read, input)、cache_creation=min(creation, input−read)、input=input−两桶（饱和）→ 全零行作废 → computed_total 余量并入 OutputTokens（蓝本计价同样把余量计入 output 价；统一记录无 extra_total_tokens 字段，并入使聚合总额与蓝本 display 总额一致——**口径偏差**：出现余量的行逐桶 output 显示含余量，实测本机数据 computed_total 与分量和恒相等，余量路径为 0）→ Z.ai 判定（显式 provider 枚举/前缀，或无 provider 的 glm-*）。（注：本条最初版本含"负值行作废（对应蓝本 i64→u64 转换失败整行跳过）"的表述，经独立审查证伪——蓝本对 token 列钳 0 保留行，见下方审查记录。）
- **计价门槛**：Z.ai 行 CostUsd=null 走定价引擎，且 cache_creation 并入 input（Z.ai 把新内容展示为 creation 桶但按标准 input 价计费，蓝本 cost_usage 同式，逐 token 一致）；其余 provider 行 CostUsd=0m（LogCost 短路），对齐本机 CLI 实测 missingPricing/cost 0。**已知偏差**：蓝本对自定义 provider 行仍尊重 raw 模型用户覆盖，本实现 0m 短路在覆盖规则之下——用户覆盖与自定义 provider 同时出现时以 0 为准（真实场景未出现，后续有需求再评估）。
- **定价快照扩充**（联动 I-003）：内置快照新增 `zai` 段 22 个 GLM 条目（取自蓝本 models-dev-pricing.json 的 zai/* 键；input/output 缺失的条目丢弃，cache_read/cache_write 缺失省略键让解析器走与蓝本 pricing.rs 相同的缺省乘数 write=×1.25 / read=×0.1）——Z.ai 限定 provider 的行离线即可计价，与本机无关（本机全为自定义 provider）但对 zai plan 用户保证与 CLI 内嵌价一致。
- **读取语义**：Microsoft.Data.Sqlite `Mode=ReadOnly`，不改 journal mode——WAL 兼容经双保险验证（单测：写连接持未提交事务时只读连接读到已提交行；实测：对账测试在本会话运行期间对活跃 WAL 库全量读取成功）。每次扫描全量重读（账本单文件持续变化，mtime 增量不适用；completed 行万级、对账实测 22k 行全链路 5s 内含 CLI 进程）。
- **诊断口径**：沿用 JsonlScanDiagnostics——FilesDiscovered=发现的库数、FilesRead=打开且 schema 支持并完成查询的库数、InvalidRecords=行级拒绝（NULL 身份/坏列类型/身份无效/负值/全零，对应蓝本静默跳过的行，此处显式计数）、DuplicateRecords=跨库 id 重复折叠；MalformedLines/LinesSkipped 恒 0（无行解析概念）。状态：有记录且无问题=Ok，有问题=Partial，无记录=NoData。
- **管道接线**：`IZCodeSqliteAdapter` 单例 + LocalIngestionDataSource 增加第三路扫描（claude/codex/zcode concat）；适配器无扫描状态（与 JSONL 适配器的增量状态不同，注释已说明）；其余成本补价 / facts 落库路径零改动。

验证：`dotnet build Tokenmetry.sln` 0 警告 0 错误；`dotnet test Tokenmetry.sln` 255 通过 / 6 跳过（基线 242/5 原样保留，新增 13 项 ZCode 单测 + 1 项门控真实对账，门控开时实跑通过）；真实数据对账数字见 TC-002。

### 2026-10-11（独立第三方审查与修复）

独立审计（subagent 扮演第三方审计员，不信任注释/卡片声明、以亲自运行为准）结论 **PASS with minor issues**：移植主体、快照 22 条价格逐位比对、真实对账（22,405 条逐位一致）均证实；声明审计 16 项中 15 项证实、1 项证伪。发现与处置：

- **修复 [P1] 负值/NULL token 列处理与蓝本相反，且卡片声明依据错误**：蓝本 `read_token_column` 为 i64→max(0)、f64 有限正数兜底、其余 0——行永保留，不存在"i64→u64 转换失败整行跳过"路径（原 Development Log 该句声明证伪，已订正）。C# 原实现把负值/NULL/坏类型 token 列整行跳过并计 InvalidRecords。修复：读取层按蓝本三分容错（token 列永不失败、身份列读取失败才静默跳行、provider/directory 类型异常按缺失保留行），f64→long 边界钳到 2^63 防 cast 回绕；映射层删除负值分支（读取层结构性保证非负）并全程改用饱和加减（蓝本 saturating_add 对齐）。新增对抗性测试：负值钳 0、文本 token 列 0 兜底、时间列 f64 兜底、NULL 身份静默跳过。
- **修复 [P2] id 去重早于行映射**：原实现先 `seenIds.Add` 后 `Map`，同 id 的无效首现行会吞掉另一库的有效行；蓝本先 row_to_entry 再去重。修复：去重移到映射成功之后。
- **修复 [P2] started_at 越界击穿扫描**：`DateTimeOffset.FromUnixTimeMilliseconds` 对 >9999 年的毫秒值抛 `ArgumentOutOfRangeException`，不在查询级 catch 内。修复：映射层按行级无效处理（MaxUnixMilliseconds 上限；蓝本 jiff 年域更宽，实际数据不可达的防御边界），新增回归测试。
- **修复 [P2] `Schema.HasSessionVersion` 死字段**：version 列不读取是已声明口径，字段无消费者——移除。
- **登记不修 [P2] 远程定价命中顺序无 zai 命名空间优先**：蓝本对 Z.ai 行优先尝试 `zai/<model>` 候选；C# 引擎按 provider rank 统一匹配。离线（内置快照 zai 段唯一）与 --offline 对账不受影响；若 models.dev 远程数据多 provider 发布同名 id 且该 provider rank 更高，Z.ai 行可能命中非 zai 段价格。真实影响需联网验证，暂记条件分歧。
- **登记不修 [P2] 定位器细节差异**：蓝本对根做 `canonicalize`（符号链接归一），C# GetFullPath 不解析 symlink——经链接指向同一物理库的两根不合并（id 去重兜底，仅重复读 + DuplicateRecords 虚高）；`~` 展开为蓝本没有的增强；默认 home USERPROFILE 优先与 Claude/Codex 定位器一致（蓝本 HOME 优先，macOS 实际只设 HOME，零影响）。
- 复核：`dotnet build` 0/0；全仓 `dotnet test` 260 通过 / 6 跳过（255 基线 + 5 项审查新增）；门控真实对账复跑通过（22,471 条，tokens 两侧仍逐位一致）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.Infrastructure/ZCodeSqlite/`（ZCodeSqliteAdapter、ZCodeLogLocator、ZCodeSchemaInspector、ZCodeRecordMapper、ZCodeUsageRow）、`src/Tokenmetry.Core/Abstractions/IZCodeSqliteAdapter.cs`、`src/Tokenmetry.Infrastructure/LocalIngestion/LocalIngestionDataSource.cs`、`src/Tokenmetry.App/DependencyInjection/AppServices.cs`、`src/Tokenmetry.Core/Pricing/model-pricing-snapshot.json`（zai 段）、`tests/Tokenmetry.Infrastructure.Tests/ZCodeSqlite/ZCodeSqliteAdapterTests.cs`、`tests/Tokenmetry.Infrastructure.Tests/LocalIngestion/LocalIngestionDataSourceTests.cs`（工厂补第三适配器）、`tests/Tokenmetry.Reconciliation.Tests/`（RealDataReconciliationTests、Support/UsageChainAggregator、Support/RealReconciliationEnvironment、CCUsageCli/CcusageGoldenFiles）
- How to run/verify: `dotnet test tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ZCodeSqlite"`；真实对账 `TOKENMETRY_RECONCILE_REAL=1 dotnet test tests/Tokenmetry.Reconciliation.Tests/Tokenmetry.Reconciliation.Tests.csproj --filter "FullyQualifiedName~Real_zcode"`
- Results: 2026-10-11 — 构建 0/0；单测 18/18；全仓 260 通过 / 6 跳过；真实对账 tokens 两侧逐位一致（22,315→22,471 条随账本活跃增长，total 2,418,223,974→2,431,819,367），cost 两侧同为 0；独立第三方审计 PASS with minor issues，P1 与可落地 P2 已修复（见 Development Log）
