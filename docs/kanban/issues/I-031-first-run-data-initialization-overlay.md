---
id: I-031
title: 首次启动数据初始化（启动即刷可见反馈 + 全窗口初始化遮罩）
type: feature
priority: P2
size: S
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

首次使用时库内完全无数据，启动首轮采集扫描毫无可见反馈（顶栏停留"启动中…"、页面空白），用户体感是"不会自动刷新，要手动点一下才有数据"。启动路径改为显式初始化流程：进程打开即刷并即时反馈扫描态；首次使用挂全窗口"正在初始化"遮罩，轮次结束自动摘除。

## Background

- 启动首刷本来就有（App.axaml.cs 对 `BackgroundRefreshService.TriggerRefreshAsync` 的 fire-and-forget），缺的是反馈而非动作——顶栏状态在整个首轮期间停留在启动态，数据要等扫描完才由页面重查填入。
- 遮罩只覆盖"初始化"阶段（探测到库内完全无数据的首次使用）；回访用户页面有数据可看，不遮罩，只有顶栏扫描态。
- 成败均摘遮罩：no data / error 的诚实终态由顶栏状态灯（A-12 语义）呈现，遮罩不掩盖结果。
- 判定"完全无数据"用查询服务的无界日序列（契约：无界范围只返回实际有数据的日期），空集即库内无 usage facts；不引入新的存储探测接口。

## Acceptance Criteria

- [x] 进程打开即触发一轮采集刷新（与定时器/托盘/设置页重扫同一 BackgroundRefreshService 单飞槽），顶栏即时切"正在扫描本地数据…"，不再停留"启动中…"
- [x] 库内完全无数据（首次使用）→ 全窗口遮罩（scrim + 居中卡片 + 旋转 ◌ + 标题/副标题），拦截命中测试；轮次结束（成功 / no data / error 均然）摘除
- [x] 库内有数据（回访用户）→ 不挂遮罩，启动轮照常触发
- [x] 探测失败（查询抛错）→ 不挂遮罩但照常刷新（宁可不遮罩不误挡）；探测在线程池执行，SQLite 聚合同步段不卡 UI
- [x] 未注入刷新服务（测试 7 参构造）→ 如实 no-op
- [x] 新令牌 AppOverlayScrimBrush（ThemeDictionaries 明暗各一份）+ DesignTokenTests 清单同步 + design-tokens.md 补条目；外壳私有样式 overlay-* 留 MainWindow Window.Styles（页面卡勿复用）
- [x] 中英文案进 Translations（InitializingTitle / InitializingSubtitle，中文源 + 英文覆盖）

## Subtasks

- [x] MainWindowViewModel：IsInitializingData + BeginStartupDataRefreshAsync + ProbeStoreEmptyAsync（无界日序列空集判定，Task.Run 包裹，失败回落不遮罩）
- [x] MainWindow.axaml：InitializingOverlay（Grid.RowSpan 全窗 + ZIndex）+ overlay-card / overlay-spinner（RenderTransform 旋转动画）/ overlay-title / overlay-subtitle
- [x] App.axaml.cs 启动序列：`_ = refreshService.TriggerRefreshAsync()` 改走 `_ = mainWindowViewModel.BeginStartupDataRefreshAsync()`
- [x] Tokens.axaml / Translations.cs / design-tokens.md / DesignTokenTests 配套

## Dependencies

- D-003（应用外壳）、S-002（BackgroundRefreshService/Wiring 刷新线）、I-027（令牌体系）

## Test Cases

### TC-001: 启动初始化语义（headless VM）

Steps: stub 查询服务（空 / 非空 / 抛错）+ 门控 stub 刷新服务，调 BeginStartupDataRefreshAsync。

Expected: 空库 → IsInitializingData 置位 + 顶栏"正在扫描本地数据…" + 触发一轮；轮次成功或失败（动作返回 false）结束均摘遮罩；非空库不遮罩但触发；探测抛错不遮罩但触发；未注入刷新服务 no-op 且状态停留启动态。

Automated: StartupInitializationTests（6 用例）。

### TC-002: 遮罩 XAML 绑定

Steps: MainWindow 渲染，翻转 VM IsInitializingData。

Expected: x:Name=InitializingOverlay 的 IsVisible 随之翻转（默认不可见，不污染既有外壳/截图测试）。

Automated: StartupInitializationTests.Initializing_overlay_visibility_follows_view_model。

### TC-003: 遮罩令牌

Steps: DesignTokenTests 画刷清单校验（含 Semi 撞名守卫通道）。

Expected: AppOverlayScrimBrush Light #e6f4f6fa / Dark #e60b1120 逐位匹配，与 Semi 资源零撞名。

Automated: DesignTokenTests（PaletteBrushes 新增条目）。

## Dev Log

- 2026-10-15：DashboardViewModel.InitializeAsync 的自愈刷新线在首启与启动轮并发（LocalIngestionDataSource 管道信号量串行化，无害），有意不动——该采集线语义被 BackgroundRefreshServiceTests / SettingsPageTests 的 P0 回归锁定。
- 2026-10-15：明暗两帧离屏截图目检通过（临时截图用例即建即删，不留档）；旋转 ◌ 走 Avalonia 文档模式的 Style.Animations + RotateTransform.Angle 关键帧，headless 渲染静态帧无碍。
- 2026-10-15：手动刷新按钮不挂遮罩（只启动路径维护 IsInitializingData）；启动轮期间按钮的 ⟳/◌ 切换不联动（命令未在跑），信号由顶栏状态与遮罩承载，够用。
