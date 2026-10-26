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

- [x] 主通道 `NSAppleEventManager` 'aevt'/'rapp' handler：挂在 delegate 对象类上的 `tokenmetryReopenAppleEvent:withReplyEvent:`（类型串 `v@:@@`），事件直达 `DockReopenAEImp` → `ShowWindow`
- [x] 纵深通道：正确拼写 `applicationShouldHandleReopen:hasAnyVisibleWindows:`（`B@:@B`，IMP 返回 YES）仍挂 delegate 类；add 失败 = 未来 Avalonia 自己接对了名字 → 跳过不抢语义
- [x] 时序纪律：AEM 注册必须晚于 NSApp `finishLaunching`（早注册会被 NSApplication 自装的 'rapp' handler 顶掉，实测复现）——App 接线 `Dispatcher.UIThread.Post`
- [x] cmd-H 应用级隐藏兜底：AEM 通道替换 NSApp 默认 'rapp' handler 后补 `unhide:`（未隐藏时 no-op）
- [x] 异常纪律：两个 IMP 托管侧吃干净异常 + 堆栈留痕（跨 ObjC 边界抛出即崩进程）；安装失败不阻塞启动
- [x] AX 模拟真机验证通过：关窗（orderOut）→ 点 Dock 图标 → 窗口唤回；stderr 无错误
- [ ] 用户真机 TC：①关窗后点 Dock 图标唤回；②弹层开着点 Dock → 弹层收起主窗前置；③Cmd+Tab 切走再点 Dock 带回；④主窗最小化点 Dock → 脱最小化

## Dependencies

- none（B-019 Show All Windows 崩溃是独立卡，勿混谈——那是 AppKit 窗口概览层的崩溃，本卡只管 reopen 缺失）

## Development Log

### 2026-10-26（第二轮：用户反馈未解决，真机取证推翻初始修法）

- **症状复现**：修复后点 Dock 图标依旧无反应。用户以 `dotnet run` 运行（排除旧包干扰）。
- **取证过程**（全部真机 macOS 26.5，arm64）：
  1. 启动期 stderr 零日志 → `TryInstallDockReopen` 三条降级路径均未触发，`class_addMethod` 成功；
  2. AX 关窗后 AXPress Dock 磁贴 → 窗口不回；pid 直发 'rapp' AppleEvent（回执无 errn=被处理）→ 回调仍不触发；
  3. 静态解剖 Avalonia.Native dylib + 上游源码（app.mm@12.1.4）：`AvnAppDelegate` 实现的是**拼写错误的** `applicationShouldHandleReopen:hasVisibleWindows:`（缺 "Any"）——本意转发 `_events->OnReopen()` → 托管 `ActivatableLifetime(Reopen)`，整条链死在 selector 名字上，是上游 bug；
  4. 但补挂正确 selector 后 delegate 自己 `respondsToSelector=YES`，事件送达仍不回调；**原生对照实验**（20 行裸 AppKit 应用）同样复现 → macOS 26 裸可执行进程的 reopen 处理不咨询 delegate，与 Avalonia 无关；
  5. 自有 AEM 'rapp' handler 在启动期注册被顶掉 → 定位出**时序约束**：NSApplication 在 `finishLaunching` 才自装 'rapp' handler（AEM 单 handler 顶替语义）；挪到 finishLaunching 之后注册 → 事件直达 `DockReopenAEImp`。
- **修订修法**：主通道改自有 AEM 'rapp' handler（`Dispatcher.UIThread.Post` 延迟注册），原 delegate 补挂降为纵深通道；AEM 通道补 `unhide:` 覆盖 cmd-H 应用级隐藏；全部诊断埋点移除。
- **验证**：AX 关窗 → AXPress Dock 磁贴 → 窗口唤回（onscreen=false→true），stderr 干净；全量 Core 189 / Reconciliation 49 / Infra 678 / Headless 399 全绿；App.Tests `SingleInstanceGuardTests` 4 失败经 stash 对照在干净 HEAD 同样复现（TMPDIR 管道路径超 104 字符上限，B-020 环境性问题，与本卡无关）。
- **真机 TC 四项移交用户**（AX 模拟已过，真实手感待用户确认）。lldb 取证注意：batch 会话异常退出会被 debugserver 杀目标进程（本案误杀一次，.ips 可辨 `byProc: debugserver`）。
