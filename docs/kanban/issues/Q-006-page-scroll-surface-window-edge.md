---
id: Q-006
title: 页面滚动面贴窗口边缘——PageHost 外边距移入滚动内容（留白自持）
type: feature
priority: P2
size: M
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

竖向滚动条贴**窗口**右缘（横向贴底缘、滑条全程从顶栏底到窗底），不再是"贴视口缘但视口离窗口缘 22px"的悬空态；页面内容视觉间距（22,22,22,30）一字不变。

## Background

- 用户反馈（2026-10-24，Q-005 验收通过后）：滚动条"总是贴不住最右边"。
- 取证（Phase 1-3 完成）：滚动条本身贴视口缘无任何空隙（ScrollBarTheme 模板 + Semi ScrollViewer 模板查证 + headless 契约），22px 空隙唯一来源是 `MainWindow.axaml` PageHost `Margin="22,22,22,30"`——页面根 ScrollViewer 被整体推入，视口够不到窗口缘。属 Q-004"贴滚动视口右缘"语义与用户"贴窗口右缘"预期的偏差，非回归。
- 同源问题：竖条顶端停在顶栏下 22px、底端停在窗底上 30px；横向条悬在窗底上 30px。
- 修法（macOS 式滚动面语义）：**滚动面铺满主内容列，留白放进滚动内容里**——PageHost 外边距归零，各页根滚动面经共享类 `ScrollViewer.page-root`（Padding=22,22,22,30）自持留白。Semi 模板已查证把 Padding 透传给 ScrollContentPresenter、不影响滚动条位置；7 个页面根 SV 首子元素均为 `Margin="0,0,0,4"`（无左右上边距），Padding 迁入不产生双倍留白（底 30+4=34 与迁移前一致）。

## Acceptance Criteria

- [x] 页面根滚动视口右缘 = 窗口右缘（总览 / 数据与计价实测；其余 6 页同一 page-root 类）
- [x] 滚动后竖向滚动条右缘 = 窗口右缘；PageHost 外边距归零后滑条纵向全程覆盖（顶栏底→窗底）
- [x] 内容留白不变：滚动内容右缘离窗口右缘 ≥20px；会话页页头顶部间距 = 顶栏 52 + 22
- [x] 会话页（无页面级滚动面）：头尾/面板自补边距，观感与迁移前一致；列表滚动条贴卡片内缘属有意设计（卡内滚动，macOS 列表在卡内同款语义），不锁窗口缘
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] `Styles/SharedStyles.axaml`：新增 `ScrollViewer.page-root` 共享类（Padding=22,22,22,30，单一出处）
- [x] `Views/MainWindow.axaml`：PageHost `Margin="22,22,22,30"` → `0`
- [x] 8 个页面根 ScrollViewer 挂 `Classes="page-root"`（总览/Agent/模型/项目/时间/成本/设置 + 数据与计价内层）
- [x] `Views/SessionsView.axaml`：页头 Margin="22,22,22,0"、面板 Margin="22,0,22,0"、错误面板 Margin="22,0,22,30"（自补 PageHost 移除的边距）
- [x] `Shell/PageScrollEdgeTests.cs`：窗口右缘贴边 + 内容留白自持契约

## Dependencies

- Q-004（overlay 滚动条语义）、Q-005（显示条件收窄，验收已过）

## Test Cases

### Shell/PageScrollEdgeTests（headless，真实 MainWindow + 页面路由）

- 总览页：根 SV 右缘 = 窗口右缘（RED 实锤：迁移前差 22px）；滚动后竖条右缘 = 窗口右缘；滚动内容右缘 ≤ 窗口右缘 - 20（留白自持，迁移前后均绿）
- 数据与计价页：内层 SV 右缘 = 窗口右缘（根 Grid 包裹结构下 page-root 仍生效）
- 会话页：面板右缘 ≤ 窗口右缘 - 20（卡片留白保持）；页头顶部 = 顶栏 52 + 22（顶部间距保持）

### 真机人工核验（headless 能力外）

- 逐页滚动：滑条贴窗口右缘、纵向全程覆盖的观感；横向条（数据与计价页窄窗）贴底缘。
- 明暗 × 1x/2x：内容间距与迁移前无差异。

## Development Log

### 2026-10-24（建卡即实施，TDD）

- RED：`PageScrollEdgeTests`（真实 MainWindow + 页面路由）——视口右缘实测 938 = 960 − 22，差值恰为 PageHost 外边距，判别性实锤；内容留白断言（右缘 ≤ 窗口右缘 − 20）迁移前后均绿，按设计作"视觉不变"守卫。
- GREEN：`SharedStyles` 新增 `ScrollViewer.page-root`（Padding=22,22,22,30 单一出处）→ PageHost `Margin="0"` → 8 个页面根 SV 挂类（7 直根 + 数据与计价 Grid 内层）→ SessionsView 头/面板/错误面板逐边自补（22,22,22,0 / 22,0,22,0 / 22,0,22,30 = 原 PageHost 逐边拆分）。
- 踩坑一：会话页顶部间距断言期望 74 实测 82——MainWindow 进场过渡（Y 8→0，180ms）在 headless 不随 RunJobs 走帧，视图悬在起始 Y+8；需泵渲染帧（ForceRenderTimerTick）到几何落定再断言。
- 踩坑二：泵判定容差 1px 宽于 Assert.Equal 的 0.05 精度，过渡尾段 74.937 提前退出翻车——泵判定收紧到 <0.01。
- 全量 `dotnet test Tokenmetry.sln` = 1452 Passed / 0 Failed。注：树上同时存在并行工作流 I-040（矢量图标，卡已收口）的未提交改动，全量运行已将两流改动共同验证；MainWindow/OverviewView/SharedStyles/board.md 为两流共享文件，提交排序经用户确认后执行。
- 有意不做：会话页列表滚动条贴窗口缘（卡内滚动语义，macOS 列表在卡内同款，卡片留白保持）；page-root Padding 令牌化（外壳布局常量，样式类单一出处已够）；StylePreviewView 预览宿主适配（dev-only）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Views/MainWindow.axaml`、`src/Tokenmetry.App/Styles/SharedStyles.axaml`、`src/Tokenmetry.App/Views/SessionsView.axaml`、8 个页面视图根 ScrollViewer、`tests/Tokenmetry.App.Headless.Tests/Shell/PageScrollEdgeTests.cs`
- How to run/verify: 自动 = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter PageScrollEdgeTests`；真机 = 卡片「真机人工核验」段
- Results: 完成（2026-10-24）。PageScrollEdgeTests 绿；全量 1452 Passed / 0 Failed。
