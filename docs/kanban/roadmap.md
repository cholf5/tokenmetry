# Roadmap

This roadmap preserves long-term goals and milestone direction for `ccusage-gui`. Detailed executable task cards live in `docs/kanban/issues/` and are tracked in `docs/kanban/board.md`.

## Planning Policy

- Do not create detailed issue cards for every possible future requirement at once.
- Keep detailed cards focused on the current 1–2 iterations or the next 5–10 actionable items.
- Use this roadmap for long-term themes, epics, and candidate work.
- Convert roadmap items into detailed task cards when they become near-term work.
- Use discovery/spike cards for uncertain work before creating implementation tasks.

## Product Vision

Build an open-source, local-first, tray-resident desktop monitor for personal AI coding costs. The app should help users understand Claude Code / Codex usage cost, inspect daily records, notice anomalies, and eventually package as a normal Windows desktop app.

## Milestones

### v0.1 - 可核对的本地成本监控器

Goal:
- Show real local `ccusage` data in a Windows desktop GUI.
- Provide both Dashboard summaries and a daily table that users can reconcile against the CLI.
- Produce an initial Windows runnable build.

Candidate tasks:
- `R-001` Reports 每日数据表格
- `D-001` Dashboard 卡片布局优化
- `P-001` Windows 打包发布

Exit criteria:
- Dashboard displays real ccusage data.
- Reports shows daily records with costs and tokens.
- Tests pass and App builds.
- A documented Windows publish command exists.

### v0.2 - 分析和配置能力

Goal:
- Make the app useful for day-to-day inspection and configuration.
- Add sorting/filtering/export for Reports.
- Add basic Settings and model-level breakdown.

Candidate tasks:
- `R-002` Reports 排序和筛选
- `R-003` Reports 导出 CSV
- `S-001` Settings 基础页面
- `R-004` Model Breakdown 页面

Exit criteria:
- Users can filter and export daily records.
- Users can view and edit basic local settings.
- Users can inspect model-level cost and token breakdown.

### v0.3 - 通知、预算和托盘体验

Goal:
- Improve always-on monitoring behavior.
- Make anomaly detection and tray usage practical.

Candidate work:
-完善异常通知触发逻辑
- Budget threshold notifications
- Tray menu polish: show/hide, refresh now, exit
- Background refresh reliability and concurrency handling
- Optional startup behavior investigation

Exit criteria:
- Background refresh is reliable.
- Notifications are useful and configurable.
- Tray behavior feels stable for daily use.

### v0.4 - 历史分析和可视化

Goal:
- Add higher-level analysis beyond tables.

Candidate work:
- 7/30/90-day cost trends
- Monthly comparison
- Model cost trend
- Agent usage summary if source data supports it
- Project-level analysis if future `ccusage` data exposes project breakdown

Exit criteria:
- Users can understand cost trends without leaving the app.
- Visualizations are consistent with Reports data.

### v1.0 - 稳定发布

Goal:
- Prepare the project for public/open-source release.

Candidate work:
- Installer or packaged distribution
- App icon and branding polish
- README installation and troubleshooting guide
- Release checklist
- Known limitations documentation
- Optional code signing investigation

Exit criteria:
- A user can install/run the app from release artifacts.
- Documentation explains setup, data source, privacy/local-first behavior, and troubleshooting.
- Core workflows are stable enough for public use.

## Parking Lot

Future ideas that are not yet committed:

- Budget planning by week/month
- Export to Excel `.xlsx`
- Import historical snapshots
- Multi-machine data aggregation
- Theme/dark mode
- Plugin support for additional AI coding tools
- GitHub Actions release automation
- Real project-level breakdown if upstream `ccusage` exposes it
