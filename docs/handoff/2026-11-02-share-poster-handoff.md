# 交接摘要：分享海报（Share Poster）

**日期**：2026-11-02
**状态**：~~骨架可跑通（8 提交、543 用例全绿），还差 4 件未完成，不可直接交付~~
**续跑结果（同日）**：交接列出的 4 件事已全部完成，另跑了一轮独立评审并修掉其阻塞/重要项；
当前 2000 个用例全绿（Core 239 / Reconciliation 49 / App 377 / Infrastructure 781 / Headless 554）。
剩余缺口见文末「续跑结果」。
**分支**：`feature/share-poster`　**工作树**：`.worktrees/share-poster`　**基线**：`08d900c`

---

## 1. 这个功能是什么

把"我在用 AI 干活"变成一张能发出去的图。内容不是新视图，而是**现有视图的印刷版**：
单日筛选 → 状态栏信息窗基底；多日筛选 → 总览页基底。同令牌、同图表控件、同信息结构，只放大尺度。

任何人接手前先读这两份（都在 `docs/plans/`，**gitignore，未入库**）：

- `2026-11-02-share-poster-design.md` —— 设计定稿（意图/目标/反目标/版面规格/六条必要视觉升级）
- `2026-11-02-share-poster.md` —— 实施计划（P1–P7 阶段划分）

视觉稿：`artifacts/poster-preview.html`（主工作区，自包含单文件，六版面：Overview 竖/横 × 夜/日 + 单日托盘 夜/日）。
**这是视觉基准**，实现出来的海报要和它一致。

## 2. 已经做完的（8 提交，每条独立可回滚）

| 提交 | 内容 |
|---|---|
| `0ddebd9` | `DonutChart.AnimateSweep`（默认 true） |
| `879018b` | `TrendChart.ChartFontScale` / `DonutChart.LegendFontSize` |
| `77390d7` | `TrendChart.ShowPeakAnnotation`（默认 false） |
| `9d6638f` | `SharePosterRequest/Totals/Ranking/Data` + `SharePosterSnapshot.Build` |
| `0f87633` | `SharePosterViewModel` + `SharePosterView`（竖版） |
| `a93dec2` | `SharePosterExporter`（2× 渲染 / 剪贴板 / 存 PNG） |
| `46a0d53` | `SharePosterDialog`（独立窗口：选项 + 预览 + 复制/保存） |
| `73ce2bb` | 项目名雾化（Off / 局部 / 全部） |

验证命令（都跑过，当前全绿）：

```bash
cd .worktrees/share-poster
dotnet test tests/Tokenmetry.App.Headless.Tests     # 543/543
dotnet test tests/Tokenmetry.Core.Tests             # 238/238
dotnet build Tokenmetry.sln                          # 0 error
```

## 3. 未完成（按严重程度，建议按序做）

### 3.1 横版版面 —— 最要紧，当前是**错误状态**

`SharePosterLayout.Landscape` 在对话框里**可以选中**，但 `SharePosterView` 只有竖版 —— 现在选横版会得到一张竖版海报。

两条路，**任选其一，不要放着**：
- 实现横版（1800 宽、两行三列：趋势 2.1fr / Agent 1fr / 类型环形 1fr；模型 1fr / 项目 1fr / 成本 1.2fr）
- 或先让 `SharePosterDialogViewModel.CanChooseLandscape` 返回 `false`，把选项藏掉
  （注意：`SharePosterDialogTests.Multi_day_window_allows_landscape` 会因此红，需同步改）

### 3.2 Agent / 模型品牌图标

用户明确要求过。资源已存在：`Styles/AgentLogos.axaml`、`Styles/ModelLogos.axaml`（lobe-icons, MIT）。
总览页的做法见 `Views/OverviewView.axaml` 的 `c:ModelChip.Avatar` + `vm:DimensionRowViewModel.Avatar`，照抄那条链。

**陷阱**：`AppModelLogoGlm` 的几何落在 x 91–309 / y 107–293（其余标识在 24×24 空间），共用同一 Viewbox 会缩成角上一个小点。

### 3.3 入口

- 总览页页头标题右侧一个弱图标按钮（常态 `opacity .5`，hover 提亮）。
  样式注意：`icon-btn` 定义在 `Views/MainWindow.axaml` 的 `Window.Styles` 里，页面内直接用不到，需在视图内自带样式。
- 设置页新增「分享海报」一节（默认版式 / 默认主题 / 默认雾化档），落点参考 `Views/SettingsView.axaml` 的 `Border.panel` + `Grid ColumnDefinitions="132,*"` 三行骨架；写入参考 `ViewModels/SettingsViewModel.cs` 的 `OnXChanged` → `_settings` → `PersistSettings`。
- `AppSettings` 增三个字段。

**难点**：对话框需要 `TopLevel`/`ShowDialog`，而总览页 VM 拿不到 TopLevel。全仓唯一的真 `ShowDialog` 在 `Views/MainWindow.axaml.cs`（代码建窗）。建议由 MainWindow 提供打开对话框的入口，VM 只负责发请求。

### 3.4 i18n

对话框标题、两个按钮、三组选项、以及 `SharePosterExporter` 的文件名提示目前是**中文占位字面量**。
约 24 个键 × 10 语种。纪律见 `AGENTS.md`：中文为源字典 + 9 个 partial 文件全量补齐，`TranslationsTests` 会强制覆盖。

另外 `Views/SharePosterDialog.axaml` 里的 `Title="分享海报"` 与按钮 `Content` 也要进 `Translations`。

### 3.5 最终评审

全部任务完成后，按 executing-plans 的要求跑一次 fresh-context 整体评审（`requesting-code-review` 的 code-reviewer）。

## 4. 踩过的坑（都会再次踩到，务必先读）

1. **`ThemeVariantScope` 在 `Avalonia.Controls`**，不在自定义命名空间。XAML 里写 `c:ThemeVariantScope` 解析失败，编译期不报，只有加载视图才炸 —— 所以海报视图的测试必须真的渲染一帧。
2. **`DonutChart` 有扫入动画**：`Rebuild()` 用 `DispatcherTimer` 从 0 帧生长弧段。离屏渲染 timer 不跑，环形会是**空的**。海报必须 `AnimateSweep=false`。
3. **图例字号写死在模板里**：`DonutChart.axaml` 的 `FontSize="11"` 局部值会压过继承，光加属性不生效。已改为继承，不要再写回局部值。
4. **`Bitmap.Save(Stream, int?)` 在 Avalonia 12 已过时**，仓库开着 `TreatWarningsAsErrors`，过时=编译错误。用 `Save(stream, new PngBitmapEncoderOptions())`。
5. **渲染只能对已挂树、已布局的节点**：两个图表在 `OnAttachedToVisualTree` 里才建图；对悬空控件 `Render()` 得到空白 + 无样式。
6. **`UserControl.Bounds` 会被外部容器拉伸**，海报真实宽度在 `PosterRoot` 这个 Border 上（`SharePosterView.PosterRootControl`）。
7. **排行占比的分母取该维度自身合计**，不是全范围总 token —— 同一批用量会同时计入 Agent/模型/项目各一行，三个维度合计互不相等。
8. **写测试时小心恒真断言**：本轮曾用"不透明像素数 > 总数/4"判断导出位图非空白，而海报底色本来就不透明，该断言永远成立。现已改为抽样统计不同颜色数。

## 5. 关键实现约定（别推翻）

- 一切色值走 `DynamicResource`，零硬编码 hex（headless 测试有扫 hex 的断言）。
- 海报不持有页面 VM（导航即 `Dispose`），只吃 `SharePosterData` 不可变快照。
- 未知成本走 `AggregatedCost` 契约：null ≠ 0，未知标记透传。
- 日序列**不补缺口**，折线在无记录处断开。
- 图表系列色不随主题翻转（沿用既有约定）。
- 海报视图的尺度差异集中在 `poster-*` 样式类里，不散落到元素上。

## 6. 续跑方式（历史记录：已于同日执行完毕）

台账（含每条任务记录、8 条 Ruling、未完成清单）：
`.worktrees/share-poster/.superpowers/sdd/2026-11-02-share-poster/progress.md`

新会话第一步建议：先跑三条验证命令确认基线全绿，再读 `progress.md` 的「未完成清单」，然后从 3.1 开始。

`docs/plans/` 与 `.superpowers/` 都在 gitignore 里，这两个目录的内容只存在于本机工作区，**不要指望它们跟着分支走**。

---

## 7. 续跑结果（2026-11-02 同一分支）

新增 4 个提交（在 8 个骨架提交之后）：

| 提交 | 内容 |
|---|---|
| `337954c` | 横版版面（1800 宽两行三列；面板只声明一次，按版式移动） |
| `f902633` | 视觉对齐基准：峰值标注（点 + 虚线 + 标签，推翻 P1c 的黄底左上角版）、日间卡片投影、竖版第一行比例、模型品牌图标 |
| `4f1eed5` | 入口（总览页页头弱图标按钮 → 外壳开窗）、设置页「分享海报」一节、`AppSettings` 三字段、29 键 × 10 语种 |
| `4d91d5f` | 独立评审修复：剪贴板位图生命周期、类型构成漏 cache write、图例硬编码英文、横版行内比例、字号尺度、长名省略、总量以 KPI 为准、Agent 环形 Top6+其他 等 |

交接 3.1–3.4 的完成方式：

- **3.1 横版**：实现（不是隐藏选项）。两种版式各带一套固定列定义的行容器，切换只移动面板 +
  换可见性——运行期改 Grid 定义数量会踩 Avalonia 测量缓存崩溃（实测），这条写进了台账 ruling。
- **3.2 品牌图标**：模型侧完成（走 `ModelAvatarResolver` → `ModelChip` 同一条链）。**Agent 侧未落**：
  总览基底里 Agent 只有环形图例，色点是「图例→弧段」的唯一映射；Agent 品牌标的落点是尚未实现的
  单日托盘基底。
- **3.3 入口**：总览页页头标题右侧弱图标按钮（`AppIconShare`，新增 lucide share-2 资源）；
  设置页「分享海报」（默认版式/主题/雾化档，改完即落盘）；`AppSettings` 三个字符串字段。
  对话框由 `MainWindow` 订阅外壳 VM 的 `SharePosterRequested` 后 `ShowDialog` 打开。
- **3.4 i18n**：29 键 × 10 语种（含后补的 `SharePosterDayCountFormat` / `SharePosterCopyFailedFormat`），
  对话框标题/选项/按钮/状态行全部走 `Translations`。
- **3.5 评审**：已完成（fresh-context reviewer 子代理），阻塞 3 项 + 重要 6 项已修。

### 剩余缺口（第一轮结束时）

1. ~~**单日托盘信息窗基底**~~ → 已补（见下节）。
2. ~~**Agent 品牌标**~~ → 随托盘 Agent 行落地。
3. ~~**剪贴板真机验证**~~ → 已补 FlushAsync 与 headless 往返用例；真机目检仍建议做一次。
4. ~~**对话框选项视觉**~~ → 已改分段胶囊。
5. ~~**FollowApp 实时跟随**~~ → 已改为继承主题。

## 8. 缺口收口（同日第二轮，commit `e839f5f`）

- **单日托盘基底**：`SharePosterView` 分两个基底，单日渲染「头部 + 三格 KPI + 构成条 +
  构成三卡 + 主要 Agent Top4 + 模型用量 Top3」的竖卡片（1080 宽、按内容高度，导出不再铺满宿主）。
  刻意不画信息窗的「记录中」状态点与「单日峰值」徽标（静态图上的运行状态是假的）。
- **Agent 品牌标**：托盘 Agent 行走 `AgentVisualMap → AgentChip` 伪类链（48px 品牌槽），
  用户自定义模型头像经 `ModelAvatarResolver` 生效。
- **剪贴板**：`FlushAsync`（X11/Wayland 不 flush 会在窗口退出后粘贴为空）+ headless 往返用例。
  交给人工的只剩「真实三平台复制→粘贴」一次目检。
- **分段选项**：ComboBox → RadioButton 胶囊（对话框私有一批选项实例，双绑 `IsSelected`），
  单日时版式组整组禁用并保留说明行。
- **FollowApp**：`RequestedThemeVariant = null` 继承应用主题，应用切明暗时海报与投影跟着变。
- i18n：新增 3 键 × 10 语种。最终 2006 个用例全绿（Headless 560）。

唯一遗留：真实 Windows / macOS / Linux 的剪贴板粘贴目检（无法在本机自动化）。
