---
id: D-022
title: 官网功能页富数据截图套件——stub fixture × zh/en × 明暗 × 7 页 + 托盘
type: chore
priority: P3
size: M
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

官网（cholf5/tokenmetry-site）功能页配图此前是「真实 DI 隔离空库」的诚实空态渲染（"—" KPI、「启动中…」），不可用。本卡新增一套面向对外展示的截图测试：**密集受控演示数据** × zh/en × light/dark，覆盖 7 个功能页 + 托盘弹窗，产出入库站点仓库 `src/assets/shots/`（命名 `{view}-{locale}-{theme}.png` 与站点约定一致）。

## Background

- D-021 已查明空态根因：headless 测试经 `SemiTestAppBuilder` 的 `AppServices.PathsOverride` 读隔离临时库（有意设计），恒无数据；并首次示范了「裸窗 + StubQueryService」的 stub 数据留档（time-enriched-*.png）。
- 本卡把该模式产品化到全部对外页面：会话 / 模型 / 时间 / 成本 / 项目 / Agent / 数据与计价 + 托盘弹窗。
- 站点侧 Hero 用真机手截图（真实数据），功能特写用本套（受控演示数据）——两者互补，各自记录一种状态。

## Acceptance Criteria

- [x] 一个自包含测试类 `SiteShotsTests`（stub 查询服务 + 受控 fixture 均在文件内，不依赖产品 src 改动）
- [x] fixture 锚定真实今天（相对时间列、默认「最近 7 天」档、日历热图都要活数据）；量级/成本比例取真实区间（7 天 36.44M tokens / $81.22 + kimi-k2.5 缺价未知贡献 / 214 会话，五分量 12/5/5/78）
- [x] 模型名真实（claude-sonnet-4-5 / gpt-5.1 / glm-5.3 / gemini-2.5-pro / qwen3-coder-plus / kimi-k2.5 / claude-haiku-4.5），kimi 缺价走「未知」不折零（诚实口径可见）
- [x] 数据与计价页：8 源状态丰富（Ok×5 / Partial / NoData / 已注册未扫描）+ 默认模型目录（内置快照价）
- [x] 托盘弹窗：今日 KPI + 今日构成三分量（此前站点图输入/输出/缓存三卡恒 "—" 的根因是 fixture 日期非今天）+ Top Agent；主题设在弹层窗体（独立 TopLevel 不继承 owner 的 RequestedThemeVariant——不设则明暗同字节）；留档前裁掉透明落白的阴影留白（39,23,420×546，PIL 两次阈值量边定值）
- [x] 语言循环 finally 恢复进程级单例基线（TestCulture 纪律）；断言语言无关（HasRows / UnpricedCountText != "—" / TodayTokensText != "—" 等结构谓词）
- [x] 8 测试全绿（3s）；`TOKENMETRY_SITE_SHOTS_DIR` 未设置时只断言富数据可渲染不落盘
- [x] 32 张 PNG 目检通过（数据密度/主题/裁剪）并已拷入站点仓库

## Non-goals

- 不做「整窗带侧栏」的富数据渲染（需给隔离库喂数据或 DI 覆写缝，另立卡）；裸窗特写 + Hero 手截图已覆盖叙事需要。
- 不改产品 src 任何代码；fixture 只服务截图，不进任何功能断言。
- 站点仓库的版式改动不在本卡（见站点仓库提交）。

## Dev Log

- 2026-10-25：落地。StubQueryService 七面数据（Kpi/Daily/Hourly/Agents/Models/Projects/Sessions）可配置，模式同 DimensionPagesTests；托盘走 StatusPopoverController（dateService 必须给，null 静默不刷新——交接教训复现）+ Show 后显式 ReloadAsync + PumpUntil。CostViewModel 富数据谓词用 `UnpricedCountText != "—"`（主留档测试同口径）。裁剪用 CopyPixels→WriteableBitmap 纯安全代码（GCHandle 钉住，无 unsafe）。
- 迭代实录：首版托盘明暗同字节（md5 相等实锤主题未生效）→ 弹层窗体上直接设 RequestedThemeVariant 修复；托盘四角纯白（255,255,255）→ 阈值 18/120 两档量边定裁剪框。
