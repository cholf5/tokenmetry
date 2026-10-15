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
   （2026-10-12 注：固定视图集合按 UI 原型扩展为 8 个固定视图——总览 / 会话明细 / Agent 统计 / 模型统计 / 项目统计 / 时间分析 / 成本分析 / 设置；「固定」的含义不变：不做用户自定义布局与插件，只是替用户选好的固定视图更多了。见 v0.4。）
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
- 全部单元测试通过：`dotnet test Tokenmetry.sln` 242 通过 / 5 跳过（跳过为 Windows 专属 CLI runner 测试 ×3 与真实数据对账门控测试 ×2，门控开时后者实跑通过）。

### v0.3.5 - 多源数据覆盖（2026-10-11 立项，批次穿插在 v0.4 执行）

Goal:
- 对齐 ccusage 的 18 源全景：把 `../ccusage`（Rust 版）各 agent 适配器逐个移植为本项目的统一 usage 记录源；DSH（不在 ccusage 支持列表，2026-10-11 核实）自研适配。
- 每源一张卡（I-006～I-022），执行按优先级逐卡进行；本机有真实数据的源（ZCode / DSH / Copilot CLI）优先且必须过对账。

Candidate tasks:
- `I-006` ZCode SQLite 适配器 ✅（2026-10-11）：真实数据对账 tokens 两侧逐位一致（22,315 条 / total 2,418,223,974），自定义 provider 行成本口径与 CLI 一致；内置快照补 zai 段 22 条 GLM 价格
- `I-007` DSH 会话适配器（P1，自研：~/.dsh/sessions zstd JSONL）
- `I-008` GitHub Copilot CLI 适配器（P2，本机有数据：~/.copilot；shutdown 快照差分语义最复杂）
- `I-009`～`I-022` OpenCode / Gemini CLI / Qwen Code / Goose / Kimi / Amp / Droid / Hermes / Kilo / Codebuff / pi-agent / OpenClaw / Antigravity / Grok（本机暂无数据，fixture 依蓝本合成；SQLite 源复用 I-006 骨架）

Notes:
- 逐源口径与蓝本路径见各卡 Background；ccusage 侧布局变更时以 `../ccusage/rust/adapters/<source>/README.md` 为准。
- 已知口径：ccusage 对部分源（如 ZCode 自定义 provider 的 GLM 行）报 missingPricing/成本 0；本项目对账锚点是 CLI，保持同口径，缺价模型由用户覆盖补齐。

### v0.4 - 固定可视化：UI 原型落地 🔨（当前迭代）

Goal:
- 以 `docs/design/ccusage_analytics_prototype.html`（2026-10-12 收入）为设计基准，把应用从旧 Fluent 界面整体重建为原型观感：Semi.Avalonia 主题 + 三栏外壳 + 8 个固定视图 + 自有聚合查询服务。数据口径延续产品立场：可对账、缺价显示未知不折零、不伪造时间精度。

选型记录（2026-10-12）:
- 基础主题换 **Semi.Avalonia**（12.1.0.1，需 Avalonia 12.0.4 → 12.1.x 小版本升级）；备选 SukiUI（风格过强）与保留 Fluent 手写样式（保底）已评估。视觉上限由图表还原度与样式纪律决定，主题只解决默认控件气质——判断依据见当日会话结论。

Candidate tasks（依赖顺序执行，详情见各卡）:
- 基建线：`I-026` Semi 主题迁移 → `I-027` 设计令牌与共享样式（原型色板/卡片/系列色/mini 进度条/toast）
- 数据线（与基建并行）：`I-028` 聚合查询 A（日趋势 + KPI + 环比；含 D-002 遗留的日历窗口语义定案）、`I-029` 聚合查询 B（Agent/模型/项目维度 + 会话列表 + 小时分布，缺精度源按 unknown 返回）
- 外壳：`D-003` 三栏外壳 + 导航 + 全局筛选（吸收「Dashboard 固定视图」规划；右侧最近会话栏常驻）
- 图表控件：`D-004` ScottPlot 正式集成（堆叠柱+折线三形态、hover helper 含右缘裁剪翻转）、`D-005` 自绘环形图 + 热力图
- 页面：`D-006`/`D-007` 总览页（吸收「Dashboard 重构 KPI+趋势+TopN」）、`D-008` 会话明细页（吸收「Reports Session tab」）、`D-009` Agent、`D-010` 模型（未知成本样板页）、`D-011` 项目、`D-012` 时间分析、`D-013` 成本分析、`S-002` 数据源与设置页重构
- 收尾：`Q-002` 旧视图清理与视觉验收（8 页 × 明暗两套差异清单）

已完成前置：
- `D-002` ScottPlot.Avalonia 集成 spike ✅（2026-10-11）：5.1.59 在 Avalonia 12 + net10.0 实测可用，结论与遗留决策点（日历窗口语义、tooltip 右缘裁剪）已并入 D-004/I-028。

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
- 月度（Monthly）汇总视图——原 v0.4「Reports Monthly tab」，UI 原型落地批次未包含，作为时间分析的后续候选（2026-10-12 移入）
- localhost HTTP 查询 API（面板平台的前置件，栈无关引擎的暴露面）
- Export to Excel `.xlsx`
- 多机数据聚合
- ~~深色模式 / 主题~~——主题系统已落地（light/dark/system + UI 原型批次全面明暗适配），2026-10-12 移出
- 周 / 月预算规划
