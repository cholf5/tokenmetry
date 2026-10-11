---
id: D-002
title: ScottPlot.Avalonia 集成 spike
type: chore
priority: P2
size: S
status: done
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

验证 ScottPlot.Avalonia 5.1.59 在 Avalonia 12 + net10.0 上可用：用真实数据渲染一张 30 天成本趋势图，摸清缩放 / 悬停 / tooltip 的实现成本，把选型结论写回卡片，为 v0.4 正式集成扫清风险。

## Background

- 选型依据见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`：ScottPlot.Avalonia ≥ 5.1.59 依赖 Avalonia ≥ 12.0.0 且支持 net10.0，NuGet 已核实，但需要在本项目实测。
- 这是 spike 卡：产出是结论与原型，不是可发布功能；正式集成的卡在 v0.4 规划时创建。

## Acceptance Criteria

- [x] 安装 ScottPlot.Avalonia 并在临时页面用真实 facts 数据渲染 30 天成本趋势图
- [x] 缩放 / 平移可用（AvaPlot 默认交互开箱即用，零配置；已确认默认启用，**交互手感待人工目检**——自动化只验证到渲染与配置层面，未模拟真实鼠标拖动）
- [x] tooltip / crosshair 有可运行原型，实现成本有结论
- [x] 结论（可行性、风险、tooltip 方案、代码规模估计）记录在 Development Log

## Subtasks

- [x] 添加 ScottPlot.Avalonia 包引用（锁定 5.1.59，见日志）
- [x] 临时 spike 页面 + 真实数据趋势图
- [x] 缩放 / 平移与 tooltip 原型
- [x] 写结论并决定 v0.4 是否按原计划推进（结论：按原计划推进，无阻塞风险）

## Dependencies

- none（不依赖 I-00x，可先用现有 daily rows 数据）

## Test Cases

### TC-001: spike 运行

Steps:
1. `dotnet run --project src/CCUsage.App` 打开 spike 页面。

Expected:
- 趋势图渲染真实数据，交互可用，无崩溃。

<!-- spike 以人工验证为准，无需自动化测试 -->

## Development Log

### 2026-10-11

- 创建任务卡（v0.3 规划），尚未开始实现。

### 2026-10-11（spike 完成）

**结论先行：ScottPlot.Avalonia 5.1.59 在本项目（Avalonia 12.0.4 / net10.0 / macOS arm64）完全可用，v0.4 建议按原计划推进，无阻塞性风险。正式集成的 ScottPlot 相关工作量估计 1–2 天（趋势图本体 0.5–1 天 + tooltip helper 与打磨 0.5 天；Dashboard 重构为 KPI+趋势+TopN 的工作量另计）。**

#### 1. 版本兼容性实测

- 锁定 **ScottPlot.Avalonia 5.1.59**（本环境经 nuget.org 的 azure.cn 镜像核实为最新 5.x；与选型文档核实的版本一致）。nuspec 依赖：`ScottPlot 5.1.59`、`Avalonia ≥ 12.0.0`、`Avalonia.Skia ≥ 12.0.0`，与当前 Avalonia 12.0.4 兼容；包内含 net10.0 目标。
- NuGet 还原结果："compatible with all the specified frameworks"；`dotnet build CCUsage.sln` 0 警告 0 错误；`dotnet test` 242 通过 / 5 跳过（基线不变）。
- 真机 `dotnet run` 启动无崩溃、日志无异常，图表渲染成功（证据见 Verification）。

#### 2. Spike 实现（代码规模）

- `src/CCUsage.App/Views/TrendSpikeView.axaml(.cs)` + `ViewModels/TrendSpikeViewModel.cs`，经 CacheService.GetDailyRowsAsync 取最近 30 个日行（本机 facts 实际 9 个数据日，见第 5 节）。VM ~80 行；View XAML ~35 行；code-behind ~200 行（其中 ~70 行是验证钩子，正式集成不需要，实际核心逻辑 ~130 行）。
- 绘图：`Plot.Add.Scatter(xs, ys)` + `Plot.Axes.DateTimeTicksBottom()`；x 用 `DateTime.ToOADate()` 数值（ScottPlot 5 的日期约定）。共 4 个绘图 API 调用，成本极低。
- 导航侵入：MainWindow.axaml 仅追加一个 `TabItem`（Header "Trend Spike (D-002)"，页内自带红色 "[SPIKE]" 标识）；ViewModel 不经 DI 注册，spike 页在 code-behind 自建（AppServices.Get\<CacheService\>()），未改任何现有页面行为。

#### 3. 缩放 / 平移

- AvaPlot 内置 `UserInputProcessor` 默认启用：左键拖动平移、右键拖动框选缩放、滚轮缩放、双击自适应、右键菜单（保存图片/复制/自适应）——**spike 未写一行交互配置代码**。已确认默认启用且窗口正常渲染；鼠标拖动的手感属人工体验项，留待正式集成时目检。

#### 4. tooltip / crosshair 原型与实现成本

- 采用官方 Avalonia demo（ShowValueUnderMouseWindow）模式：`PointerMoved → e.GetPosition(control) → Plot.GetCoordinates(pixel) → Scatter.GetNearestX(coords, Plot.LastRender, 15px) → 更新 Crosshair.Position / Tooltip`，每次悬停后 `Refresh()`。核心逻辑 ~60 行。
- `plot.Add.Crosshair(x, y)` 是一等公民：两条线 + 端点 marker，改 Position/IsVisible 即可，成本≈0。
- `Plot.Add.Tooltip(...)` 是**静态气泡**（非自动 hover）：TipLocation / LabelText / LabelLocation / IsVisible 全部手动驱动。关键坑：Label 对齐语义是"锚点在气泡的哪个角"——`UpperLeft`=气泡从锚点向下延伸、`LowerLeft`=向上；贴近绘图区上边缘的数据点必须把气泡翻转到下方，否则整体顶出数据区被裁剪（本次已用"上半区 Lower/上半区 Upper 翻转"解决，见 UpdateHoverAt）。
- 正式集成建议：封装一个 ~100–150 行的 hover helper（拾取 + 对齐翻转 + 格式化），或改用 Avalonia overlay TextBlock 做悬浮提示（更简单，但气泡不在 plot 坐标系内）。结论：tooltip 成本可控，不是选型风险。

#### 5. 渲染性能与数据事实

- 本机真实数据（~/Library/Application Support/CCUsage/ccusage.db）：9 个有数据日（2026-01-28 .. 2026-09-25，Codex/gpt-5.2-codex），合计 $101.39，峰值 $63.16（02-07）——图表上的峰值点与 SQLite `SUM(cost_usd)` 对账一致。
- facts 读取 + 映射耗时 **7 ms**（VM 状态栏实测）；ScottPlot 为百万点级设计，30/90 天每日 1 点（≤90 点）毫无压力，无需考虑 Signal/DataStreamer 等优化路径。
- 观察到产品决策点（留给正式集成卡）：稀疏日期之间的长直线有误导性（看起来像持续花费），v0.4 需决定空日补零 / 断线 / 仅画实际日。

#### 6. Avalonia 12 集成注意点

- **命名空间冲突**：ScottPlot 与 Avalonia 存在同名类型（PixelSize、Pixel、Color 等），同文件混用需全限定（本次实测 CS1503：ScottPlot.PixelSize ≠ Avalonia.PixelSize）。建议正式集成时绘图代码集中在单一 code-behind/partial，避免 using 混战场。
- macOS 截屏（`screencapture`）依赖屏幕录制 TCC 权限，终端宿主进程被拒（"could not create image from display"，caffeinate 唤醒后仍失败）。降级方案：应用进程内离屏渲染（ScottPlot `Plot.SavePng` + Avalonia `RenderTargetBitmap`），零系统权限，已验证可行——正式集成如需 CI 截图证据可复用此钩子思路。
- Avalonia 11+ 组合器逐 Visual 调用 Render：对父级控件调 `Render(ctx)` 不会递归子树，离屏捕获必须直接渲染目标控件（AvaPlot 自身可渲染成功）。
- spike 页用 `SelectedIndex` 直开 spike tab 做截图验证后已还原；默认仍落在 Dashboard。

### 2026-10-11（独立审查）

- 独立审查结论 PASS with minor issues（无 P0/P1）。审查员以 /tmp 影子副本复跑离屏渲染钩子：三张证据图与原件**字节级一致**，tooltip 数值（2026-02-07 / $63.16 / 238,955,887 tok）与 sqlite3 直查 usage_facts 逐项对账一致；边界纪律核对通过（MainWindow 仅 +4 行 Tab、DI/现有页面零改动）。
- 修复/记录（均为 P2）：
  - Verification 复现步骤补注：离屏渲染钩子需切到 spike tab 触发 Loaded（仅设环境变量不触发）。
  - 看板簿记修正：I-003 曾同时留在 Todo 与 Done 两列（PM 移卡失误），已清理；本卡移 Done。
  - 记录在案："30 天趋势"实际取最近 30 个数据行（本机 9 个数据日横跨 8 个月）——v0.4 正式卡须定日历窗口语义（空日补零/断线/仅画实际日）；hover tooltip 右缘裁剪未处理（正式集成时并入 hover helper）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | （无阻塞 bug；tooltip 首版气泡贴边被裁剪属实现偏差，已在 UpdateHoverAt 用对齐翻转修复） | — | — |

## Verification

- Related files: `src/CCUsage.App/CCUsage.App.csproj`（+ScottPlot.Avalonia 5.1.59）、`src/CCUsage.App/Views/TrendSpikeView.axaml`、`src/CCUsage.App/Views/TrendSpikeView.axaml.cs`、`src/CCUsage.App/ViewModels/TrendSpikeViewModel.cs`、`src/CCUsage.App/Views/MainWindow.axaml`（仅追加 spike Tab）
- How to run/verify: `dotnet run --project src/CCUsage.App` → 切到 "Trend Spike (D-002)" tab；或设 `CCUSAGE_SPIKE_CAPTURE=/tmp/x.png`（可选 `CCUSAGE_SPIKE_CAPTURE_WINDOW=/tmp/y.png`）**并切到 spike tab**（TabControl 只在选中时挂载内容、触发 Loaded，审查实测仅设环境变量不触发）让 spike 页离屏输出证据图
- Results:
  - build 0 警告 0 错误；test 242 通过 / 5 跳过（Core 68 / Reconciliation 18 / Infrastructure 116 / App 40），基线不变。
  - 真机 `dotnet run` 启动无崩溃、日志无异常；状态栏实测 `loaded 9 rows in 7 ms; range=2026-01-28 .. 2026-09-25 · 合计 $101.39 · 峰值 $63.16/day`。
  - macOS `screencapture -x` 被屏幕录制 TCC 权限拒绝（宿主终端无权限，如实记录）→ 降级验证：应用进程内离屏渲染证据（均在仓库外 /tmp）：
    - `/tmp/ccusage-spike-plot.png` — ScottPlot 渲染的 30 天成本趋势（真实 facts）
    - `/tmp/ccusage-spike-plot.png.hover.png` — 峰值点合成悬停：crosshair + tooltip 气泡（2026-02-07 / $63.16 / 238,955,887 tok），与 SQLite 对账一致
    - `/tmp/ccusage-spike-window.png` — 活动窗口内真实 AvaPlot 控件实例的 Avalonia RTB 离屏渲染
  - 交互（缩放/平移拖动手感）与真实鼠标悬停：待人工目检（自动化仅覆盖渲染与拾取逻辑路径）。
