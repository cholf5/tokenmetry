---
id: D-019
title: 弹层模型用量区——Top3 按今日已知成本（未知沉底 / 诚实占比 / 无数据整块隐藏）
type: feature
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

托盘快捷信息窗（D-015）新增「主要模型」区：回答"今天钱都花在哪个模型了"。
Agent Top4 回答的是"哪个 agent 在跑"，与"钱花在哪个模型"正交——模型间价差十倍以上，
token 占比 ≠ 成本占比，现有 KPI（总量/成本/缓存占比）与 Token 构成条（in/out/cache）都
替代不了这个判断。场景经用户拍板为**日累计成本口径**（非"正在烧哪个模型"的即时速率口径）。

## Background

- 用户 2026-10-24 发起讨论："托盘信息窗里要不要加一下模型用量？"——两轮问答定案：
  日累计口径 + 模型区块（非 Active banner 并入）。
- 信息架构偏离说明：D-015 的弹层 IA 按原型 `docs/design/tokenmetry_statusbar_prototype.html`
  锁定，本卡是**用户拍板的既有架构增量**（同 D-015 修正项先例：原型文件不动，偏离落卡留证）。
- Opinionated 立场核对：仍是固定信息架构（固定 Top3、固定排序口径、无任何自定义开关），
  不违背 2026-10-11 pivot。
- 数据零新口径：`IUsageQueryService.GetModelBreakdownAsync(今日窗口)` 与总览模型页同源
  （含 IModelNameResolver 别名归一）；行字段 `Cost`（I-028 AggregatedCost 四态）/
  `CostShare`（未知成本时服务侧即 null）全部现成。

## 口径拍板（本卡决策，偏离即改卡）

1. **排序与占比只看已知成本**：`KnownCostUsd ?? -1m` 哨兵降序（未知成本低于任何已知值
   含"已知为零"），同档按 TotalTokens 降序；占比直接吃 `CostShare`（本行含未知成本 →
   "—"/空轨道，不伪造）。已知为零（ZCode 自定义 provider 显式声明零价）≠ 未知。
2. **无数据整块隐藏**：Agent 区已有「暂无数据」诚实空态行，模型区不渲染第二处空行。
3. **行点击 → 模型统计页**（PageId.Models）：会话页无按模型筛选的既有路由，收敛到模型页；
   区头不加「查看详情」链接（Agent 区头有链接是因为其行去的是会话页，模型行本就落模型页）。
4. **Top3**：弹层压缩视图取 3（Agent 区是 Top4；总览模型页全表）。行结构复用 Agent 行
   （popover-agent-row 样式同吃 Semi 拉伸修复），右列放成本金额 + 成本占比，无副题行；
   图标槽新增 `model-chip-lg`（30×30，几何/字号对齐 agent-chip-lg，logo 22×22 同尺度），
   走 ModelVisualMap / ModelChip.Vendor 既有机制，未识别落 generic 字形兜底。

## Acceptance Criteria

- [x] 弹层 Agent 区下方、footer 上方渲染「主要模型」区，Top3 按已知成本降序（未知沉底）
- [x] 行 = model-chip-lg 品牌槽 + 模型名 + 成本占比 mini bar + 右列（金额 + 占比）；
      含未知成本行占比 "—"（I-028 四态同源：`OverviewViewModel.FormatCost/FormatShare`）
- [x] 行点击 → 模型统计页；无模型数据整块隐藏（headless 双向断言）
- [x] i18n：`PopoverTopModels` zh 源「主要模型」+ en 覆盖 "Top models"；测试语言无关
- [x] headless 回归锁 3 用例：排序/截断/未知沉底/诚实占比；渲染三行 + 空态整块隐藏；
      行命令导航 Models 页。令牌纪律测试（零硬编码色值）继续通过
- [x] 全量 `dotnet test Tokenmetry.sln` 1445 Passed（+3）

## Subtasks

- [x] `ViewModels/StatusPopoverViewModel.cs`：`StatusPopoverModelRowViewModel` +
      `TopModels`/`HasTopModels` + `OpenModelsCommand` + `ReloadAsyncAt` 增模型查询
      （序号防护同既有四路）+ Apply 排序口径
- [x] `Views/StatusPopoverView.axaml`：模型区 StackPanel（x:Name="TopModelsSection"，
      空态隐藏）+ 行模板（复用 popover-agent-row）
- [x] `Styles/SharedStyles.axaml`：`model-chip-lg` 样式组（30×30 槽 + 字形显隐并入 +
      22×22 logo 槽；厂商伪类/phi/generic 字形规则与 16×16 槽共用）
- [x] `Localization/Translations.cs`：1 键三处（zh 源 / en 覆盖 / 属性声明）
- [x] `tests/.../StatusPopoverTests.cs`：StubQueryService.Models 可注入 + Model fixture
      helper + 3 用例

## 落地记录

2026-10-24：一次收口。数据管线零改动（查询服务既有），App 层四文件 + 测试一文件。
排序测试首轮翻车记录：fixture 第五行（已知 1.10）排名高于"已知为零"行属**期望写错**
（1.10 > 0 数学正确），修正 fixture 让"已知为零 > 未知"边界留在 Top3 内验证——实现口径
无改动。全量 1445 Passed（headless 341→344）；真机明暗 × 1x/2x 目检（区块间距/模型名
截断/mini bar 观感）移交用户。

## Related

- Depends: D-015（内容层）/ D-016（壳层，高度 SizeToContent 自适应无需改动）
- Related files: `src/Tokenmetry.App/ViewModels/StatusPopoverViewModel.cs`、
  `src/Tokenmetry.App/Views/StatusPopoverView.axaml`、
  `src/Tokenmetry.App/Styles/SharedStyles.axaml`、
  `src/Tokenmetry.App/Localization/Translations.cs`、
  `tests/Tokenmetry.App.Headless.Tests/Shell/StatusPopoverTests.cs`
