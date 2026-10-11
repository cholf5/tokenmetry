---
id: I-007
title: DSH 会话适配器
type: feature
priority: P1
size: M
status: todo
created: 2026-10-11
updated: 2026-10-11
blocked: none
---

## Goal

直读 DSH（DeepSeek Harness）本地会话日志（~/.dsh/sessions/<encoded-cwd>/session-*/session.v*.jsonl.zstd），产出 agent=DSH 的统一 usage 记录。

## Background

DSH 不在 ccusage 支持列表内（2026-10-11 全量核实），需自行实现适配器。本机数据勘察（66MB）：zstd 压缩 JSONL，目录名即项目路径（--Users-theo-dev-<proj>-- 式编码）。

**参考蓝本（2026-10-11 确认）：`../dsh-usage-stats`**（MIT，DSH web 用量插件，本机 91 文件 / 7907 条 usage 事件抽样核实过其口径）。其聚合层为纯 JS、无框架依赖，可整体移植为 C# 参考：

- `lib/usage.js` — fold 口径权威实现（样本载体、替换语义、模型归因、日归属）
- `lib/pricing.js` — DeepSeek USD 定价规则目录 + peak/offPeak 时段表
- `scripts/verify-raw.mjs` — **直读 zstd 文件的现成蓝本**（帧扫描器 + 逐帧解压）+ 四路对账方法

关键口径（均已实测核实）：

- **token 语义**：四桶 `inputTokens` / `outputTokens` / `cacheReadTokens` / `cacheWriteTokens`，缺失字段按 0；**inputTokens 不含缓存切片**（实测样本 1372 + 7488 + 339 = 9199 = totalTokens）；raw 里的 `totalTokens` 字段参考实现忽略、自行按四桶求和。官方 `session.list` tokenUsage 投影的 input 桶字段名就叫 `uncachedInputTokens`，反向印证。
- **usage 样本有两种载体**：`assistant/chunk`（`data.chunk.type === "usage"`，usage 在 `data.chunk.usage`）和 `assistant/message`（usage 在 `data.usage`）——比原勘察多一种，抽取须同时覆盖。
- **替换语义**：同 `(turn, step)` 的重复样本是**替换不是累加**（减旧样本桶、加新样本桶，且重归属到后样本的事件日期）；增量 fold 靠 `lastSample` 游标跨片保持该语义。
- **模型归因**：`assistant/message` → `data.message.source.{provider,model}`；usage chunk → 回退最近 `request/header` 的 `data.header.config.{provider,model}`；无信息落 `unknown/unknown`。`provider/model` 组合键使不同 provider 的同名模型分开统计。全零样本（warmup 请求）过滤。
- **日归属**：`event.time`（epoch ms）按**本地日历日** `YYYY-MM-DD`。
- **文件布局**：canonical 文件名 `session.jsonl`（v0）/ `session.v<N>.jsonl`（v1+），均可 `.zstd`；迁移过的会话目录**保留多代文件，只读最高版本**；session id 取 `type === "session"` 头行的 `id` 字段（回退目录名）；`DSH_HOME` 环境变量 → `~/.dsh`（verify-raw.mjs 同口径）。
- **zstd 帧布局**：DSH 每次追加写一个独立 zstd 帧（实测 91/91 文件多帧，最多 1689 帧/文件），**每帧独立可解压**——逐帧解压即流水式方案，增量扫描可记"最后完整帧结束偏移"；帧扫描需容忍尾部 torn frame（半写帧）。verify-raw.mjs 有完整帧扫描参考实现（magic `0xFD2FB528`）。
- **定价**：`lib/pricing.js` 内置 DeepSeek USD 规则目录（per 1M tokens、四桶分价），规则带生效区间 + Asia/Shanghai peak/offPeak 时段表（08-16 前平价 → 08-16~08-22 每日高峰时段 → 之后工作日高峰）；费用按**每个样本的事件时间**匹配历史规则，绝不"当前价 × 累计 token"；正桶无可靠单价时整体 fail-closed 为 null。缓存 `~/.dsh/storages/usage-stats-cache.json`（version 5）的 `pricingFingerprint.pricingCatalog` 即该目录快照（原"缓存含 pricingCatalog"表述修正为该嵌套路径）。注意本机还有 `ark/glm-5.3-flash` 等非 deepseek 路由，超出现有目录覆盖，费用将 fail-closed——token 统计不受影响。

## Acceptance Criteria

- [x] 确认 token 语义：inputTokens 是否含缓存切片、cache 字段是否存在（抽样多会话核对 dsh-usage-stats 口径）；deepseek 模型定价映射来源确定（快照扩充 or 用户覆盖）
  - ✅ 2026-10-11 核实（见 Background）：四桶口径、inputTokens 不含缓存切片、cacheRead/cacheWrite 字段存在（缺失按 0）；定价映射移植 `../dsh-usage-stats/lib/pricing.js` 内置目录（快照扩充），用户覆盖留作后续扩展
- [ ] C# zstd 解压方案落定（托管库或 P/Invoke），**逐帧解压**避免整文件入内存（帧独立可解压，帧扫描器参考 verify-raw.mjs：magic `0xFD2FB528`、容忍尾部 torn frame、记录完整帧偏移供增量续扫）
- [ ] 目录发现（DSH_HOME 环境变量 → ~/.dsh）与增量扫描（mtime/大小记账复用引擎）：会话目录内**只读最高版本** `session.v<N>.jsonl(.zstd)`（v0 = `session.jsonl`），忽略旧代文件
- [ ] usage 记录抽取：`assistant/message`（`data.usage`）**和** `assistant/chunk`（`data.chunk.type === "usage"`）两种载体，同 `(turn, step)` 重复样本按替换语义（减旧加新），agent=DSH，项目路径从目录名还原
- [ ] 与 dsh-usage-stats 项目数字对账（tokens 精确一致）：跑通其 `scripts/verify-raw.mjs` 四路对账方法（raw bytes ↔ session.history ↔ 插件端点 ↔ 官方 tokenUsage 投影，注意投影 input 桶字段为 `uncachedInputTokens`）
- [ ] fixtures + 单元测试 + 接入本地采集管道

## Subtasks

- [ ] （开工时细化；I-006 之外的本批卡片为规划卡，实现时从蓝本移植并回填子任务）

## Dependencies

- I-003（定价）、I-005（接线）

## Test Cases

### TC-001: （开工时补充）

Steps:
1. （依实现回填）

Expected:
- （依实现回填）

## Development Log

### 2026-10-11

- 创建任务卡（多源覆盖批次，蓝本 ../ccusage/rust/adapters/(无上游蓝本，自研)/），尚未开始实现。
- 调研参考蓝本 `../dsh-usage-stats`（DSH web 用量插件，MIT）并回填卡片，修正"无上游蓝本"的初始判断：`lib/usage.js` 为 fold 口径权威实现，`scripts/verify-raw.mjs` 提供直读 zstd 的帧扫描蓝本与四路对账方法，`lib/pricing.js` 提供可移植的 DeepSeek 定价目录。实测核实：91 文件全为多帧 zstd（逐帧独立解压即可流式）、四桶口径（inputTokens 不含缓存切片）、usage 样本两种载体、同 (turn,step) 替换语义、多代日志文件只读最高版本。若移植其代码需保留 MIT 许可声明。下一步：开工时细化 subtasks 并落定 C# zstd 库选型。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: （开工时回填）
- How to run/verify: （开工时回填）
- Results: 尚未开始
