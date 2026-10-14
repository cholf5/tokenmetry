---
id: D-008
title: 会话明细页 + 右栏最近会话联动
type: feature
priority: P1
size: M
status: done
created: 2026-10-12
updated: 2026-10-13
blocked: none
---

## Goal

会话明细页：搜索框 + Agent 筛选 + 排序下拉，明细表（会话/Agent/模型/Token/估算成本/最后活动），行数虚拟化；右栏「最近会话」与快速筛选接真实数据，并与页面及全局筛选联动。

## Background

- 设计基准：原型 `#page-sessions` 与右栏 `.sessions-panel`。
- 吸收 v0.4 原规划的「Reports Session tab」；Monthly 维度不进本卡（记入 Parking Lot）。
- 会话点击行为：原型是 toast 占位；正式版打开会话详情（下钻弹窗或详情页，实现时定案记录）。

## Acceptance Criteria

- [x] 搜索 + Agent 筛选 + 排序全接 I-029，组合筛选结果正确（stub 查询参数逐项对账 + 真实服务冒烟同口径直查对账）
- [x] 大列表滚动流畅（≥1 万行 fixture 虚拟化验证：realized container 数 1..200 ≪ 6,667 行全集；真机滚动帧率属人工目检项，留人工——自动化只能断言虚拟化生效，无法断言帧率）
- [x] 右栏最近会话、Agent 复选组、模型/项目下拉与全局筛选联动生效（联动语义见 Dev Log 定案：最近会话随全局筛选重查；复选组/下拉为会话页级筛选与全局取交集）
- [x] 未知成本显示「未知」；计数条（显示 X / Y 条）正确（CostUsd=null 不折 0；X=已加载、Y=过滤后总数）
- [x] 明暗两套目检通过（自动化明暗截图留档 docs/design/sessions-{light,dark}.png 含右栏，headless 真实 App.axaml 渲染；审查修复轮 P1 后重生成并逐张目检：Agent 软色底芯片与字形明暗两套均正确呈现——首轮截图芯片缺失的漏检已在 P1 记录）

## Subtasks

- [x] SessionsViewModel（搜索防抖 + 筛选 + 排序）
- [x] 明细表 DataGrid 行模板 + 虚拟化（定案用 ListBox 默认虚拟化面板 + 行模板，见 Dev Log）
- [x] 右栏最近会话 + 快速筛选接线
- [x] 会话点击下钻方案定案与实现

## Dependencies

- D-003, I-029

## Test Cases

### TC-001: 组合筛选

Steps:
1. 构造 ≥1 万行 fixture 会话。
2. 组合搜索词 + Agent + 排序各维度操作。

Expected:
- 结果与查询服务单测口径一致；滚动帧率无可感知卡顿。

Result: 自动化部分通过（SessionsTests，见 Verification）；"帧率无可感知卡顿"留人工真机目检。

## Development Log

### 2026-10-13（审查修复轮）

独立审查核实全绿后提出 1 P1 + 3 P2 + 1 Nit，全部修复（headless 134/134，全量 962 通过 0 失败 0 警告）：

- **P1（Agent 芯片不渲染）**：根因一句话——`AgentVisualMap` 用 `ThemeVariant.Default` 显式查询 `AppIconChip*` 令牌，而这些键只在 Tokens.axaml 的 Light/Dark 主题字典（无 Default 兜底），TryGetResource 软失败返回 null → 明细表与右栏的 Agent 软色底图标 + 字形在明暗两套全部不渲染（首轮截图可证，当时目检未看出的失误）。修复选型：**XAML DynamicResource + 变体伪类**（`Controls/AgentChip` 附加属性把行 VM 的语义变体名翻译成 `chip-blue/green/purple/neutral` 伪类，颜色由 SharedStyles 四组 chip-* 样式 DynamicResource 引用令牌）——不用 VM 侧 ActualThemeVariant 解析 + 主题切换重建行的方案，理由：行 VM 不再持画刷（数据/视图职责更清），窗口级 `RequestedThemeVariant`（明暗截图正用的机制）也能正确呈现，主题翻转零成本，无需行重建/重查。已重生成截图并逐张目检：明暗两套芯片与字形均出现且用对应主题色。
- **P2-1（启动竞态假空态）**：全局已选 Agent 而 AgentCatalogService 未返回（复选组未加载）时，`ResolveAgentScope` 对空复选集取交集得 Empty → 假"没有匹配的会话"且选项到位后无重查。修复：该分支改走 `Single(全局)` 兜底（与"复选组未加载 → 不过滤"同一原则）；选项就绪后不强制重查（全选 ∩ 全局单选 = 全局单选，页面数据本就与交集语义一致）。新增 `Global_agent_with_unloaded_catalog_falls_back_to_global_single` 用例（挂起目录查询验证首查参数与就绪后的状态）；顺带发现并修掉 stub 的记账缺陷（挂起任务消费后放行引用丢失 → Release 空转）。
- **P2-2（加载失败三重矛盾态）**：原 catch 清空 Rows/TotalCount → 错误面板 + "没有匹配的会话" + "显示 0 / 0 条"同时出现。修复：失败只置错误态、**保留已加载行与总数**（与 LoadMore 追加失败不清数据纪律对齐）；空态改 `ShowEmptyState`（无行且无错误才显示）、计数条/加载更多改 `ShowCountLine`（失败隐藏，重试成功恢复）。新增 `Load_failure_keeps_last_good_rows_and_hides_count_and_empty_state` 用例（含恢复路径）。
- **P2-3**：`MainWindowViewModel.LoadAgentOptionsAsync` 失败分支补最小诊断日志（stderr 一行，含失败原因与"保持所有 Agent"说明），不再全静默。
- **Nit**：`Assert.Equal(SearchDebounceMilliseconds, 300)` 恒真无回归力 → 改 `Assert.InRange(100, 1000)` 区间护栏（300ms 是产品口径记于 Dev Log，测试防 0/负值与秒级离谱值）。

### 2026-10-13

实现完成（headless 132/132 通过，全仓 0 警告）。定案与机制记录：

- **右栏快速筛选 vs 全局筛选关系（定案，采用任务卡方向 a)**：全局筛选（顶部栏时间范围 + 单选 Agent）= 跨页上下文——右栏「最近会话」列表只随全局筛选重查（最近活动降序 Top6，对齐原型 `slice(0,6)`，原型 `.filterbox` 同样不影响 `#recentSessions` 渲染）；Agent 复选组/模型/项目下拉 = **会话页级**快捷筛选，只驱动会话明细页查询，与全局筛选取交集。未选方向 b)（FilterStateService 多 Agent 集）：I-029 `UsageQueryFilter.Agent` 是单值，扩集需变更已审定的查询契约并波及 Overview/后续页，不在本卡动。
  - 复选集 → 查询映射：全勾 → `Agent=null`（不过滤）；恰勾 1 个 → 该 Agent（走 I-029 服务分页）；真子集（勾 2..N-1 个）→ **逐已选 Agent 查询（PageSize=null）+ VM 合并 + 与服务同口径排序（未知成本视为最小值 + SessionId 平局键）+ 客户端按页大小 50 分页**——单值过滤无法一次表达并集，多查询合并不改契约；TotalCount = 各 Agent 之和，计数条如实。复选全不勾、或与全局单选 Agent 无交集 → 诚实空态、不发查询。取舍：子集模式的「加载更多」走客户端切片而非服务分页（数据量受 facts 内存全集上界约束，可接受，已测试覆盖）。
  - 复选/下拉改动为**暂存**（不触发重查），「应用筛选」「清除」是提交点（触发会话页重查；页面未打开时仅 toast 反馈）；「清除」复位全勾 + 全部模型/项目并提交。文案对齐原型 toast。
- **下钻方案定案（最小诚实）**：明细表行点击（ListBox SelectedItem）与右栏最近会话行点击 → toast 显示会话标识 + "详情视图规划中"，选中态立即复位（同一行可再次点击）。理由：原型即 toast 占位；本卡不为下钻发明弹窗/详情页新功能面，正式下钻留待后续卡。
- **Agent 清单接线（单一真相源 + 服务层缓存）**：新增 `AgentCatalogService`（I-029 `GetAgentBreakdownAsync/GetModelBreakdownAsync/GetProjectBreakdownAsync` 无界查询 + 信号量护栏 + 应用生命周期缓存）——全局 Agent 下拉（外壳构造时异步拉取写入 `FilterStateService.UpdateAgentOptions`，`PropertyChanged(AgentOptions)` 刷新顶部栏）与右栏复选组/模型/项目下拉共用同一份缓存，导航不重拉。初始仅 [所有 Agent]（诚实：不冒充占位清单），加载失败/无数据保持该值，下次启动重试。占位清单 `AgentOption.Options` 删除，运行时选项全部来自真实维度查询。注意：`UpdateAgentOptions` 显式给属性名（`SetProperty` 的 CallerMemberName 在方法内会解析成方法名，坑已踩）。
- **搜索防抖（为什么 + 怎么实现）**：每次击键重启 300ms `DispatcherTimer`，停顿才重查——每击键全量过滤+排序代价随会话总数线性增长（facts 全在内存，万行可感），且快速输入的中间态结果必然被下一击覆盖，重查纯浪费。headless 不驱动 DispatcherTimer 时钟，测试经 `CommitSearchForTests()` 直调提交（任务卡允许"虚拟时间或直接调命令"），断言"连打三次仅一次查询且以最终词为准"。搜索 placeholder 诚实：facts 无标题字段（I-002/I-029），命中字段为会话标识/项目/模型/Agent，不写"标题"。
- **明细表虚拟化方案**：选 ListBox（默认 VirtualizingStackPanel）而非 DataGrid——行模板要复刻原型 `.sessiontable` 观感（会话+项目两行、Agent 软色底图标、table-* 令牌列），DataGrid 列模板定制成本更高；页面根 Grid 用 `*` 行约束视口。1 万行 fixture（测试内代码合成，不进 git）：真子集合并模式客户端持有 6,667 行全集，断言 realized container ∈ [1,200] ≪ 6,667（另覆盖子集模式的「加载更多」客户端分页路径）。真机滚动流畅性留人工。列宽用混合星宽（会话 2* / 模型 1.2*，数字列定宽），最小窗口（主列 456px）不挤出最后一列。
- **页面 VM 生命周期/线程纪律照抄 D-006/D-007**：每次导航新建、外壳 Dispose 退订（全局筛选 + 右栏提交事件 + 防抖定时器；Dispose 后封禁一切重查路径，防定时器被后续输入重启的回归）；加载序号防护（挂起的旧响应/异常不覆盖新选择）；`TimeRange→本地日窗口`映射抽到 `GlobalFilterMapper`（Overview 同口径复用，映射断言两页共享语义）。
- **测试**：新增 `SessionsTests`（23 用例）：组合筛选→`SessionListQuery` 参数逐项对账、分页/加载更多/计数条、防抖、排序映射、复选集三形态（null/单值/子集合并）、全局∩复选交集空态、未知成本「未知」、万行虚拟化 realized-count、序号防护、Dispose、Agent 目录（注入/缓存/空/失败）、右栏 Top6 随全局筛选、提交点、双下钻 toast、相对时间、真实服务冒烟、明暗截图留档。stub 最小复刻服务口径只承担数据回传（服务行为对账在 UsageQueryServiceTests）。既有 AppShellTests/OverviewTests/OverviewBottomTests 随真实页替换占位页与真实清单同步更新。
- **观察到既有无关 flake（记录，非本卡引入）**：全量 `dotnet test` 间歇出现 `CopilotSessionAdapterTests` 用例失败（本卡期间实锤 `Discovers_session_state_files_without_nested_session_files`：`FilesDiscovered` 期望 1 实得 2）。根因高概率为 **`CopilotOtelMergeTests` 以 `Environment.SetEnvironmentVariable` 改进程级 `COPILOT_OTEL_FILE_EXPORTER_PATH`，与并行运行的 `CopilotSessionAdapterTests` 竞态**——locator 对该环境变量"指向既有文件即纳入发现"，跨测试类进程环境无隔离。Infrastructure.Tests 不引用本卡改动的 App 工程，与 D-008 无关；证据已追加到 B-004 Dev Log（该卡已建在追同一 flaky 家族）。
- 遗留风险：①子集合并模式在超大子集下客户端持有全量行（内存与万行级渲染压力，虚拟化已覆盖但真机帧率未测）；②Agent/模型/项目选项清单服务层缓存整个应用生命周期，后台刷新后新 Agent 要下次启动才进下拉（取舍已文档化）；③最近会话/明细行标题位用 SessionId，等标题数据源引入（契约已预留 `SessionListQuery.SearchTerm` 扩展点）后可替换。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/TokenPulse.App/Views/SessionsView.axaml(.cs)`、`ViewModels/SessionsViewModel.cs`、`ViewModels/SessionsPanelViewModel.cs`、`ViewModels/AgentVisualMap.cs`、`Controls/AgentChip.cs`、`Services/AgentCatalogService.cs`、`Services/GlobalFilterMapper.cs`、`Services/FilterStateService.cs`、`Styles/SharedStyles.axaml`（chip-* 四变体）、`ViewModels/MainWindowViewModel.cs`、`Views/MainWindow.axaml`、`DependencyInjection/AppServices.cs`、`tests/TokenPulse.App.Headless.Tests/Shell/SessionsTests.cs`
- How to run/verify: `dotnet test tests/TokenPulse.App.Headless.Tests`；截图留档 `TOKENPULSE_SESSIONS_PREVIEW_DIR=docs/design dotnet test --filter Sessions_page_renders_light_and_dark`；真机 `dotnet run --project src/TokenPulse.App` 目检（含明暗切换、最小窗口、行点击 toast）
- Results: 2026-10-13（实现 + 审查修复轮）— headless 134/134 通过（基线 109 + D-008 新增 25，0 失败 0 警告）；全量 `dotnet test TokenPulse.sln` 962 通过 / 0 失败（Core 99 / Infra 637 / App 43 / Reconciliation 49 / Headless 134，0 警告）。明暗截图 docs/design/sessions-light.png、sessions-dark.png（P1 修复后重生成，含右栏真实数据，芯片/字形两套主题均正确）。TC-001 自动化部分通过；真机滚动流畅性留人工目检。

**独立审查轮（2026-10-13，PM 派独立审查 agent）**：结论"有问题需修复"——1×P1 + 3×P2；核心逻辑全部核实为真（真子集合并排序与 I-029 服务端逐分支一致、AgentCatalogService 双检无死锁、防抖 dispose 全路径、虚拟化断言非虚拟化必红、契约零绕过、B-004 根因证据行号逐一对上且机制成立、3 次全量无 flake 佐证"与本卡无关"）。修复（由原开发 agent 完成，PM 验收）：
1. **P1 Agent 芯片四处静默不渲染**：AgentVisualMap 以 ThemeVariant.Default 显式查询仅存在于 Light/Dark 主题字典的键，TryGetResource 软失败返回 null（DynamicResource 处正常，fail-soft 掩盖）。修复选型：XAML DynamicResource + AgentChip 附加属性翻译 chip-* 伪类（SharedStyles 四组样式），行 VM 不再持画刷、明暗自动翻转零重建；AgentVisualMap 收敛为字形 + 变体名。PM 逐张复核重生成截图：明暗两套芯片与字形均正常呈现。
2. **P2 启动竞态假空态**：全局已选 Agent 而目录未返回时 ResolveAgentScope 误得 Empty → 假"没有匹配的会话"。改 Single(全局) 兜底 + 挂起目录查询用例；顺带修 stub 记账缺陷。
3. **P2 加载失败语义**：失败不再清已加载行/总数、隐藏计数条与空态（新增 ShowEmptyState/ShowCountLine），含失败保留 + 重试恢复用例。
4. **P2 目录拉取失败补 stderr 诊断**。
5. Nit：防抖常量恒真断言改 InRange(100,1000) 区间护栏。
PM 验收：build 0 警告 + 全量 962 通过 / 22 跳过 / 0 失败，明暗截图亲核。
