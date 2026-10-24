---
id: Q-007
title: 成本分析页补缓存读/写占比卡——Token 构成四项合计满 100%
type: fix
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

用户真机反馈：成本分析页「输入成本占比 2.8% + 输出成本占比 0.6%」加不满 100%——分母 Σ TotalTokens
含缓存读/写而分子没有。把缓存读取占比、缓存写占比也单列成卡，四项分子合计 = 分母，占比拼起来
覆盖全部 Token。

## Background

- D-013 方案 b 定案时输入/输出占比只出两张卡、缓存相关「单列说明」不进卡（脚注「缓存单列」）；
  用户实际观感是「数字不自洽」。本卡把「说明」升级为「成卡」，口径本身不变（仍是 Token 构成占比
  而非方向计价拆分，脚注显式标注照旧）。
- 分母口径：total = 输入 + 输出 + 缓存读 + 缓存写（与 ccusage / facts total_tokens 一致，
  fixture 逐点验证）。缓存写 = `CacheCreationTokens`（Anthropic 口径 cache_creation）。
- 术语沿用仓库既有：缓存读取 / 缓存写（Overview KPI「缓存读取占比」、模型目录表头「缓存读取」、
  模型表单「缓存写单价」）。

## Acceptance Criteria

- [x] 四张占比卡（输入 / 输出 / 缓存读 / 缓存写）同分母 Σ TotalTokens，分子合计 = 分母；
      逐项 F1 舍入后合计可能差 ±0.2pp（脚注写「四项合计全部 Token」不写死 100.0%）
- [x] 无基数（总 token 0）时四卡均 "—"，不伪造 0%
- [x] 脚注口径标注更新：去掉过时的「（缓存单列）」括注，保留 Token 构成 + 拆价待扩展表述
- [x] headless 用例覆盖：四项占比数值、无基数 "—"、页面渲染可见性；全量测试绿

## Subtasks

- [x] `CostViewModel.CreateDirectionShares` → `CreateCompositionShares` 四元占比 + 两个新文案属性
- [x] `CostAnalysisView` KPI 行 4 卡 → 6 卡（缓存读 Neutral 芯 + hard-drive-upload / 缓存写 Neutral
      芯 + hard-drive-download，箭头方向相对盘体：读=数据出盘、写=数据入盘，与输入 ↓ / 输出 ↑ 同构）
- [x] 两个新 AppIcon 几何（scripts/generate-ui-icons.py 白名单 + lucide-static@1.61.0 生成）

## Dependencies

- D-013（页面本体；方案 b 决策沿用）

## Test Cases

### TC-001: 四项占比与全 Token 覆盖

Steps:
1. fixture 日序列（in 3700 / out 1950 / 缓存读 2200 / 缓存写 100 / tot 7950）加载成本页。

Expected:
- 输入 46.5% / 输出 24.5% / 缓存读 27.7% / 缓存写 1.3%，四项 F1 合计 100.0%；无基数时四卡 "—"。

## Development Log

### 2026-10-24

- 用户真机反馈发起：两卡占比加不满 100%，原因是分母含缓存读/写而分子没有。口径不变（Token 构成
  占比、脚注显式标注），只把缓存两项补成卡——不改方向计价拆分（那仍是方案 a 的「待扩展」，波及
  对账基线，本卡不碰）。
- 测试先行：`Cost_kpis_bind_estimated_cost_and_token_composition_shares` 先补四项断言编译 RED
  （VM 无新属性），实现后 GREEN；无基数用例补 "—" 断言；渲染可见性用例补新卡数值断言。
- 图标：新卡 icon-chip 避开 I-040 取证过的字体字形陷阱，走 lucide 管线新增 hard-drive-upload /
  hard-drive-download 两键（白名单注释声明方向语义：读=数据出盘 upload、写=数据入盘 download）。
- **并发落地注记（如实登记）**：本卡实现期间另一会话正在同工作区做 I-040 修正项 2（PathIcon 居中，
  scripts/generate-ui-icons.py 与 Icons.axaml 同文件在途改动）。本卡对其只做脚本白名单增项与
  Icons.axaml 再生成（hard-drive-upload/download 两新几何随其 ControlTheme 模板产出共存，无覆盖
  冲突）。收尾时该会话先以全量暂存把本卡四个实现文件扫入其提交，随即 amend 剔除（提交改号
  4194dec → 6e3c8e9），本卡实现回归独立提交；两个新图标几何仍随其提交落地（本卡引用的
  AppIconHardDriveUpload/Download 资源在其提交内）。
- 全量 `dotnet test Tokenmetry.sln` 1452 Passed / 0 Failed（22 跳过）。

### 2026-10-24（修正项 1）

- 用户目检截图反馈：单排六卡太挤、首卡（估算总成本）标题文字溢出卡外。修法：KPI 行改**两排三列**
  （`Grid RowDefinitions="Auto,Auto" ColumnDefinitions="*,*,*"` + RowSpacing=12，第一排 = 总成本 +
  输入/输出，第二排 = 缓存读/写 + 待确认价格），1180px 窗口卡宽 ≈179px → ≈370px。
- 几何契约测试 `Cost_kpi_cards_lay_out_in_two_rows_of_three` 锁两排三列（Y 两簇 / 每行 3 卡 /
  行内三列 / 第二排在下 / 卡宽 ≥240 防回归单排）。
- 留档截图 cost-light/dark.png 重生成（time 两张随同一测试顺带刷新，内容为当前已提交 UI 态）。
  踩坑：D-013 记录的 `TOKENMETRY_TIMECOST_PREVIEW_DIR=docs/design` 相对路径落在 testhost bin
  目录（testhost cwd ≠ 仓库根），须用绝对路径。
- headless TimeCostPagesTests 17/17；全量见 Verification。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/ViewModels/CostViewModel.cs`, `src/Tokenmetry.App/Views/CostAnalysisView.axaml`, `src/Tokenmetry.App/Localization/Translations.cs`, `tests/Tokenmetry.App.Headless.Tests/Shell/TimeCostPagesTests.cs`（图标几何 `scripts/generate-ui-icons.py` + `src/Tokenmetry.App/Styles/Icons.axaml` 随 6e3c8e9 / I-040 修正项 2 落地）
- How to run/verify: `dotnet test tests/Tokenmetry.App.Headless.Tests --filter FullyQualifiedName~TimeCostPagesTests`；真机看成本分析页四张占比卡合计与两排观感。
- Results: 2026-10-24 headless TimeCostPagesTests 17/17 通过（含两排三列几何契约）；全量 1454 Passed / 0 Failed（22 跳过）；留档截图 cost-light/dark.png 已重生成，明暗目检移交用户。
