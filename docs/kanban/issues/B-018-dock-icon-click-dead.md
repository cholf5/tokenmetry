---
id: B-018
title: macOS 点 Dock 图标无反应——applicationShouldHandleReopen 未接线，经 interop 挂委托类补全唤回
type: bug
priority: P2
size: S
status: done
created: 2026-10-26
updated: 2026-10-26
blocked: none
---

## Goal

macOS 上点 Dock 图标：主窗隐藏时唤回、可见时带到前台（macOS 系统惯例，`applicationShouldHandleReopen` 语义）；与 B-013 第二实例激活、托盘「打开主窗」同一 `ShowWindow` 语义。

## Background

- 用户报告：关主窗（红色关闭钮，OnClosing → Cancel + Hide）后 Dock 图标仍在且亮灯，但点图标无任何反应——唯一唤回路径是菜单栏弹层「查看详细」或右键菜单「打开主窗」。
- 根因：macOS 惯例要求应用实现 `applicationShouldHandleReopen(_:hasAnyVisibleWindows:)` 响应"点 Dock 但无可见窗"；Avalonia 12.1.4 的 Avalonia.Native **不实现该方法**（官方文档无此钩子；dock 生命周期接线不全的旁证：上游 #20683 dock 菜单 Quit 不可取消）。窗口 orderOut 后 AppKit 不会自动带回来。
- 产品裁决（本次会话用户拍板）：**保 Dock 图标 + 补全交互**——尊重 package.sh:110 既有决策"普通应用，不设 LSUIElement——主窗与 Dock 图标都是产品语义"（推翻转纯菜单栏应用的方案被否）。
- 临时解法已在：双击 .app 二开经 B-013 单实例管道发 show 请求也能唤回（但不符合直觉，本卡正是消灭这个"直觉缺口"）。

## Acceptance Criteria

- [x] `MacTrayInterop.TryInstallDockReopen(Action)`：取 `NSApplication.sharedApplication.delegate` 实际类，`class_addMethod` 挂 `applicationShouldHandleReopen:hasAnyVisibleWindows:`（类型串 `B@:@B`，IMP 返回 YES）
- [x] 已有实现时的安全降级：`class_addMethod` 失败（未来 Avalonia 版本自己接了 reopen）→ 跳过接线 + 诊断日志，不替换他人语义
- [x] IMP 异常纪律：托管回调异常在托管侧吃干净（跨 ObjC 边界抛出即崩进程，与 TrayButtonActionImp 同款），堆栈留痕
- [x] App 接线：macOS 且非 headless 时在 `trayService.Initialize()` 后安装，回调直调 `trayService.ShowWindow`（主线程即 UI 线程，与 interop 左键回调同约定）；安装失败不阻塞启动
- [x] 回调语义复用 `ShowWindow`（Show + 脱最小化 + Activate）：主窗隐藏 → 唤回；可见 → 带到前台；弹层开着 → 主窗激活引发既有失焦通道自然收起
- [ ] 真机 TC（移交用户）：关窗后点 Dock 图标唤回主窗；弹层开着点 Dock → 弹层收起主窗前置；Cmd+Tab 切走再点 Dock 带回

## Dependencies

- none（B-019 Show All Windows 崩溃是独立卡，勿混谈——那是 AppKit 窗口概览层的崩溃，本卡只管 reopen 缺失）

## Development Log

### 2026-10-26

- 建卡即实施。interop 无 headless 可测面（D-016/D-018 同款先例：ObjC 层真机 TC，托管侧全量套件作回归网），TDD 例外按仓库 interop 卡惯例登记。
- 实现两件：`MacTrayInterop.TryInstallDockReopen`（selector 缓存补 `delegate` / `applicationShouldHandleReopen:hasAnyVisibleWindows:`；`DockReopenImp` 走既有 UnmanagedCallersOnly + 托管侧吃异常纪律，hasVisibleWindows 不区分对待统一 ShowWindow；add 失败 / delegate 未就位两类降级路径均有诊断日志）+ `App.axaml.cs` 在 B-013 监听块后接线（mac 且非 headless，直调 `trayService.ShowWindow`——AppKit 主线程即 Avalonia UI 线程，与 interop 左键回调同约定）。
- 全量：Core 189 / Reconciliation 49（19 跳过）/ Infra 681 / Headless 399 全绿；App.Tests 265+1 失败——`SingleInstanceGuardTests` 同款环境性 flake（证据已钉死与本卡及真 App 进程均无关，正式立卡 **B-020**，查清前验证遇此失败按该卡豁免）。
- 真机 TC 清单移交用户：①关主窗后点 Dock 图标 → 主窗唤回；②弹层开着点 Dock → 弹层收起主窗前置；③Cmd+Tab 切走再点 Dock → 带回；④主窗最小化点 Dock → 脱最小化到前台。
