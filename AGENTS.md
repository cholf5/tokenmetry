# AGENTS.md — Tokenmetry

本地优先、托盘常驻的 AI 编程 token/成本监控桌面应用（Avalonia）。产品定位 **Opinionated Usage Stats**：固定信息架构（总览/Agent/模型/项目/会话/时间/成本分析/数据与计价/设置），不做面板平台、插件、布局自定义——立场与理由见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`（改动产品方向前必读）。

## 常用命令

```bash
dotnet build Tokenmetry.sln            # 全量构建
dotnet test Tokenmetry.sln             # 全部测试（xUnit）
dotnet test tests/Tokenmetry.Core.Tests
dotnet test tests/Tokenmetry.Infrastructure.Tests
dotnet test tests/Tokenmetry.Reconciliation.Tests
dotnet test tests/Tokenmetry.App.Tests
dotnet test tests/Tokenmetry.App.Headless.Tests   # Avalonia Headless UI 测试
dotnet run --project src/Tokenmetry.App
```

.NET SDK 10（net10.0）。开发机可在 macOS / Windows / Linux 任意平台进行，发布目标是 Windows / macOS / Linux 三平台（发布流程见 `docs/release/`，注意该文档当前停留在 v0.1 时代、待重写）。

## 架构边界（三层，禁止反向依赖）

- **Tokenmetry.Core**：模型、适配器接口（`Abstractions/`，每个 agent 源一个 `I*Adapter`）、业务服务（查询/定价/异常检测）、内置价格快照（`Pricing/model-pricing-snapshot.json`）。无 UI 依赖。
- **Tokenmetry.Infrastructure**：每 agent 源一个目录的采集适配器（ClaudeCodeJsonl / CodexJsonl / ZCodeSqlite / DshSession / GeminiCli / GooseSqlite 等 ~20 个）、`Cache/SqliteUsageStore`（usage_facts + session_facts + `schema_migrations` 顺序迁移 runner——改表必须新增迁移；全局版本即 `schema_migrations` 账本，业务代码不做数据版本分支；迁移必须让旧语义的行消失：新维度进主键时在迁移内转换或丢弃，不进主键时回填语义诚实的默认值，细则见 `SqliteUsageStore` Migrations 列表旁注释块）、ModelsDev 定价客户端、CCUsageCli 集成。
- **Tokenmetry.App**：Avalonia UI（Semi.Avalonia 主题）、ViewModels（CommunityToolkit.Mvvm，`AvaloniaUseCompiledBindingsByDefault=true` → XAML 必须写 `x:DataType`）、DI 注册集中在 `DependencyInjection/AppServices.cs`、托盘、i18n。

新增 agent 数据源的固定套路：Core/Abstractions 加接口 → Infrastructure 建目录实现适配器 → AppServices.cs 注册 DI → Infrastructure.Tests 加 fixture 用例。

## 与 ccusage 的对账是信任锚

任何成本数字必须能与 `npx ccusage@20.0.29` 输出对账一致（tokens 精确、cost ≤0.5%）。改解析、计价、聚合口径前**必读** `docs/reconciliation/2026-10-11-reconciliation.md`（口径逐项对齐表：costUSD 优先级、UTC 分桶、`-fast` 剥价、消息去重、cumulative 守卫等）。Reconciliation.Tests 只解析提交进仓库的 golden JSON，不现场调用 CLI。

## i18n 纪律

全部界面文案唯一出处：`src/Tokenmetry.App/Localization/Translations.cs`。简体中文字典为源（key = `nameof(属性)`，漏 key 显示属性名自暴露），其余语种字典只放译文，字典体在 partial 分文件（`Translations.ZhHant.cs` / `.Ja.cs` / `.Ko.cs` / `.PtBr.cs` / `.Ru.cs` / `.Es.cs` / `.De.cs` / `.Fr.cs`；字典**字段声明**留在主文件、置于 `Instance` 之前——跨文件静态初始化顺序陷阱）。支持语种（D-024/D-025）：`zh` / `zh-hant` / `en` / `ja` / `ko` / `pt-br` / `ru` / `es` / `de` / `fr` + `system`（AppSettings.Language 统一小写存码；`Resolve` 时 zh 系按书写系统分简繁，pt 系统一落 pt-BR，es/de/fr 不分地区变体；未知值回落 system）。回退链：新语种缺项 → 英文 → 简体，zh-Hant 缺项 → 简体——链只是漏译安全网，**所有语种必须全量覆盖**（`TranslationsTests` 的 `*_CoversEveryChineseSourceKey` 与占位符不变式测试固化，新增 zh key 必须同步补全部语种译文）；语言名选项自名不翻译（各语种同值）。明确不做 hi/bn/ar/id。XAML 用 `{Binding 属性, Source={x:Static loc:Translations.Instance}}`。`Tokenmetry.App.Tests` 固定 zh-Hans 文化（`TestCulture.cs`），测试断言必须语言无关（不要断言具体中文/英文串）。

## 设计令牌（改 UI 前读 `docs/design/design-tokens.md`）

令牌在 `Styles/Tokens.axaml` + 共享样式 `Styles/SharedStyles.axaml`。规则：新增资源键只能用 `App*` 前缀且不得与 Semi.Avalonia 撞名（有 headless 测试固化）；视图内**禁止硬编码色值**，一律 `Classes="..."` + DynamicResource；随明暗翻转的进 ThemeDictionaries，图表系列色（AppSeries0–6）不翻转。

## 命名史陷阱（勿"顺手改"）

仓库经历过两次全仓更名：CCUsage → TokenPulse → Tokenmetry（见 944ca91、5c14525）。当前品牌 **Tokenmetry**，环境变量 `TOKENMETRY_*`，数据目录 `%LOCALAPPDATA%\Tokenmetry\tokenmetry.db`。但以下仍是有意保留的旧名：`CCUsageCli*` 集成类、npm `ccusage` CLI 引用、历史文档中的旧代号——不要重命名。

## 看板与提交约定

任务管理用 `docs/kanban/board.md`（Todo|Done 两列）+ `docs/kanban/issues/<ID>-<slug>.md` 卡片；移动卡片 = 同一次操作里改看板行 + 卡片内 status。提交信息为中文，格式 `type: 一句话` + 正文说明"改了什么、为什么、明确不做什么"（参照 git log 风格）。

## 隐私红线

SQLite 只存聚合事实与配置；不存 prompt、代码、会话内容。
