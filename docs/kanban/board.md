# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

> 2026-10-11 迁移说明：按 markdown-kanban 规程，旧 7 列看板收敛为 Todo | Done。原 In Review 的 7 张 v0.1/v0.2 卡片经 git 记录核实（功能均已提交、卡片内有测试通过记录）后记为 Done，卡片文件移入 `issues/archive/`（保留旧格式）。产品定位转向见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`。

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|

> 2026-10-12：按用户决定一次性建 UI 原型落地批次卡 I-026～I-029 / D-003～D-013 / S-002 / Q-002（17 张）。设计基准 `docs/design/ccusage_analytics_prototype.html`（同日自 ~/Downloads 收入仓库）；选型 Semi.Avalonia（依据见 I-026 Background）。本批是 v0.4 主体的全景规划，执行按依赖顺序逐卡进行：I-026 → I-027 → D-003，I-028/I-029 与主题线并行，随后 D-004/D-005 → D-006 → 其余页面 → Q-002。与「看板保持 5–10 张」的常规策略有意偏离，理由同 2026-10-11 适配器批次。

> 2026-10-11：按用户决定一次性建多源覆盖批次卡 I-006～I-022。蓝本为 ../ccusage/rust/adapters/（Rust 移植版，18 源清单见其 README「Supported Sources」）；DSH 不在 ccusage 支持列表（核实过，README 的 "trendshift" 含 "dsh" 是误匹配），I-007 为自研。与「看板保持 5–10 张」的常规策略有意偏离——本批是一次性全景规划，执行仍按优先级逐卡进行。**2026-10-12 追记：I-006～I-022 十七源适配器全部完成**（对账口径见各卡 TC），多源覆盖批次收口；I-024 候选计价接线随九源收口（UnpricedModels 欠账消除，快照补 tiers 段并刷新 kimi-k2.6）；**I-025 同日收口**：快照 7 条目经 CLI 探针核实已逐位等于 npm 20.0.29 锚点（I-024 审查清单对的是 rust HEAD、与锚点漂移，照单改价会炸对账——详见该卡 B-001），不改价，归一化歧义守卫 + 快照 _provenance 溯源落地。**Todo 清空。**

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
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
