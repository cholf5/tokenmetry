# 产品转向决定：Opinionated Usage Stats

**日期**: 2026-10-11
**状态**: 已决定，生效中
**性质**: 产品定位转向记录 + 面板平台方向封存备忘

---

## 1. 决定摘要

经 2026-10-11 的方向评估，本项目做出如下决定：

1. **继续使用 C# / Avalonia** 完成本项目，不换栈。
2. **放弃"用户自定义面板平台"（Grafana 式）方向**：不再做面板运行时、dashboard JSON schema、布局编辑器。
3. 产品定位改为：**Opinionated Usage Stats** —— 把"我们替你选好的默认视图"做到极致的本地 AI 编程成本监控器。
4. 自主采集（直读 JSONL）、facts 数据层、定价引擎、ScottPlot 图表等技术决策**全部保留**——这些与面板平台无关，是数据质量的根基，原方案中这部分工作量一项都不浪费。

一句话定位：

> 打开就知道 AI 编程花了多少钱、是否异常、钱花在哪——视图是我们选好的，数据是你自己的。

---

## 2. 为什么转向（决策记录）

### 2.1 面板平台与当前技术栈错配

面板平台的产品重心在"交互密集的 UI"：面板布局编辑器、图表交互、查询配置表单。参照 Grafana / Metabase / Superset，这类产品清一色是 Web 前端——面板平台是 Web 的原生物种。在 Avalonia 上实现同样的能力：

- 布局编辑器：Web 有 react-grid-layout 这类成熟轮子，Avalonia 需要手写拖拽网格；
- 图表交互（tooltip / crosshair / 缩放）：Web 是声明式的，Avalonia 需要手写 overlay。

工作量翻倍且体验上限更低。结论：**"C# / Avalonia + 面板平台"组合不成立**，这是本次转向的直接原因。

### 2.2 为什么不换 Electron + React

换栈评估的结论是 Electron + React 对面板平台确实更优，且当时换栈的沉没成本仅约 1-2 周骨架。但作者决定**留在 C#**：动机是业余项目存活的第一因素，作者想用 C# 做完这个项目，这比"指标上最优的栈"更重要。该决定有效且不再重开评估。

### 2.3 候选路线裁决表

| 路线 | 结论 | 原因 |
|---|---|---|
| Electron + React 重写 | 否决 | 作者决定留在 C# |
| 复活 ccusage-tauri（Rust 冻结，新逻辑走 TS） | 否决 | 每次动采集/事实表都要回 Rust，摩擦来源未消除 |
| 留在 Avalonia + 继续做面板平台 | 否决 | 见 2.1 |
| **留在 Avalonia + 转 Opinionated 固定 UI** | **采纳** | 低占用托盘常驻 + 优秀默认视图正是 Avalonia 擅长的形态 |

### 2.4 转向的代价与收益

- 代价：放弃"自定义自由度"这个差异化点。
- 收益：产品与栈重新对齐。Opinionated 产品赢在**默认视图的决策质量 + 性能 + 零配置**，这恰好是 Avalonia 轻量原生的主场，也是原 PRD 的强项（Raycast/Linear 式的克制观感、"5 秒知道状态"）。

---

## 3. Opinionated 的立场（我们替用户做的决定）

固定 UI 不等于没有主张。以下默认行为是产品的一部分，写进这里防止将来摇摆：

1. **本地优先**：不上传、不登录、不云同步。SQLite 只存聚合事实与配置，不存 prompt / 代码 / 会话内容。
2. **可对账**：任何成本数字必须能与 `ccusage` CLI 输出核对（容差内一致），这是产品的信任锚。
3. **离线优先的定价**：内置价格快照，联网刷新是后台增强（TTL 24h），永远不阻塞 UI。
4. **固定信息架构**：Dashboard / Reports / Breakdown / Settings 四页 + 托盘。不做插件、不做皮肤、不做布局自定义。
5. **克制的数据表达**：趋势（时间序列）、构成（TopN）、异常（通知）三种表达够用，不追求图表花样。

---

## 4. 保留与丢弃清单（相对面板平台方案）

**保留（工作量不浪费）**：

- 自主采集：Claude Code / Codex JSONL 直读适配器（ccusage-tauri 的 claude.rs / codex.rs 为移植蓝本）
- SQLite facts 表（daily × agent × model × project + session）+ 真正的 migration runner
- 定价引擎：内置快照 + models.dev（TTL 24h）+ 用户覆盖，三级回退
- 与 ccusage CLI 的对账测试
- ScottPlot.Avalonia 5（≥5.1.59，已核实兼容 Avalonia 12 + net10.0）

**丢弃（面板平台专属，不再实现）**：

- Dashboard / Panel JSON schema 与导入导出
- 面板运行时与面板类型系统
- 布局编辑器、查询配置表单、模板变量
- （远期再议）localhost HTTP 查询 API

---

## 5. 固定信息架构（v1 产品规格）

- **Dashboard**：KPI 行（今日 / 本周 / 本月成本、今日 tokens）+ 30 天趋势图（cost / tokens 切换，ScottPlot）+ Top Model / Top Agent / Top Project + 最近一条异常提示。默认时间范围今日，可切 7 / 30 / 90 天。
- **Reports**：Daily / Monthly / Session 三个 tab；排序、筛选、CSV 导出（在现有能力上扩展）。
- **Breakdown**：Model / Agent / Project 三个维度。
- **托盘**：常驻显示今日成本；菜单：打开主窗、立即刷新、设置、退出。
- **通知**：成本 / Token 异常检测（沿用 AnomalyDetector）+ 预算阈值，带去重，可关闭。
- **Settings**：数据源路径（自动探测 + 手选）、刷新间隔、定价覆盖、通知 / 预算、开机自启。
- **空态**：无数据时给出支持工具清单 + 重新扫描按钮，永不暴露 ENOENT / 堆栈。

---

## 6. 里程碑重排

| 里程碑 | 内容 | 状态 |
|---|---|---|
| v0.1 可核对的监控器 | 真实数据 + Dashboard + Reports 表 | ✅ 已达成 |
| v0.2 分析与配置 | 排序 / 筛选 / CSV / Breakdown / Settings | ✅ 已达成 |
| v0.3 自主采集引擎 | JSONL 适配器、facts 表 + 迁移框架、定价引擎、对账测试、Codex 适配 | 🔨 进行中（当前迭代） |
| v0.4 固定可视化 | ScottPlot 集成、Dashboard 重构为 KPI + 趋势 + TopN、Monthly / Session tab | 规划 |
| v0.5 常驻体验 | 通知去重 + 预算、autostart、刷新可靠性 | 规划 |
| v1.0 稳定发布 | Windows / macOS 打包、CI release、README、图标、空态打磨 | 规划 |

详细任务卡片见看板 `docs/kanban/board.md`。

---

## 7. 封存备忘：如果将来重启面板平台

**触发条件**（全部满足再考虑）：v1.0 稳定发布；facts 层在生产中稳定运行；作者仍有持续投入意愿。

届时可直接复用的资产：

1. **facts 表 + 自主采集 + 定价引擎** —— 面板平台的数据地基已就绪，约占面板平台总工作量的 40%。
2. 本文第 4 节的丢弃清单 = 届时的待建清单（dashboard JSON schema、面板运行时、编辑器）。
3. 关键技术判断（届时需重新校验）：面板平台 UI 是 Web 原生物种，届时建议 UI 用 Web 栈（Electron / Tauri shell 均可），C# 引擎经 localhost HTTP API 暴露——**引擎是栈无关的，这是本次转向保留下来的最值钱资产**。
4. 参考实现：`../ccusage-tauri` 的 pricing_refresh.rs（定价三级回退）、scheduler.rs（分片轮询）；cache.rs 的教训（无迁移框架导致 DROP 重建，勿复制）。

**关联文档**：

- `docs/chore/why-ccusage-so-slow.md` — 自主采集的性能依据
- `docs/handoff/260618-ccusage-gui-avalonia.md` — C# 版起点交接
- `../ccusage-tauri/` — 解析 / 定价 / 调度的移植蓝本
