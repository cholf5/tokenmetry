# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

> 2026-10-11 迁移说明：按 markdown-kanban 规程，旧 7 列看板收敛为 Todo | Done。原 In Review 的 7 张 v0.1/v0.2 卡片经 git 记录核实（功能均已提交、卡片内有测试通过记录）后记为 Done，卡片文件移入 `issues/archive/`（保留旧格式）。产品定位转向见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`。

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| I-003 | 自有定价引擎（内置快照 + models.dev + 用户覆盖） | P1 | M | none | none |
| Q-001 | 与 ccusage CLI 的对账测试 | P1 | M | none | I-001, I-002, I-003 |
| I-004 | Codex JSONL 适配器 | P2 | M | none | I-001, I-003 |
| I-005 | 本地采集数据源接线，CLI 降级为对账工具 | P1 | S | none | I-002, I-004 |
| D-002 | ScottPlot.Avalonia 集成 spike | P2 | S | none | none |

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
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
