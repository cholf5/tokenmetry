# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

> 2026-10-11 迁移说明：按 markdown-kanban 规程，旧 7 列看板收敛为 Todo | Done。原 In Review 的 7 张 v0.1/v0.2 卡片经 git 记录核实（功能均已提交、卡片内有测试通过记录）后记为 Done，卡片文件移入 `issues/archive/`（保留旧格式）。产品定位转向见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`。

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| D-014 | macOS 状态栏直显今日成本（模板图标→动态成本位图，不走 NSStatusItem interop） | P3 | M | none | none |

> 2026-10-17：**S-005 关窗行为收口（用户提案直接落地）+ D-014 建卡**。A-0「关窗即托盘」补齐两缺口：设置页「关闭窗口时最小化到系统托盘」开关（默认开，关闭后点 X 即经生命周期退出）+ 首次关窗一次性模态提示窗（in-app toast 在主窗隐藏后无处渲染、Avalonia 无跨平台系统通知 API——balloon 需 Win32 内部句柄、macOS 通知需 interop+权限流程，模态窗是零 interop 且关窗时点告知的方案）。Mac 状态栏直显成本（tooltip 在 Mac 渲染不出，成本唯一出口是菜单首行）立 D-014 单独处理；OS 级 balloon/toast 有意不做（不成比例，待真系统通知需求成卡）。

> 2026-10-17：**B-007 / Q-003 收口，Todo 清空**（B-006 遗留双卡一次清账）。Heatmap/DonutChart 挂 `CurrentLanguage` 重译订阅（挂树/脱树对称）→ Rebuild，切语言后 tooltip（单位口径 / "无数据" / 空态副题）即时换语言；DonutChartTests 补 chart-tip 类 + 紧凑文案断言，独立审查复现反向验证锁有效。有意不做：TrendChart 图内 tooltip/轴刻度恒 ASCII（I-032 锁定决策）、非空态 `CenterSubtitle`（属主注入 StyledProperty），留待属主页卡处理。

> 2026-10-16：**B-006 收口（d268368）后的 code review 建 P3 小卡两张**。B-007：Donut/Heatmap 的 Avalonia 弹层 tooltip 是 build-time 固化字符串且控件未订阅 `CurrentLanguage` 重译信号（切语言后陈旧至下次数据重建；热力图"无数据"固化是 B-006 前旧账一并纳入）；Q-003：DonutChartTests 零 tooltip 断言，共享 helper/样式回归时环形图侧静默绿。均为 S 级独立可收。

> 2026-10-16：**S-004 数据与计价页拆分收口**（用户决策）。S-002 的合并页「数据源与设置」沿"影响数字的口径 vs 应用偏好"拆为「数据与计价」（数据源 / 口径 / 模型目录 / 别名映射）与「设置」（主题 / 语言 / 刷新 / 通知 / 路径）两个页签，侧栏 8→9 项；行为语义零变化，P0 重扫回归锁随迁。pivot 文档 §5 同日追记。

> 2026-10-15：**模型目录批次 I-035/I-036/I-037/S-003/I-038 同日收口**。起点是用户提出的两个问题：缺价模型没有地方设置价格（`PricingOverrides` 当时没有任何 UI 出口），以及 token 聚合的模型映射"不干净"（只影响统计、关联不上价格）。方案见 `docs/plans/2026-10-15-model-registry-design.md`。实施中有一处方案变更：原定"计价挪到查询期 + 拆 cost 来源"经核实不必要——增量扫描器每轮返回全量记录，改价触发一轮刷新即可全量重算，故按更便宜的方案落地（见 I-037 Dev Log）。遗留三项已登记：模型维度按 provider 拆分（需 session_facts 加列）、按 provider 的差异价、Pi/DSH 解析期定价的陈旧边界。

> 2026-10-12：按用户决定一次性建 UI 原型落地批次卡 I-026～I-029 / D-003～D-013 / S-002 / Q-002（17 张）。设计基准 `docs/design/ccusage_analytics_prototype.html`（同日自 ~/Downloads 收入仓库）；选型 Semi.Avalonia（依据见 I-026 Background）。本批是 v0.4 主体的全景规划，执行按依赖顺序逐卡进行：I-026 → I-027 → D-003，I-028/I-029 与主题线并行，随后 D-004/D-005 → D-006 → 其余页面 → Q-002。与「看板保持 5–10 张」的常规策略有意偏离，理由同 2026-10-11 适配器批次。

> 2026-10-11：按用户决定一次性建多源覆盖批次卡 I-006～I-022。蓝本为 ../ccusage/rust/adapters/（Rust 移植版，18 源清单见其 README「Supported Sources」）；DSH 不在 ccusage 支持列表（核实过，README 的 "trendshift" 含 "dsh" 是误匹配），I-007 为自研。与「看板保持 5–10 张」的常规策略有意偏离——本批是一次性全景规划，执行仍按优先级逐卡进行。**2026-10-12 追记：I-006～I-022 十七源适配器全部完成**（对账口径见各卡 TC），多源覆盖批次收口；I-024 候选计价接线随九源收口（UnpricedModels 欠账消除，快照补 tiers 段并刷新 kimi-k2.6）；**I-025 同日收口**：快照 7 条目经 CLI 探针核实已逐位等于 npm 20.0.29 锚点（I-024 审查清单对的是 rust HEAD、与锚点漂移，照单改价会炸对账——详见该卡 B-001），不改价，归一化歧义守卫 + 快照 _provenance 溯源落地。**Todo 清空。**

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
| S-005 | 关窗行为开关（关窗=托盘可关）+ 首次关窗托盘一次性提示 | P2 | 2026-10-17 |
| B-007 | 语言切换后图表 tooltip 文案陈旧（Heatmap/DonutChart 缺 CurrentLanguage 重译订阅） | P3 | 2026-10-17 |
| Q-003 | 图表 tooltip 测试对称补齐（DonutChart chart-tip/紧凑文案零覆盖） | P3 | 2026-10-17 |
| B-006 | 图表 hover tooltip 三缺陷（透明无样式气泡 / 配色不随主题 / token 数紧凑单位） | P1 | 2026-10-16 |
| S-004 | 数据与计价页拆分（数据源/口径/目录/别名 ↔ 应用偏好分离，侧栏 8→9 项） | P2 | 2026-10-16 |
| I-035 | 模型目录（ModelId 单一身份）+ 计价接线 | P1 | 2026-10-15 |
| I-036 | provider 三元组（agent × model × provider）落库与查询 | P1 | 2026-10-15 |
| I-037 | 目录驱动的重算（改价格即生效，不做查询期计价） | P1 | 2026-10-15 |
| S-003 | 设置页模型目录（价格 / 厂商 / 图标唯一入口 + 关联） | P1 | 2026-10-15 |
| I-038 | 目录迁移与对账护栏（旧字段兼容 + 信任锚边界） | P2 | 2026-10-15 |
| B-005 | 首启全窗冻结（采集扫描内联 UI 线程）+ 初始化菊花转动不匀 | P1 | 2026-10-16 |
| I-034 | Agent 品牌 logo 接入（ZCode/DSH 等 16 个 Agent 行内官方标 + 未覆盖者散列字形兜底） | P2 | 2026-10-15 |
| I-033 | 模型厂商 logo 接入 lobe-icons（官方彩标行内直放 + 单色标明暗翻转） | P2 | 2026-10-15 |
| I-032 | 趋势图视觉升级（单调平滑曲线 + Web 风 chrome）+ 只读交互（禁用缩放/平移） | P2 | 2026-10-15 |
| I-031 | 首次启动数据初始化（启动即刷可见反馈 + 全窗口初始化遮罩） | P2 | 2026-10-15 |
| I-030 | 模型聚合（别名映射）+ 内置模型图标 | P1 | 2026-10-15 |
| B-004 | flaky：CopilotSessionAdapterTests 间歇多计 LinesSkipped | P2 | 2026-10-13 |
| Q-002 | 旧视图清理与视觉验收打磨 | P2 | 2026-10-13 |
| S-002 | 数据源与设置页重构（原型版） | P2 | 2026-10-13 |
| D-013 | 成本分析页（计费口径透明） | P2 | 2026-10-13 |
| D-012 | 时间分析页（小时分布 + 活跃日历热力图） | P2 | 2026-10-13 |
| D-011 | 项目统计页 | P2 | 2026-10-13 |
| D-010 | 模型统计页（价格缺失显示未知口径样板） | P2 | 2026-10-13 |
| D-009 | Agent 统计页 | P2 | 2026-10-13 |
| D-008 | 会话明细页 + 右栏最近会话联动 | P1 | 2026-10-13 |
| D-007 | 总览页（下半）：模型分布 + Token 构成 + 项目排行 + 成本趋势 | P2 | 2026-10-13 |
| D-006 | 总览页（上半）：KPI 行 + 使用趋势 + Agent 占比环形 | P1 | 2026-10-13 |
| D-005 | 自绘环形图与热力图控件 | P1 | 2026-10-13 |
| D-004 | ScottPlot 趋势图正式集成（堆叠柱 + 折线 / hover helper） | P1 | 2026-10-13 |
| I-029 | UI 聚合查询服务 B：维度聚合 + 会话明细 + 小时分布 | P1 | 2026-10-13 |
| I-028 | UI 聚合查询服务 A：日趋势 + KPI + 区间对比 | P1 | 2026-10-13 |
| D-003 | 三栏应用外壳 + 导航 + 全局筛选状态 | P1 | 2026-10-13 |
| I-027 | 设计令牌与共享样式（原型色板 / 卡片 / 系列色 / mini 进度条 / toast） | P1 | 2026-10-13 |
| I-026 | Semi.Avalonia 主题迁移（含 Avalonia 12.1.x 升级） | P1 | 2026-10-13 |
| I-025 | 定价快照数据版本刷新 | P3 | 2026-10-12 |
| I-023 | Copilot CLI OpenTelemetry 合并与积分对账 | P3 | 2026-10-12 |
| I-024 | 候选计价端到端接线 | P2 | 2026-10-12 |
| I-021 | Antigravity 适配器 | P3 | 2026-10-12 |
| I-020 | OpenClaw 适配器 | P3 | 2026-10-12 |
| I-022 | Grok Build CLI 适配器 | P3 | 2026-10-12 |
| I-019 | pi-agent 适配器 | P3 | 2026-10-12 |
| I-018 | Codebuff 适配器 | P3 | 2026-10-12 |
| I-013 | Kimi Code 适配器 | P3 | 2026-10-12 |
| I-011 | Qwen Code 适配器 | P2 | 2026-10-12 |
| I-015 | Droid (Factory) 适配器 | P3 | 2026-10-12 |
| I-014 | Amp 适配器 | P3 | 2026-10-12 |
| I-010 | Gemini CLI 适配器 | P2 | 2026-10-12 |
| I-017 | Kilo 适配器 | P3 | 2026-10-12 |
| I-016 | Hermes Agent 适配器 | P3 | 2026-10-12 |
| I-012 | Goose 适配器 | P2 | 2026-10-12 |
| I-009 | OpenCode 适配器 | P2 | 2026-10-12 |
| I-008 | GitHub Copilot CLI 适配器 | P2 | 2026-10-11 |
| I-007 | DSH 会话适配器 | P1 | 2026-10-11 |
| I-006 | ZCode SQLite 适配器 | P1 | 2026-10-11 |
| D-002 | ScottPlot.Avalonia 集成 spike | P2 | 2026-10-11 |
| I-005 | 本地采集数据源接线，CLI 降级为对账工具 | P1 | 2026-10-11 |
| Q-001 | 与 ccusage CLI 的对账测试 | P1 | 2026-10-11 |
| I-004 | Codex JSONL 适配器 | P2 | 2026-10-11 |
| I-003 | 自有定价引擎（内置快照 + models.dev + 用户覆盖） | P1 | 2026-10-11 |
| I-002 | SQLite facts 存储 + migration runner | P1 | 2026-10-11 |
| I-001 | Claude Code JSONL 直读适配器 | P1 | 2026-10-11 |
| R-001 | Reports 每日数据表格 | P1 | 2026-06-19 |
| D-001 | Dashboard 卡片布局优化 | P1 | 2026-06-19 |
| P-001 | Windows 打包发布 | P1 | 2026-06-19 |
| R-002 | Reports 排序和筛选 | P2 | 2026-06-19 |
| R-003 | Reports 导出 CSV | P2 | 2026-06-19 |
| S-001 | Settings 基础页面 | P2 | 2026-06-19 |
| R-004 | Model Breakdown 页面 | P2 | 2026-06-19 |
