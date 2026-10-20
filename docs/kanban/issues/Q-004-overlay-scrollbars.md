---
id: Q-004
title: 全局滚动条重主题——overlay 语义（闲置隐藏 / 贴边 / 无箭头）
type: feature
priority: P1
size: M
status: done
created: 2026-10-20
updated: 2026-10-20
blocked: none
---

## Goal

替换 Semi.Avalonia 的 ScrollBar ControlTheme 为现代 overlay 语义，解决用户三点反馈：

1. **闲置隐藏**：滚动条不再常驻——滚动值变化或指针在滚动区内活动时淡入，闲置约 1s 淡出；悬停滑块/拖拽中钉住不隐。
2. **贴边**：6px 细条圆角滑块贴滚动视口右缘/底缘，无 Semi 收合态的 6px 空隙。
3. **无箭头**：Semi 的上下/左右 Line 箭头按钮从模板移除（Windows 11 上古审美）。

## Background

- 用户反馈（2026-10-20 会话）：「所有的滚动条都很奇怪：一直显示、位置不贴最右边、有上下箭头」。
- Semi 12.1.0.1 的 ScrollBar 收合态（`IsExpanded=False`）仍是 2px 常驻细条 + 右缘 6px 空隙，展开态有箭头——不满足现代 overlay 语义。Avalonia 12 ScrollBar 原生带 `AllowAutoHide`/`IsExpanded`/`ShowDelay`（Fluent 模板即 overlay），但 Semi 模板另起炉灶。
- 全局生效：TextBox / ListBox / 页面 ScrollViewer / 下拉弹层内的 ScrollBar 全部走同一 `{x:Type ScrollBar}` ControlTheme。
- 隐藏态 `IsHitTestVisible=False`——悬浮条不挡贴边内容（表格右缘单元格、贴边按钮的点击不被 10px 命中条吃掉）。

## Acceptance Criteria

- [x] 闲置（无滚动、无指针）滚动条完全不显示且不参与命中
- [x] 滚动值变化（滚轮/拖拽/程序滚动）立即显示，闲置延迟（默认 1s）后淡出（120ms，AppMotionFastMs 镜像值）
- [x] 指针在滚动区内活动即显；悬停命中条/拖拽滑块期间钉住不隐
- [x] 模板无 `PART_LineUpButton`/`PART_LineDownButton`（箭头消失）；Track 页按钮透明保留（点击滑块上/下方翻页，命名保留供 ScrollBar.OnApplyTemplate 挂接）
- [x] 滑块贴边：滑块右缘 = ScrollBar 右缘 = 滚动视口右缘（垂直）/底缘（水平）
- [x] 配色走令牌：`AppScrollBarThumbBrush` / `AppScrollBarThumbHoverBrush` 随明暗翻转（DesignTokenTests 固化 + Semi 撞名白名单）
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] `Controls/ScrollBarOverlay`：附加行为状态机（Value 变化/宿主指针活动/悬停/拖拽 → `overlay-shown` 类；IdleHideDelay 可注入；离树清理防模板重建泄漏）
- [x] `Styles/ScrollBarTheme.axaml`：ScrollBar ControlTheme 全量替换（无箭头模板 + 页按钮/滑块 ControlTheme + 显隐样式）+ `AppScrollBarPageButton`/`AppScrollBarThumb` 子主题
- [x] `App.axaml`：ResourceInclude 进 Application.Resources（资源查找先于 Styles 里的 SemiTheme，同键覆盖）
- [x] `Styles/Tokens.axaml`：明暗两套滑块画刷
- [x] `tests/.../ScrollBarOverlayTests.cs`：6 条 headless 契约测试
- [x] `docs/design/design-tokens.md` §9 滚动条章节

## Dependencies

- none

## Test Cases

### TC-001~006: headless 契约（ScrollBarOverlayTests）

闲置隐藏+无箭头 / 垂直贴边 / 水平贴边 / 滚动即显闲置即隐（含淡入淡出落定）/ 指针活动即显 / 悬停钉住。闲置窗经 `ScrollBarOverlay.SetIdleHideDelay` 注入 1ms，显示态断言用长窗（60s）避开 120ms 淡入与 1ms 收回的竞态。

### 真机人工核验（headless 能力外）

- 明暗 × 1x/2x 下滚动各页：手感（淡入淡出时长、1s 闲置窗是否合适）、贴边观感、悬停提亮。
- 离屏截图已核（2026-10-20，headless 渲染 Dark/Light 各一帧）：6px 圆角细条贴右缘、无箭头无轨道、两主题配色协调（Dark #3d5175 / Light #c2cbd9）。

## Development Log

### 2026-10-20（建卡即实施）

- 调证：Avalonia 12 ScrollBar 原生 `AllowAutoHide=true` 默认即收合态，但 Semi 模板收合态=2px 常驻细条+6px 空隙+悬停出箭头；Fluent 12 官方 ScrollBar.xaml 是 overlay 参照（收合隐藏轨道/箭头、thumb scale 贴边）。
- 方案：全量替换 ControlTheme 而非修补 Semi 样式——箭头从模板物理移除，几何自己定（命中条 10px、滑块 6px 贴边、无轨道底）。
- 踩坑一：Avalonia 12 禁止控件外部添加 `:` 前缀伪类（`Classes.ThrowIfPseudoclass`）——显隐改用普通类 `overlay-shown`，选择器机制等价。
- 踩坑二：`ControlTheme` 不能放 `<Styles>` 根（AVLN3000），放 ResourceDictionary + `ResourceInclude` 进 Application.Resources 才能覆盖 Semi（Styles 里的 StyleInclude 只收 IStyle）。
- 踩坑三：Avalonia 的 ScrollViewer 默认 `HorizontalScrollBarVisibility=Disabled`（Vertical 才是 Auto）——测试横条需显式开启；生产页需要横滚时同样注意。
- 踩坑四（测试时序）：headless Window 内容默认居中——指针坐标必须经 `TranslatePoint` 换算，不能假设 sv 在 (0,0)；1ms 闲置窗会在 RunJobs 内抢先收回，与 120ms 淡入竞态，显示态断言须用长闲置窗。
- 行为泄漏修复：模板重建遗弃旧 ScrollBar 实例（诊断实锤双实例），`DetachedFromVisualTree` 时停计时器/摘类/解绑宿主订阅，防死实例继续响应宿主活动。
- 有意不做：悬停加宽滑块（Windows 11 式 morph——单宽更克制，与 motion design 克制基调一致）；Semi 的滚动条右键菜单（STRING_SCROLLBAR_*，overlay 细条上无意义）；轨道底色（macOS 式纯滑块）。
- 测试结果：`dotnet build Tokenmetry.sln` 0 error 0 warning；全量 `dotnet test Tokenmetry.sln` = 1392 Passed / 0 Failed（Headless 331→337）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Controls/ScrollBarOverlay.cs`、`src/Tokenmetry.App/Styles/ScrollBarTheme.axaml`、`src/Tokenmetry.App/App.axaml`、`src/Tokenmetry.App/Styles/Tokens.axaml`、`docs/design/design-tokens.md`、`tests/Tokenmetry.App.Headless.Tests/Controls/ScrollBarOverlayTests.cs`、`tests/Tokenmetry.App.Headless.Tests/Styles/DesignTokenTests.cs`
- How to run/verify: 自动 = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter ScrollBarOverlayTests`；真机 = 卡片「真机人工核验」段
- Results: 完成（2026-10-20）。TC-001~006 全绿；全量 1392 Passed / 0 Failed。
