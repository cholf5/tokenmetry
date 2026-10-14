---
id: D-011
title: 项目统计页
type: feature
priority: P2
size: S
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

项目维度统计页：项目/目录、会话数、总 Token、估算成本、占比、平均每会话六列表；行点击跳会话明细页并带项目筛选。

## Background

- 设计基准：原型 `#page-projects`；「路径归一化后的项目名」来自 I-029 的项目维度聚合。
- 归一化歧义守卫对齐 I-025 思路：歧义时显示原始路径，不猜测合并。

## Acceptance Criteria

- [x] 六列表接 I-029 项目维度，占比与均值计算正确
- [x] 行点击跳会话页且项目筛选生效
- [x] 归一化歧义项目显示原始路径（或可展开原始路径）
- [x] 未知成本显示「未知」；明暗两套目检

## Subtasks

- [x] ProjectsViewModel + 排行表
- [x] 跳转联动与歧义路径展示

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 跳转联动

Steps:
1. 点击某项目行。

Expected:
- 会话页列表只剩该项目会话。

## Development Log

### 2026-10-12

- 创建任务卡（v0.4 UI 原型落地批次），尚未开始实现。

### 2026-10-13

- 实现落地：ProjectsViewModel（六列行 VM）+ ProjectsView，外壳真实化（注入 `Action<string, string?>` 跳转回调）。歧义守卫项目如实显示原始路径：I-029 已在服务端守卫（DisplayName 退回原始路径），页面 `DisplayName ?? Key` 直显 + ToolTip 恒为原始路径——无需页面侧再守卫。
- **决策点 2（行点击跳转途径，D-009/D-011 共用定案）：项目维度走页级途径**——FilterStateService 无项目维度，唯一已审定宿主是右栏快速筛选的项目下拉（D-008 会话页级筛选状态持有者）。新增 `SessionsPanelViewModel.ApplyExternalProjectFilter(path, label)`：程序化立即应用（不走暂存，跳转即生效），选项清单缺该路径时如实追加（选项是启动期全集缓存，后台刷新后的新项目可能不在其中，静默丢弃会让跳转失效），随后触发 QuickFiltersCommitted；外壳先写状态后导航（会话页首查即带筛选）。落地后右栏下拉可见、「清除」可复位——无隐藏过滤态（诚实原则）。**不升格独立 SessionQuickFilterService**：D-008 刚把"暂存/提交"语义落在 SessionsPanelViewModel，迁移是纯结构重构无行为收益，本卡取侵入最小的方案；手动改下拉的暂存语义不变。
- 均值 = TotalTokens / SessionCount（AwayFromZero 取整后紧凑格式；除零保护：零会话项目（纯 CLI 源）显示 "—"，不伪造 0）。
- token / 均值列用紧凑格式 FormatCompactTokens（千分位全数字在定宽列会被裁切成错值，宁紧凑不裁切，D-009/D-010 同款裁定，与原型 fmt() 为显式偏差）。
- 遗留风险：跳转写入的页级项目筛选是持久状态（右栏可见），用户不点「清除」时会话页持续按其过滤——与 D-008 快速筛选语义一致；与全局单选 Agent 叠加时按交集语义（两者都可见）。

### 2026-10-13（审查修复轮）

- **P2（随 D-009 修复轮，注释级）**：选项补入的竞态安全不变量落成注释——ApplyExternalProjectFilter 对"快照缺路径"的判重补入，与 D-009 的 Agent 补入同理，依赖 AgentCatalogService 项目目录恒为无界严格超集 + ProjectFilterOption record 值相等重绑才保持幂等（AgentCatalogService 类型注释已锁定"无界超集语义是契约的一部分，勿收窄"）。行为无变化。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/ViewModels/ProjectsViewModel.cs`、`src/TokenPulse.App/Views/ProjectsView.axaml(.cs)`、`src/TokenPulse.App/ViewModels/SessionsPanelViewModel.cs`（ApplyExternalProjectFilter）、`src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`（OpenSessionsForProject）、`tests/TokenPulse.App.Headless.Tests/Shell/DimensionPagesTests.cs`
- How to run/verify: `dotnet test tests/TokenPulse.App.Headless.Tests/TokenPulse.App.Headless.Tests.csproj`（DimensionPagesTests）+ 明暗截图目检
- Results: 2026-10-13（审查修复轮后）全套件 987 通过 / 22 跳过 / 0 失败 / 0 警告。TC-001 端到端通过（`Project_row_click_navigates_shell_to_filtered_sessions_and_clears`：行点击 → 右栏项目筛选程序化应用 → 会话页首查只剩该项目会话 → 「清除」复位重查）；占比 / 均值 / 除零 "—" / 歧义守卫原始路径直显 / 未知成本「未知」/ 选项缺路径如实追加 / 序号防护 / Dispose 退订各有用例。截图 `docs/design/projects-light.png`、`docs/design/projects-dark.png`（真实数据 7 个项目，均值列 11.41M … 258.6K 可见）。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：三卡组合审查结论"有问题需修复"——1×P1 + 3×P2；契约演进推导、对账三分支手算、两条跨页途径端到端、TC-001 合计口径、数字裁切修复、三页模式合规全部核实为真。修复（原开发 agent 完成，PM 验收）：
1. **P1 Agent 跳转死胡同**（目录缓存不刷新时新 Agent 只补全局选项、右栏复选组快照不含它 → 会话页 Empty 硬 0/0 且「清除」不可恢复）：OpenSessionsForAgent 补全局选项时同步补入已勾选的右栏复选项（复选组非空才补，空组走既有 Single 兜底），死胡同解除且状态对用户可见可清除；新增死胡同变体端到端回归。
2. P2 tie-break 对账缺区分度（键序数胜者恰巧 token 更多）→ 交换 fixture token 量级使断言 discriminating。
3. P2 目录无界超集不变量落成双向注释锁定（防后人收窄目录查询）。
4. P3 DominantAgent XML doc 补第三种 null 来源（会话无有效 Agent 归属）。
PM 验收：build 0 警告 + 全量 987 通过 / 22 跳过 / 0 失败。
