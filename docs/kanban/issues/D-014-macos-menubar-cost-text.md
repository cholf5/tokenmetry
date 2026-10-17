---
id: D-014
title: 状态栏直显今日用量（位图方案真机不可行 B1 → 决策 C 回落模板图标，关卡）
type: feature
priority: P2
size: M
status: done
created: 2026-10-17
updated: 2026-10-17
blocked: none
---

## Goal

macOS 菜单栏（及 Windows 托盘可选）图标直接渲染「42.4K · $12.46」级紧凑用量文本，无需展开菜单——补齐 A-0「托盘常驻显示今日成本」（pivot §5）的另一半，也是状态栏快捷信息窗（D-015/D-016）的入口价值前提。

## Background

- **规格缺口**（2026-10-17 用户会话核实）：macOS 菜单栏图标没有 tooltip——`UpdateToolTip` 写的 tooltip 在 Mac 上渲染不出，菜单首行 `_costItem` 是 Mac 上成本的唯一出口（`TrayServiceTests.cs:57` 注释同口径）。Windows 靠 tooltip 算是兑现了规格，Mac 只兑现了一半：状态栏本身不显示任何信息，想看成本必须展开菜单。
- **路径评估**（2026-10-17 会话结论）：
  - ① 纯托管 hack（本卡方案）：成本文字动态渲染成单色位图 → `WindowIcon`，改数字时重绘。不动 Avalonia 外部 API，成本低；难点是 22pt 状态栏高度下小数字的字形与 DPI（1x/2x）调优，做不好会显糊。
  - ② 原生 interop（iStat 菜单类做法）：NSStatusItem + NSTextField，需 unsafe/objc interop 层，工程量大，**本阶段不做**；仅当位图方案画质不可接受且证据充分时再议。
- 可复用路径：`TrayService.CreateIcon()` 已有运行时换图入口；`Dashboard.PropertyChanged → UpdateToolTip(BuildCostText(...))` 已有成本联动管线（KPI 更新即回写），重绘挂钩同一信号。
- 约束：保持 template 单色语义（macOS template icon 是 alpha 蒙版位图，跟随明暗菜单栏自动反色——文字位图同样必须 alpha-only，禁用彩字）；紧凑数字格式（$12.50 → $12.5 / $1.2k）宽度才放得下；无数据 / 扫描中态有诚实降级字形。
- **原型补充规格**（2026-10-17 状态栏路线定稿，原型 `docs/design/tokenmetry_statusbar_prototype.html` 底部 tray chip）：状态栏文本 = 紧凑 tokens + 成本（`42.4K · $12.46`）；Warn 态可切警告字形（`⚠ 38.2K/min`，可选）。Windows 托盘 tooltip 已有成本展示，Windows 侧是否同步上文字位图在本卡实施时决策（macOS 优先——tooltip 在 Mac 渲染不出）。

## Acceptance Criteria

- [ ] macOS 状态栏直接渲染当日成本文本——**未交付**：B1 取证位图方案不可行（Avalonia 方形状态项裁剪宽位图、托管无长度出口），用户决策 C 关卡回落模板图标；文本信息改由菜单首行 + D-015/D-016 弹窗承接
- [x] 明暗菜单栏两态 + 1x/2x DPI 截图留档目检——真机目检已做（2026-10-17 用户截图）：不是画质问题，是**方形裁剪**，位图方案在 macOS 出不了可读文本
- [x] 无数据 / 异常态有诚实降级（"—" 或回落纯模板图标），不留陈旧数字——降级分派随位图路径一并撤除，语义记录在案
- [x] Windows 行为零变化（仍 tooltip + 菜单首行；`CreateIcon` 的 Windows 资产路径不动）
- [x] 位图方案不可行的证据已记录（B1）且**决策已做（C：回落模板图标、关卡）**，不静默搁置

## Subtasks

- [x] TrayService：macOS 分支动态位图渲染（文字 → alpha-only 位图 → WindowIcon），挂 Dashboard KPI 信号重绘
- [x] 紧凑成本格式化（独立 TrayStatusText，阶梯规则与图表 FormatCompact 对齐、单位制沿原型 ASCII；为何无 Translations 新键见 Dev Log）
- [x] 无数据 / 扫描中 / 刷新失败三态降级字形（`ResolveStatusGlyph` 纯函数：— / … / 模板回落）
- [x] 真机 macOS 截图验证（明暗 × 1x/2x）——用户目检完成：发现 B1 裁剪（非画质问题）

## Dependencies

- none（寄生在 TrayService 既有换图路径与成本联动管线上）

## Test Cases

### TC-001: 成本更新触发重绘（headless 可测部分）

Steps: 构造 TrayService（注入 dashboard fixture）→ 改 `TodayCostUsd` → 断言图标被替换（非原始模板资产）与菜单首行文案同步。

### TC-002: 真机画质验收（移交用户）

Steps: macOS 真机明暗两态截图（1x/2x）→ 目检字形锐度与反色正确性。

Expected: 数字可读不糊；暗色菜单栏白字、亮色黑字（template 反色）。

### 真机人工核验（headless 能力外，移交用户）

- 启动应用看菜单栏：状态栏直接显示「紧凑 tokens · 成本」文字（如 `42.4K · $12.46`），无需点开菜单。
- 系统设置切深/浅色菜单栏：文字随 template 语义自动反色（暗栏白字、亮栏黑字），无彩边残影。
- Retina 屏（2x）与外接 1x 屏各看一眼：字形锐度可接受（不糊即过；嫌小/糊先调 `TrayStatusIconRenderer.LogicalFontSize`，嫌宽调紧凑口径）。
- 清空/改小系统时间无新数据场景：显示「—」而非陈旧数字；制造一轮失败刷新（如断源）回落纯模板图标。
- 菜单首行仍为「今日成本：$x.xx」且与状态栏数字同源同步。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B1 | P1（规格级，阻塞） | 真机（macOS arm64，2026-10-17 用户截图）：状态栏只见「M ·」而非全文本。根因：Avalonia 12.1.4 `trayicon.mm` L15 `statusItemWithLength: NSSquareStatusItemLength` 固定方形（~24pt）且全文件无 `setLength`，NSStatusBarButton 不随图片加宽 → 宽位图按 17pt 高缩放后水平**中段被裁**。本机今日文本 "324.2M · $0.00"（facts 库 324,221,037 tokens / $0.00），中段恰为「M ·」，证据闭环。非画质调优可解（24pt 内塞下全文本 = 不可读）；托管侧无出口（`MacOSProperties` 仅 IsTemplateIcon、`TrayIcon` 无长度属性、`IAvnTrayIcon` vtable 无 SetLength）。 | closed | 用户决策 **C：回落模板图标、关卡**（2026-10-17）。位图路径（渲染器/字形分派/托盘接线）撤除；`TrayStatusText` 格式化器与测试保留，D-015 弹层复用。上游 `setLength` 修复可另议（未承诺）。 |

## Development Log

### 2026-10-17

- 建卡（S-005 实施会话顺带定案的规格缺口）：Mac 状态栏成本唯一出口是菜单首行，状态栏本身零信息；方案选定纯托管位图渲染，interop 方案明确不在本阶段。
- 2026-10-17（晚）状态栏路线定稿：升 P2，标题从「macOS 状态栏直显今日成本」放宽为「状态栏直显今日用量」，并入原型 tray chip 规格（紧凑 tokens + 成本，Warn 态可选）；popover 路线拆 D-015（内容层）/ D-016（壳层），本卡是其入口前提。

### 2026-10-17（实施，真机目检移交用户）

- **尺寸依据（清晰度的根据）**：读 Avalonia.Native `trayicon.mm` SetIcon——任意位图统一缩放到 `floor(菜单字体 13pt × 4/3) = 17pt` 高（宽度等比），且 `MacOSProperties.SetIsTemplateIcon` 经 `setTemplate:` 只取 alpha 通道。据此 `TrayStatusIconRenderer` 按 **2x 像素空间（34px 高）** 渲染：retina 1:1 无重采样，1x 屏轻微降采样；不透明黑字画透明底即 alpha-only（颜色无意义）。运行时换图链路核实过：`TrayIcon.OnPropertyChanged(IconProperty) → impl.SetIcon` 实时推到 NSStatusItem。
- **紧凑口径定案**：独立 `TrayStatusText`（App.Tests 纯函数覆盖），阶梯规则（≥1000 取整 / ≥100 一位小数 / 其余两位 + 取整越档升档）与图表 `FormatCompact` 同构，但单位制**沿原型 ASCII 千进制 K/M/B/T、不随 UI 语言**——原型在中文 UI 下即为 `42.4K`，状态栏与弹层（D-015 走万/亿）口径差异是原型自身选择。成本 1000 以内保持 `F2`（与菜单首行/对账口径一致），≥1000 进阶梯（`$1.23K`）。
- **无 Translations 新键的理由**：位图文本是纯数据（数字 + `·` + `$`），无词可译；降级字形 `—`/`…` 语言中立。i18n 纪律不受影响——菜单首行仍走既有 `TrayCostFormat`。
- **三态分派**抽成 `internal static ResolveStatusGlyph(status, hasData, isLoading, cost, tokens)`（沿 S-005 `ResolveUserCloseAction` 纯函数先例）：有数据→文本；无数据→`—`；扫描中尚无值→`…`；**刷新失败（Status=="error"）→null = 回落纯模板资产**（宁可少显示不留陈旧数字，验收口径）。扫描中已有值保持原文案——一分钟的周期刷新不该让状态栏闪动。字形未变则跳过重绘（`_lastStatusGlyph` 缓存）。
- **踩坑**：headless 宿主 `WindowIcon.Save` 是 no-op（写出零字节），TC-001 首版「字节级对比图标内容」不可用 → 加 `TrayService.LastStatusGlyph` internal 接缝断言字形语义（沿 `MainWindow.ActiveHintWindow` 先例），图标实例换新另证重绘发生；真机观感归 TC-002 目检。
- Avalonia 12.1.4 API 备注：`FormattedText` 无公开 `Draw`/`pixelsPerDip` 参数，文本绘制走 `DrawingContext.DrawText(FormattedText, Point)`；`Bitmap.Save(Stream)` 弃用，改 `Save(Stream, PngBitmapEncoderOptions.Default)`。
- 自检探针（临时测试落盘 PNG，已删）：headless 渲染 `42.4K · $12.46` 位图经 Pillow 合白底目检——字形完整清晰、alpha 满量程（0→255）、RGB 全 0，template 语义正确；不能替代真机明暗/DPI 目检。
- 有意不做：Warn 态警告字形（`⚠ 38.2K/min`，可选未做）——速率=刷新差值÷间隔的口径归 D-015 内容层，速率计算不该寄生在托盘；Windows 侧同步上文字位图（tooltip + 菜单首行已达标，本卡 Windows 零变化）。
- 测试结果：`dotnet test Tokenmetry.sln` 全绿 = Passed 1300 / Failed 0（Core 159 / Reconciliation 49 / App.Tests 151 / Infrastructure 652 / Headless 289）；其中本卡新增 TrayStatusTextTests 23 条（格式化 + 四态分派）+ TrayServiceTests 2 条（KPI 更新换图 + 菜单首行同步；失败回落模板）。

### 2026-10-17（真机目检反馈：B1 裁剪，位图方案在 macOS 不可行，出路决策待定）

- 用户真机截图：状态栏只显示「M ·」。取证链（全部落实）：① 模板资产是三柱图字形，排除旧构建干扰（跑的确实是新构建）；② 本机 facts 库 `2026-10-17` 行 = 324,221,037 tokens / $0.00 → 应渲染文本 "324.2M · $0.00"，方形按钮中段裁剪恰好剩「M ·」，与截图逐字吻合；③ 安装版 12.1.4 tag 的 `trayicon.mm` 与 master 一致：`statusItemWithLength: NSSquareStatusItemLength` 写死、无 `setLength`；④ 托管侧三处（`MacOSProperties`/`TrayIcon`/`IAvnTrayIcon`）均无长度出口。
- **结论：这是硬裁剪不是画质问题**——方案①（位图）在 Avalonia TrayIcon 上的天花板就是方形 ~24pt，调 `LogicalFontSize`/紧凑口径都救不了（塞得下的文本不可读）。Acceptance #5 预案触发。
- 出路三选（已向用户提交决策）：**A. 升级 interop（卡内预留方案②）**——自建 NSStatusItem（`NSVariableStatusItemLength`）+ `button.title` 真文字（顺带免位图：任意 DPI 锐利、系统自动反色），但菜单/点击/tooltip 全原生重建（objc_msgSend P/Invoke 估 300–500 行），只能真机验证，Windows 路径不动；**B. 方形紧凑字妥协**——24pt 内渲染 3–4 字符（"324M" 或 "$0"），成本/tokens 二选一，偏离原型规格，快；**C. 回落模板图标关卡**——文本放弃，看板数走菜单首行 + D-015/D-016 弹窗，另可给 Avalonia 提上游 `setLength` 修复（周期不可控）。
- 卡片状态回退 todo（blocked: B1 决策）；headless 断言与格式化器（TrayStatusText / ResolveStatusGlyph）无论哪条出路都保留复用。

### 2026-10-17（用户决策 C：回落模板图标、关卡，收口）

- 三选出路提交用户（A interop 真文字 / B 方形紧凑字妥协 / C 回落关卡），**用户选 C**。
- 落地内容：`TrayService` 撤除 macOS 文字位图路径（`RefreshCostPresentation`/`UpdateStatusImage`/`_lastStatusGlyph`/`LastStatusGlyph` 接缝、扩展的 KPI 订阅全部还原为 tooltip+菜单首行原语义）；删除 `TrayStatusIconRenderer.cs` 与 `ResolveStatusGlyph`（无生产消费者，不做死代码——三态语义已在本卡 Dev Log 存档，D-015 banner 三态是另一套状态模型，届时按需重建）；`TrayStatusText` 格式化器及 19 条纯函数测试**保留**（D-015 弹层 chip 复用，注释已改写指向 D-015）。
- 回归：全量 `dotnet test Tokenmetry.sln` = Passed 1292 / Failed 0（App.Tests 151→145 撤 ResolveStatusGlyph 六例，Headless 289→287 撤托盘位图两例，其余不动）。
- 重启真机即恢复三柱模板图标（本轮回退正是回到 0f05cab 之前的托盘形态；`dotnet run --project src/Tokenmetry.App` 无需任何配置）。
- 有意不做：Avalonia 上游 `setLength` 修复未提交（外发动作待用户另起话头）；状态栏文本路线的下一站是 D-015/D-016 弹窗（点击即见全量 chip），若未来仍要免点击文本，走 interop 方案②并携本卡 B1 证据开新卡。

## Verification

- Related files: `src/Tokenmetry.App/Tray/TrayService.cs`（回落后=原始 A-0 语义）、`src/Tokenmetry.App/Tray/TrayStatusText.cs`（保留，D-015 复用）；测试 `tests/Tokenmetry.App.Tests/Tray/TrayStatusTextTests.cs`
- How to run/verify: 真机重启应用 → 状态栏恢复三柱模板图标，成本出口=菜单首行/tooltip（与 S-005 时点一致）
- Results: 关卡收口（2026-10-17，决策 C）。全量 Passed 1292 / Failed 0；格式化器随 D-015 复活。
