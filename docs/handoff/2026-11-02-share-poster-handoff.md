# 交接摘要：分享海报（Share Poster）

**日期**：2026-11-02
**状态**：骨架可跑通（8 提交、543 用例全绿），**还差 4 件未完成**，不可直接交付
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

## 6. 续跑方式

台账（含每条任务记录、8 条 Ruling、未完成清单）：
`.worktrees/share-poster/.superpowers/sdd/2026-11-02-share-poster/progress.md`

新会话第一步建议：先跑三条验证命令确认基线全绿，再读 `progress.md` 的「未完成清单」，然后从 3.1 开始。

`docs/plans/` 与 `.superpowers/` 都在 gitignore 里，这两个目录的内容只存在于本机工作区，**不要指望它们跟着分支走**。
