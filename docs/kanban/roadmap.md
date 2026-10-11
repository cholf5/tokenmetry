# Roadmap

This roadmap preserves long-term goals and milestone direction for `ccusage-desktop`. Executable task cards live in `docs/kanban/issues/` and are tracked in `docs/kanban/board.md`.

## Planning Policy

- Do not create detailed issue cards for every possible future requirement at once.
- Keep detailed cards focused on the current 1–2 iterations or the next 5–10 actionable items.
- Use this roadmap for long-term themes, epics, and candidate work.
- Convert roadmap items into detailed task cards when they become near-term work.
- Use discovery/spike cards for uncertain work before creating implementation tasks.

## Product Vision

**Opinionated Usage Stats** —— 本地优先、托盘常驻的 AI 编程成本监控器。不提供自定义面板，而是把一组我们替用户选好的固定视图做到极致：打开就知道花了多少钱、是否异常、钱花在哪。

定位转向记录与完整依据见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`。产品立场（不变量）：

1. 本地优先：不上传、不登录、不云同步。
2. 可对账：成本数字与 `ccusage` CLI 容差内一致。
3. 离线优先定价：内置价格快照，联网刷新永远在后台、不阻塞 UI。
4. 固定信息架构：Dashboard / Reports / Breakdown / Settings + 托盘，不做插件与布局自定义。
5. 克制的数据表达：趋势、构成、异常三种表达够用。

## Milestones

### v0.1 - 可核对的本地成本监控器 ✅（2026-06-19）

Dashboard 真实数据 + Reports 每日表格 + Windows 发布文档。卡片：R-001、D-001、P-001（已归档）。

### v0.2 - 分析和配置能力 ✅（2026-06-19）

Reports 排序 / 筛选 / CSV 导出、Settings 基础页、Model Breakdown。卡片：R-002、R-003、S-001、R-004（已归档）。

### v0.3 - 自主采集引擎 ✅（2026-10-11）

Goal:
- App 不依赖 `ccusage` CLI 也能产出可信数据：直读 Claude Code / Codex JSONL，自有 facts 存储与定价引擎。
- 成本数字与 CLI 对账通过——这是产品的信任锚。

Delivered cards:
- `I-001` Claude Code JSONL 直读适配器 ✅
- `I-002` SQLite facts 存储 + migration runner ✅
- `I-003` 自有定价引擎（内置快照 + models.dev + 用户覆盖）✅
- `Q-001` 与 ccusage CLI 的对账测试 ✅
- `I-004` Codex JSONL 适配器 ✅
- `I-005` 本地采集数据源接线，CLI 降级为对账工具 ✅

Exit criteria 证据：
- 断网且无 CLI 环境刷新出真实数据：`I-005` 探针冒烟（本机真实 Codex 日志 19 文件 / 2931 记录，快照定价 0 未命中，全程不 spawn 进程）。
- 对账测试容差内通过：fixture 对账零差异；本机真实 Codex 数据 tokens 全等、cost 与 ccusage v20.0.29 逐分钱一致（$101.3856438）。证据与逐日明细：`docs/reconciliation/2026-10-11-reconciliation.md`。
- 全部单元测试通过：`dotnet test CCUsage.sln` 242 通过 / 5 跳过（跳过为 Windows 专属 CLI runner 测试 ×3 与真实数据对账门控测试 ×2，门控开时后者实跑通过）。

### v0.4 - 固定可视化 🔨（当前迭代）

Goal:
- 用 ScottPlot 补齐趋势可视化；Dashboard 重构为 KPI 行 + 30 天趋势 + TopN 的固定视图；Reports 增加 Monthly / Session 维度。

Candidate tasks:
- `D-002` ScottPlot.Avalonia 集成 spike ✅（2026-10-11）：5.1.59 在 Avalonia 12 + net10.0 实测可用，缩放/平移零配置、tooltip 成本可控（对齐翻转有坑已记录），正式集成估 1–2 天；结论见任务卡 Development Log
- ScottPlot 正式集成：30 天趋势图（cost / tokens 切换）——需先定日历窗口语义（空日补零 / 断线 / 仅画实际日，spike 观察到的产品决策点）与 hover helper（含右缘裁剪处理）
- Dashboard 重构为固定视图（KPI 行 + 趋势 + Top Model / Agent / Project）
- Reports Monthly / Session tab（依赖 I-002 的 session_facts，表结构已就绪）

### v0.5 - 常驻体验

Goal:
- 托盘与后台刷新达到日常可用：通知去重、预算阈值、autostart、刷新可靠性。

Candidate tasks:
- 通知 v2：异常去重 + 预算阈值 + 对应设置项
- Windows / macOS 开机自启
- 后台刷新可靠性（并发去重、失败退避、休眠唤醒补偿）

### v1.0 - 稳定发布

Goal:
- 公开发布就绪。

Candidate tasks:
- Windows installer + macOS app 打包，CI release 自动化
- 应用图标与品牌
- README（安装、数据源、隐私说明、排障）
- 空态 / 引导打磨、Known limitations 文档

## Parking Lot

Future ideas that are not yet committed:

- 面板平台（Grafana 式自定义）——已封存；重启条件与可复用资产见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md` 第 7 节
- localhost HTTP 查询 API（面板平台的前置件，栈无关引擎的暴露面）
- Export to Excel `.xlsx`
- 多机数据聚合
- 深色模式 / 主题
- 周 / 月预算规划
