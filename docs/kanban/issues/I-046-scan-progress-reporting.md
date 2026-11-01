---
id: I-046
title: 源内进度上报——数据源表显示「正在处理 3200/16403」，用户能区分「在跑」与「卡了」
type: infra
priority: P2
size: M
status: todo
created: 2026-11-01
updated: 2026-11-01
blocked: none
---

## Goal

把扫描进度从「每源完成后一次性登记」（现状）细化到**源内部按文件数递增上报**（如每 500 个文件一次），
让设置页数据源表在扫描期间能显示「正在处理 3200/16403」——issue #1 报告者的顺带改进 #1
（他原话："哪怕每 500 个文件一次……用户立刻能区分'在跑'与'卡了'"）。

## Background

### 现状

- 进度登记只有一处：`LocalIngestionDataSource.cs:314` 的 `_scanRegistry?.ReportScan(agent, status, recordCount, time)`，
  在**每个源整体完成后**才调用（数据源异常时 `:309` 登记 Error 快照）。
- 设置页数据源表（S-002）经 `IngestionScanRegistry`（`Core/Services/IngestionScanRegistry.cs`）读快照，
  现有语义是「结果态」（Ok / Partial / NoData / Error / 尚未扫描），**没有进行中粒度**。
- Claude 源内部要扫上万个文件（issue #1：16,403 个 .jsonl / 8.32 GB），全串行
  （`IncrementalJsonlScanner.ScanAsync` 的 `foreach` 逐个 `await ParseFileAsync`），
  整个源扫描期间 UI 没有任何中间信号。

### 触发事件

- issue #1：报告者首启时界面停在「正在初始化」，无法区分"在跑"与"死了"
  （后来证实是去重阶段二次方退化，PR #2 已修）；即便修后首启 8 GB 历史仍要跑一段时间，
  没有中间进度依然像"假死"。他在 issue 里明确把这条列为顺带改进 #1。

## 定案方向（实现时确认）

1. **上报点**：`IncrementalJsonlScanner.ScanAsync` 的文件循环内按节流粒度回调
   （每 N 个文件 / 每 N 秒一次，取大者防高频抖动；N 的默认值实现时定，报告者建议 500 文件）。
   回调经适配器透传到 `IngestionScanRegistry`，注册表新增「进行中」快照形态
   （agent / 已处理文件数 / 发现文件数 / 开始时刻），不改变现有「结果态」语义。
2. **共用引擎一次受益**：Claude（I-001）与 Codex（I-004）共用 `IncrementalJsonlScanner`，
   在引擎层加回调两个源同时获得；SQLite 账本类源（ZCode/Goose/Hermes/Kilo/OpenCode 等）本身是
   全量查询、秒级完成，**不做**源内进度（保持简单，报告者场景全在 JSONL 源）。
3. **UI 呈现**：设置页数据源表在扫描期间对进行中源显示进度行；扫描完成后回到现有结果态。
   文案进 `Translations`（zh 源 key + 全语种覆盖，i18n 纪律）。
4. **节流回调的线程语义**：`ReportScan` 现有调用在采集线程，新增回调也在采集线程，
   注册表沿用现有的线程安全约定（实现时对齐 `IngestionScanRegistry` 注释里声明的模型）。
5. **卡内明确不做**：不做全管道总进度（多源串行 + 每源文件数差异大，加权总进度必然失真）；
   不做取消按钮（gate 串行化下取消一轮的语义要单独设计，超范围）。

## Acceptance Criteria

- [ ] Claude/Codex 源扫描期间，`IngestionScanRegistry` 能读到按文件数递增的进行中快照
      （fixture 用可暂停的 parseFile 委托断言中间值）
- [ ] 节流正确：N 文件/N 秒双条件，快速小目录不产生回调风暴（用例锁回调次数上限）
- [ ] 设置页数据源表扫描期间显示进行中文案，完成后回结果态（headless 断言语言无关）
- [ ] 全部新文案进 Translations 且全语种覆盖测试全绿
- [ ] 不改扫描语义：文件记账、去重、I-042 短路判定、诊断计数全部不变（既有测试零改动通过）
- [ ] 全量测试 0 失败

## 开放问题

- 进行中快照的展示字段（"3200/16403"数字对 + 耗时？）与数据源表现有列的排布。
- `ReportScan` 完成登记与进行中快照的时序边界（一轮结束必须覆盖掉最后的中间态）。

## Dependencies

- none。与 I-045（文件日志）天然互补：日志回答"事后哪一步慢"，进度回答"当下在不在跑"。

## Related

- issue #1（报告者顺带改进 #1 的实现卡）
- S-002（设置页数据源表的来源卡，本卡在其表上加进行中形态）
- I-042（无变化轮短路——短路轮 FilesChanged=0，无文件级进度可言，UI 需容忍"瞬间完成"）

## Development Log

### 2026-11-01 建卡

- 用户指示"为那三项建卡"（issue #1 报告者提出的三项改进，本卡 = 顺带改进 #1）。
- 优先级 P2：#2 修掉根因后"假死"已消除，本卡价值回归到"大历史量首启/重扫期间的确定性反馈"，
  是体验改进而非缺陷修复；排在 I-045（诊断面空白）之后。
