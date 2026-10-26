---
id: D-018
title: 应用图标——托盘外三平台图标接线（exe / 窗口 / Dock）
type: feature
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-26
blocked: none
---

## Goal

补齐托盘之外的应用图标：此前托盘有图标（A-0），但窗口标题栏 / 任务栏 / Alt-Tab /
Windows exe 文件 / macOS Dock 全是系统默认图标，品牌不闭环。三平台一次接齐。

## Background

- 现状：托盘资产 `tray-icon.png`（彩色 64px）+ `tray-icon-template.png`（macOS 单色
  模板）只服务托盘；64px 撑不起 Windows ico 256px 层与 Dock 图标。
- 设计流程：按 app-icon-design 纪律走了两轮候选（`design/icon-candidates/` 全档归档，
  含深浅背景 × 16–512px 评审矩阵 `preview*.html`/`preview*.png`）。
  - 第一轮 4 方向：v1 绿柱续作 / v2 token 三色对角瀑布（自审 16px 散点，淘汰，v2.1
    缺角网格替代）/ v3 面积脉冲 / v4 品牌芯片 T 字。用户反馈：**只有 v1 可取，其余
    "很差劲"，要"高级感"**。
  - 第二轮保留绿柱基因换工艺（顶部受光深盘 + rim light + 暗角 + 高光渐变/落影）：
    v5 玻璃绿柱 / v6 极光绿柱 / v7 翡翠阶梯（融合轮廓）/ v8 同调暗盘单亮柱。
    用户裁决：**"还是 v1 最好看，就它吧"**——高级感工艺版全部不取。
  - Refine 结论（app-icon-design 第 3 步）：v1 的克制平面风被用户两轮连选确认，
    **不加不减**，按原 SVG 定稿烘焙。
- 平台图标语义（app-icon-design 平台几何表）：ico 全出血（Windows 原生尺寸直出，不带
  Apple 网格边距）；png/icns 走 Apple 网格（830/1024 内容块）；macOS 裸可执行（无
  .app bundle）的 Dock 图标只能运行时喂扁平位图，icns 只对 bundle 场景有效。

## Acceptance Criteria

- [x] `Assets/app-icon.ico`（全出血 16–256 七尺寸）挂 csproj `ApplicationIcon`——
      Windows exe 文件 / 固定任务栏图标
- [x] `MainWindow` 构造器设 `Window.Icon`（`app-icon-window.png` 全出血 256）——
      窗口标题栏 / 任务栏 / Alt-Tab / Linux WM（`_NET_WM_ICON`）
- [x] macOS Dock：`MacTrayInterop.TrySetDockIcon`（NSApplication
      applicationIconImage，喂 `app-icon.png` Apple 网格 PNG），App 启动 macOS 分支
      调用、headless 宿主跳过、失败静默降级（复用既有 ObjC 通道，新增 2 selector）
- [x] headless 回归锁 `WindowIconTests`：主窗 Icon 非空 + 图标三资产进包断言
- [x] 本机（macOS）冒烟 12s stderr 干净——托盘 interop 与 Dock interop 均无异常回落
- [x] 全量 `dotnet test Tokenmetry.sln` 1402 Passed（+2）

## Subtasks

- [x] `design/icon-candidates/`：两轮 8 版 SVG + 评审矩阵 + 烘焙 icns/预览表归档
- [x] make-icon.py 烘焙：ico（全出血）/ png（Apple 网格 1024）/ window png（Chrome
      直出 256 全出血）；icns 归档不进 AvaloniaResource（915KB 空耗，打包卡落地时现烘）
- [x] `Tokenmetry.App.csproj`：`<ApplicationIcon>Assets/app-icon.ico</ApplicationIcon>`
- [x] `Views/MainWindow.axaml.cs`：构造器设 Icon（AssetLoader 模式同 TrayService.CreateIcon）
- [x] `Tray/MacTrayInterop.cs`：`TrySetDockIcon`（sharedApplication /
      setApplicationIconImage: 两 selector，app 为 nil 时返回 false 不误报成功）
- [x] `App.axaml.cs`：macOS Dock 调用点（try/catch 不阻塞启动；未来改 .app bundle
      + Info.plist icns 时删除，注释留证）

## 修正项 1（2026-10-26）——window png 是裁切图，Windows 标题栏只见半张图标

用户 Windows 真机反馈标题栏图标只显示一半。取证：`Assets/app-icon-window.png`
alpha 解码不透明 bbox 仅 (122,0,255,255)——左 122px 全透明、内容贴死右/上/下画布
边，是"Chrome 直出 256"时截到局部视口的裁切图，Windows 标题栏只是忠实显示它。
ico（exe/固定任务栏）与 app-icon.png（macOS Dock）均不经过 Window.Icon 且资产完整
（7 帧阶梯逐帧核过），故只有标题栏暴露；macOS 标题栏本就不显示窗口图标，Mac 上不可见。

修法：ico 内 256 帧全出血渲染原样覆盖 `app-icon-window.png`，与 exe 图标像素级同源，
代码零改动。headless 回归锁 `WindowIconTests.Window_icon_png_is_full_bleed` +1
（alpha bbox 必须铺满画布；make-icon.py 烘焙自检覆盖不到这类管线外产物，只能测试里锁）。
全量 Core 189 / Reconciliation 49 / Infra 678 / Headless 397 全绿；App.Tests 4 失败经
stash 干净工作区复现为 SingleInstanceGuardTests 环境性失败（本机 TMPDIR 超 Unix 域
套接字 104 字符上限，B-020 家族），与本次零交集。Windows 真机标题栏目检移交用户。

## Dev Log

- 2026-10-24：两轮候选 + 定稿 + 接线同日收口。决策留痕：①用户两轮均选 v1 系——
  "高级感"诉求经工艺版对照后仍回落克制平面风，后续若再动图标以 v1 语义为基线；
  ②托盘图标有意不动（-template 单色语义独立，且与应用图标同符号家族，视觉自动
  连续）；③v2 对角瀑布的教训：对角构图受圆角盘约束，feature 只能缩到 16px 散点，
  已在候选文件头注释留档防止复踩。
- 真机目检移交用户（系统限制无法自动断言）：macOS Dock 图标（运行时设置，
  `NSRunningApplication.icon` 读不到，只能肉眼）、Windows exe/任务栏/Alt-Tab 图标
  （需 Windows 机，冒烟清单可并入 windows-publish.md 重写卡）。

## Dependencies

- none
