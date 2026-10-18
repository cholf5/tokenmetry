# D-017 右栏撤除 + 顶栏四维全局筛选 + 会话行元数据升级

**Status**: Todo
**Priority**: P1
**Date**: 2026-10-18
**Design**: docs/plans/2026-10-18-right-rail-removal-design.md（已评审定案）
**Plan**: docs/plans/2026-10-18-right-rail-removal.md（7 任务，inline 执行）

## 背景

右侧固定栏（276px）承载「最近会话」与「快速筛选」，定位失败：最近会话行以 SessionId 当标题（无信息量、点击仅 toast 占位）；快速筛选是会话页级筛选却放在全局外壳里（作用域与位置错配，会话未打开时"应用筛选"只弹 toast）；顶栏全局筛选缺 Model / Project 维度。右栏整体撤除、空间归还主内容，筛选语义收敛成一套。

## 四项定案（决策记录，见设计文档 §0）

1. **右栏整体撤除**——不改实时活动面板/上下文感知右栏；外壳 `.shell` 184,*,276 → 184,*。
2. **筛选全部并入顶栏**——时间 + Agent + Model + Project 四维全局（`UsageQueryFilter.Model/Project` 新维度，`FilterStateService` 状态 + `GlobalFilterMapper` 映射 + `AgentCatalogService` 三份选项装载）；Sessions 页只留搜索 + 排序。否决顶栏+页级两层并存。
3. **最近会话直接删掉**——Sessions 页默认排序即最近活动，本就冗余；托盘弹层 footer「最近会话 →」保留跳转入口。
4. **会话行元数据升级**——项目展示名升主文本（空则原始路径，再空则会话标识），SessionId 降副文本 + tooltip 可复制；**不引入会话标题**（隐私红线 §3.1）。

## 实施要点

- Core 查询单点扩展：`GetScopedUsageFactsAsync` / `GetScopedSessionsAsync` 谓词 + KPI 环比窗口继承；模型过滤走别名归一规范名空间（与模型维度聚合同口径）；单维分解行不携带其他维度的粒度语义与既有 Agent 过滤一致。表结构不动、不加迁移。
- `OpenSessionsForProject`（D-011 项目行点击）改写全局 Project 筛选：顶栏可见、「全部项目」可复位；目标项目不在选项清单时如实补入临时选项（无界超集契约同 `OpenSessionsForAgent`）。
- 删除：`SessionsPanelViewModel`（整文件）、`RecentSessionRowViewModel`、`AgentQuickFilterViewModel`、DI 注册、`QuickFiltersCommitted` 事件、Sessions 页 scope/SubsetMerge 合并机制、右栏专属 i18n key 9 个、`MainWindow.axaml` 右栏块与 `session-list` 样式。

## Dev Log（执行取舍）

- **临时项目选项永久补入**：跳转写入的 Project 若不在启动期快照中，补入选项常驻到下次选项刷新（不实现"下拉关闭时清临时项"）——与既有 `OpenSessionsForAgent` 的永久补入先例一致且更简单；选项清单本就是启动期快照（陈旧一个会话周期是 D-008 既定取舍）。
- **会话标题留后续卡**：facts 无标题字段是隐私红线（不存 prompt/会话内容），元数据升级是红线内的诚实改进；"会话在干嘛"的进一步信息量（如首条用户消息派生标题）越线，独立卡片再议。
- **FilterStateService 的 Model/Project 不持久化**：与 Agent 同理由——选项清单数据驱动，持久化选中值会在清单收缩时制造静默空结果。
- 计划执行裁定记录：`ReplaceOptions<T>(ref …)` 泛型抽取不可编译（CS1628 ref 捕获）→ `RunOnUi(Action)` + 显式字段闭包；`LabelProjectDir` 计划误列右栏专属，实为 Projects/Overview 表头共用 → 保留；项目过滤 KPI 测试期望 5350→5500（种子含 unpriced 行）；setter 依赖用例按仓库约束放 Headless `[AvaloniaFact]`。

## 验证

- 全量 `dotnet test Tokenmetry.sln`：**1359 Passed / 0 Failed**（Core 161 / Reconciliation 49+19 skip / App 158 / Infrastructure 657+3 skip / Headless 312）。
- 删除右栏用例（Top6/复选集/SubsetMerge/Panel 提交点/RecentTopN 竞态）、新增全局四维映射与驱动重查、项目行点击写全局筛选（含快照缺项补入）、两栏外壳固化（`SessionsPanelBorder` 不得存在）、会话行元数据映射、行虚拟化改走全集分页。
- Reconciliation.Tests 全量跑确认口径未动（golden JSON 不含筛选场景）。
- **真机目检移交用户**：960×640 最小窗口顶栏四维不换行不溢出（溢出按设计 §1 降级隐藏筛选标签文字）；明暗两主题顶栏新下拉观感；项目页行点击 → 落 Sessions 页且顶栏 Project 已选中、「全部项目」可复位。

## 明确不做（YAGNI 留档）

会话标题字段（隐私红线，独立卡再议）、Agent 多选查询、右栏任何形式复活、响应式顶栏布局系统、FilterStateService 的 Model/Project 持久化。
