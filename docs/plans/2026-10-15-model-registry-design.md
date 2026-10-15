# 提案：模型注册表（ModelId 单一身份）驱动价格 / 厂商 / 图标 / 聚合

**日期**: 2026-10-15
**状态**: **已实施**（I-035/I-036/I-037/S-003/I-038，同日收口；一处方案变更见 §3.3）
**性质**: 数据模型 + 设置面变更提案；不动产品定位（固定信息架构不变，见 `2026-10-11-opinionated-usage-stats-pivot.md`）

---

## 1. 现状核实：三个机制各自用「模型名字符串」当键

| 机制 | 载体 | 生效位置 | 匹配语义 | 消费者 |
|---|---|---|---|---|
| 聚合归一 | `AppSettings.ModelAliases`（raw → 规范名） | **查询期**（`UsageQueryService.ResolveModel`，UsageQueryService.cs:48/147-155） | `Ordinal` 大小写敏感 | 模型维度聚合键 + 会话模型筛选 |
| 计价 | `ModelPricingOverride` → models.dev → 内置快照 | **采集期**（LocalIngestionDataSource.cs:222 → `pricedCosts` → usage_facts.cost_usd） | `OrdinalIgnoreCase` 候选精确 / 家族包含 | 落库的 `cost_usd` |
| 图标 | `ModelVisualMap`（App 层硬编码） | 渲染期 | `Contains` 子串 | 模型芯片 / 别名规则行 |

三条后果：

1. **别名改不动价格**——别名只被查询服务消费，定价引擎完全不看它。用户口径「只影响 token 消耗的统计，没法关联价格」的字面成因就在这里。
2. **设置页没有任何价目 UI**。`AppSettings.PricingOverrides` 目前只有 `JsonAppSettingsStoreTests` 在写，是死配置字段（模型聚合区 S-002 已有 UI，定价覆盖区没有）。缺价模型唯一的出口是成本分析页「待确认价格」KPI + tooltip（CostViewModel.cs:155/218）——**只读，不可操作**。
3. 同一件事三处实现：新增一个模型的正确价格/厂商/图标要改三个地方，且图标是 App 层子串表、无法配置。

## 2. 「变成 0」的真实路径（分三类，别混谈）

- **真 0（应当修）**：`ZCodeRecordMapper.cs:85` —— 非 Z.ai provider 的行显式 `CostUsd = 0m`（注释说明是对齐 CLI missingPricing 口径）。
- **真 0（口径自相矛盾）**：`PiSessionParser.cs:96` / `PiAgentAdapter.cs:37` —— `PriceRecord(record).CostUsd ?? 0m`：引擎判 Missing 时记**已知 $0**。注释说这是蓝本「空 PricingMap 塌缩」的移植，但 `DshSessionLogParser` 的注释把**同一做法**定性为 bug（"98.1% token 被记成已知 $0 且绕过 GetUnpricedModels"）并已修掉。同一处境、两个源、两种处理，需要一次裁决。
- **诚实未知（保持）**：其余缺价走 `null` → `AggregatedCost` 未知态，token 计入、成本不计入合计。这条纪律不动。

## 3. 提议

### 3.1 拆成三层（今天全挤在 `ModelPriceEntry` 里）

- **模型条目 `ModelEntry`**：`Id` + `Vendor` + `DisplayName` + `Logo` + 价格四桶 + 长上下文分档 + `SourceKey`（导入来源的原始键，如 models.dev 的 `provider/model-id`）+ `Origin`（builtin / remote / user）。
- **匹配规则 `MatchRule`**：pattern → ModelId。今天 `ModelPriceEntry.MatchPattern`（快照 82 条里只有 `openai/gpt-5.6-sol` 用了它），语义是**规则**不是身份，扩表时必须分开。
- **别名 `Alias`**：原始名 → ModelId。今天 `AppSettings.ModelAliases`，语义不变（精确匹配、宁可分开不猜）。

### 3.2 三元组：usage_facts 的读侧解释，而非写侧固化

推荐 **facts 保持原始观测（day × agent × raw model × provider × project），解释放到查询期**：

- 好处：改别名 / 改价格**立即生效**，不需要全量重扫（用户的核心诉求）；facts 层语义仍是"日志里观测到什么"。
- 代价：查询期多做一次 resolve —— 现在已经在做（`ResolveModel`），无新增开销。
- 明确否决「采集期把 ModelId 写进 facts」：任何映射改动都要重扫 ~20 个源，等于把上面那个好处丢掉。

### 3.3 计价与重算：**实施时推翻了原方案**（改价即重算，不做查询期计价）

原设想：把计价从采集期挪到查询期，migration 里按 cost 来源拆行。**实施前核实发现前提不成立**：

- `IncrementalJsonlScanner` 每轮扫描返回**全量累积记录**（`_recordsByFile` 全量回放，只重解析变更文件，IncrementalJsonlScanner.cs:100）；
- 补价侧表每轮都对所有 `CostUsd is null` 的记录重跑 `PriceRecord`（LocalIngestionDataSource.cs:217-224）。

因此**触发一轮刷新即可全量重算**——不需要查询期计价，也不需要拆 cost 来源（那本是本方案唯一的硬点与 schema 改动）。

**实际落地**：目录热更新 + 保存后触发一轮刷新（复用 `BackgroundRefreshService.TriggerRefreshAsync`，与定时刷新同一单飞槽）。`cost_usd` 语义、`LogCost` 优先口径、与 ccusage 的对账锚全部不动。

已知边界：Pi / DSH 两条源的成本在**解析期**落定（Pi 的 identity cost 走注入引擎、DSH 走 `DeepSeekPricingCatalog` 历史规则），未变更文件不重解析，故改价要到相应文件变化后才反映到这两源的历史行——既有增量语义，已登记在 I-037。

## 4. 对原方案的两处修正

**(a) Provider 不能是硬门槛。** provider 只有约 6 类源真的有：ZCode `provider_id`、Droid `providerLock`、Goose `ProviderName`、Hermes `ProviderRaw`、Kilo `provider`、OpenClaw `provider`；其余源只能用模型名前缀（`openrouter/x`、`ark/glm-...`——`PricingModelMatcher.GetCandidates` 现在就是把前缀剥出来当 provider 用）。而且 `UsageRecord` **没有 Provider 字段**、usage_facts 没有 provider 列：这一路信息在适配器里读完就丢。

所以：`provider TEXT NOT NULL DEFAULT ''`，空值语义 = **未检测**（聚合显示"(未指定)"，由**模型条目**的 Vendor 兜底显示）。强制用户补全才能聚合，会把"打开就知道花了多少钱"变成"打开先填表"，与 Opinionated 立场冲突。

**(b) Vendor 与 Provider 是两个概念，别用一个词。** 条目上的 provider = "这个模型属于谁"（厂商）；用量行上的 provider = "这次请求由谁服务"（glm-4.6 走 z.ai 还是 openrouter，同模型不同价）。三元组里的应该是**行级 Provider**；条目侧叫 **Vendor**。

## 5. 手动 CRUD 的裁决：要，但分层

- **导入层只读**：内置快照 / models.dev（24h TTL）的数据不允许原地改——会被下次刷新覆盖，且会污染 ccusage 对账锚。
- **用户层 = 覆盖层**：条目字段级 `已覆盖` 标记（价 / 显示名 / Vendor / Logo），刷新跳过已覆盖字段；UI 明示"已被你覆盖"。
- 用户可**新建条目**（自定 Id）与**把未匹配原始名指到条目**（别名表的自然归宿）。
- **对账护栏**：Q-001/Reconciliation.Tests 只走内置快照路径，用户覆盖不进对账输入。

## 6. ID 约定与导入对齐

- `Id`：小写字母 + 连字符，**稳定不可变**（改显示名不得改 Id，否则用户自己的关联全丢）。
- 导入条目额外存 `SourceKey`（models.dev 用 `provider/model-id`，**id 里带点号**，如 `google/gemini.3.pro`）。刷新按 SourceKey 对齐已有条目，绝不按 Id 猜。
- 用户改价后刷新：按 SourceKey 命中 → 跳过已覆盖字段，其余字段照常更新。

## 7. 设置面

- 设置页新增「模型目录」区（列表 + 条目编辑 + 未匹配名关联），**吸收**现有"模型聚合（别名映射）"区；别名成为模型的关联项而非独立表格。
- 成本分析页「待确认价格」补「关联」入口（现有唯一 UX 缺口：能看到缺价模型，但什么也做不了）。
- 全部文案进 `Translations.cs`；色值一律令牌（`docs/design/design-tokens.md`）。

## 8. 迁移与兼容

- `ModelAliases` → Alias 层：键值语义已在，settings.json 无需改形。
- `PricingOverrides` → `Origin=user` 的用户条目（今天 `Provider="user"` 的语义迁移）。
- **migration 2**：usage_facts 加 `provider` 列、PK 变更、cost 来源拆分。migration 1 禁止修改（顺序迁移纪律）。

## 9. 卡片拆分建议（5 张）

| 建议 ID | 内容 |
|---|---|
| ID | 内容 | 实施结果 |
|---|---|---|
| I-035 | 目录模型与解析口径（三层结构 + SourceKey + 覆盖层语义 + 单测） | 完成 |
| I-036 | provider 三元组落库（migration 2 + 6 源补 provider + facts/查询链路） | 完成（模型维度按 provider 拆分未做，见 I-036 遗留） |
| I-037 | **改为**：目录驱动的重算（保存即重算；不做查询期计价与 cost 拆来源，依据见 §3.3） | 完成 |
| S-003 | 设置页模型目录 CRUD（成本页为指路文案，不做跨页跳转） | 完成 |
| I-038 | 迁移与对账护栏（旧字段只读桥、对账边界、死代码清理） | 完成 |

卡片文件在 `docs/kanban/issues/`，看板已记 Done（2026-10-15）。

## 10. 明确不做

- 不做按方向（input/output）的成本拆价——D-013 已定案方案 b，本提案不重开。
- 不改对账容差与 UTC 分桶口径。
- 不强制用户补 provider 才能看到统计。
- 不改内置快照里的价格（I-025 锚点纪律）。
- facts 不存 prompt / 代码 / 会话内容（隐私红线不动）。

## 11. 已裁决（用户授权按建议执行）

1. **两处"真 0"暂不统一**：ZCode 非 Z.ai provider 行与 PiAgent 的 `?? 0m` 都是蓝本口径的移植（对齐发布版 CLI 的 missingPricing 与蓝本空 PricingMap 塌缩），改它们会动对账；本批不动，登记为待评估项（与 DSH 已修的同类问题不同源）。
2. **provider 未检测不催补**：只显示"未指定"，facts 里留空串（I-036）。
3. **允许用户新建条目**：`ModelDefinitionOverride` 的 ID 不在导入层时即自建条目（S-003 的表单两者共用）。

## 12. 明确遗留（不在本批）

- 模型维度按 provider 拆分成多行（需 `session_facts` 同步加 provider 列）。
- 同一模型多 provider 的差异价（现取 rank 最优的单份条目价）。
- 图标变体与厂商 id 的自动映射表（现由 `LogoVendor` 显式指定，未覆盖时走内置名字推断）。
- Pi / DSH 解析期定价的陈旧边界（改价后需文件变化才反映，见 §3.3）。
