# AGENTS.md — Tokenmetry

本地优先、托盘常驻的 AI 编程 token/成本监控桌面应用（Avalonia）。产品定位 **Opinionated Usage Stats**：固定信息架构（总览/Agent/模型/项目/会话/时间/成本分析/数据与计价/设置），不做面板平台、插件、布局自定义——立场与理由见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`（改动产品方向前必读）。注意 `docs/plans/` 整个目录在 .gitignore 里：设计与计划文档是**本地文档**，不入库、新 clone 没有，改动也不会出现在 git status。

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

## 分享海报（SharePoster）子系统

组成：Core `Models/SharePoster*.cs`（请求/快照模型，快照构建 `SharePosterSnapshot` 也在这里）+ App 侧 `Views/SharePosterDialog`（独立窗口，代码建窗）、`Views/SharePosterView`（海报本体）、`ViewModels/SharePoster*`、`Services/SharePosterExporter`；设计与取舍见 `docs/plans/2026-11-02-share-poster-design.md`。改预览/导出前需要知道的硬事实：

- **预览是活视图，导出与预览缩放无关**：对话框窗口本身是渲染宿主（海报子树挂在里面才拿得到样式与图表初始化）；导出 = `SharePosterExporter.Render(PosterRootControl)` 对海报根按 2× 光栅化，要求节点已挂树、已布局。海报根画布宽固定（竖版 1080 / 横版 1800，常量在 `SharePosterView`）；预览层的任何缩放变换都套在视图**之外**，不会污染导出。
- **投影只能用 `DropShadowEffect`，不能用 `Border.BoxShadow`**：Avalonia #20693（RenderTargetBitmap 上 DPI≠96 时 BoxShadow 把 DPI 平方乘进其后首个变换，v0.4.0 日间导出真机中招）未修前不许换回，细则见 `SharePosterView.ApplyPanelShadows` 注释。
- **AvaPlot 输入已禁**（`TrendChart` 构造里 `UserInputProcessor.Disable()` + `HandleMouseWheelEvent = false`，后者是放行冒泡）：滚轮/双击会照常冒泡到宿主，页面/预览层可以放心挂手势。
- **对话框内数据范围行与主窗口共享 `FilterStateService`**（同一实例即双向同步，关窗保留改动；海报永远印「当前筛选下的这个视图」）：筛选变更 300ms 防抖后经 `MainWindowViewModel.LoadPosterBundleAsync` 重查一轮 `SharePosterDataBundle`，按代取消 + 过代丢弃，旧预览 stale-while-revalidate；海报选项（样式/版式/主题/雾化）只重排不重查。对话框 VM 关窗必须 `Detach()`（窗口 `Closed` 已接线），否则整棵 VM 沿筛选订阅钉死在进程里。
- 版式合法性唯一出处是能力表 `SharePosterOptions.SupportedLayouts`（样式锁版式：托盘/里程碑锁竖版，热力/节律锁横版，越界起值收口）；换版式 = `SharePosterView.ApplyLayout` 重排栅格，**运行期不要动 ColumnDefinitions**（Avalonia Grid 定义数量变化会踩测量缓存索引越界）。

## i18n 纪律

全部界面文案唯一出处：`src/Tokenmetry.App/Localization/Translations.cs`。简体中文字典为源（key = `nameof(属性)`，漏 key 显示属性名自暴露），其余语种字典只放译文，字典体在 partial 分文件（`Translations.ZhHant.cs` / `.Ja.cs` / `.Ko.cs` / `.PtBr.cs` / `.Ru.cs` / `.Es.cs` / `.De.cs` / `.Fr.cs`；字典**字段声明**留在主文件、置于 `Instance` 之前——跨文件静态初始化顺序陷阱）。支持语种（D-024/D-025）：`zh` / `zh-hant` / `en` / `ja` / `ko` / `pt-br` / `ru` / `es` / `de` / `fr` + `system`（AppSettings.Language 统一小写存码；`Resolve` 时 zh 系按书写系统分简繁，pt 系统一落 pt-BR，es/de/fr 不分地区变体；未知值回落 system）。回退链：新语种缺项 → 英文 → 简体，zh-Hant 缺项 → 简体——链只是漏译安全网，**所有语种必须全量覆盖**（`TranslationsTests` 的 `*_CoversEveryChineseSourceKey` 与占位符不变式测试固化，新增 zh key 必须同步补全部语种译文）；语言名选项自名不翻译（各语种同值）。明确不做 hi/bn/ar/id。XAML 用 `{Binding 属性, Source={x:Static loc:Translations.Instance}}`。`Tokenmetry.App.Tests` 与 `Tokenmetry.App.Headless.Tests` 各自固定 zh-Hans 文化（各自的 `TestCulture.cs`），测试断言必须语言无关（不要断言具体中文/英文串；要断言文案就引用 `Translations.Instance` 的属性，两边出处一致即语言无关）。

## 设计令牌（改 UI 前读 `docs/design/design-tokens.md`）

令牌在 `Styles/Tokens.axaml` + 共享样式 `Styles/SharedStyles.axaml`。规则：新增资源键只能用 `App*` 前缀且不得与 Semi.Avalonia 撞名（有 headless 测试固化）；视图内**禁止硬编码色值**，一律 `Classes="..."` + DynamicResource；随明暗翻转的进 ThemeDictionaries，图表系列色（AppSeries0–6）不翻转。

## Headless UI 测试（`tests/Tokenmetry.App.Headless.Tests`，改 UI 后补测试）

- 用例标 `[AvaloniaFact]`（Avalonia.Headless.XUnit）；布局收口仪式：`Show() → Dispatcher.UIThread.RunJobs() → AvaloniaHeadlessPlatform.ForceRenderTimerTick()`。
- 输入模拟走真实输入管线：`HeadlessWindowExtensions`（`MouseWheel` / `MouseDown` / `MouseUp` / `MouseClick` 等，点位相对窗口、带 `RawInputModifiers`，修饰键会正确映射到事件的 `KeyModifiers`）。没有 DoubleClick helper，双击 = 同点位两组 MouseDown/Up。
- 整个程序集禁并行（`AssemblySettings.cs`——Translations 是进程级单例，并行切语言会互踩）；语言无关断言纪律见 i18n 一节，两套 UI 测试套件同款。
- Avalonia 12 的两个布局坑（写几何断言会撞上）：`LayoutTransformControl` 内部按设备像素取整，居中差 ±1px 属正常，容差别卡死在 1px；ScrollViewer 的 ContentPresenter 会把比视口小的内容拉伸铺满，要居中得在 ScrollViewer 和内容之间垫一层 Grid。

## 命名史陷阱（勿"顺手改"）

仓库经历过两次全仓更名：CCUsage → TokenPulse → Tokenmetry（见 944ca91、5c14525）。当前品牌 **Tokenmetry**，环境变量 `TOKENMETRY_*`，数据目录 `%LOCALAPPDATA%\Tokenmetry\tokenmetry.db`。但以下仍是有意保留的旧名：`CCUsageCli*` 集成类、npm `ccusage` CLI 引用、历史文档中的旧代号——不要重命名。

## 看板与提交约定

任务管理用 `docs/kanban/board.md`（Todo|Done 两列）+ `docs/kanban/issues/<ID>-<slug>.md` 卡片；移动卡片 = 同一次操作里改看板行 + 卡片内 status。提交信息为中文，格式 `type: 一句话` + 正文说明"改了什么、为什么、明确不做什么"（参照 git log 风格）。

**开卡必须由用户明确指示**。看板卡是严肃的项目记录，不是随手记账：用户没有明说"开卡 / 建卡 / 立卡"时，一律直接干活，不要自作主张新建卡片或往看板加行。已有卡片的移动与收口仍按上面的约定执行。

## 隐私红线

SQLite 只存聚合事实与配置；不存 prompt、代码、会话内容。
