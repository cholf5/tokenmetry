---
id: B-015
title: 计价页价格输入固定 InvariantCulture——de-DE 等逗号小数文化输入必被拒
type: bug
priority: P3
size: S
status: todo
created: 2026-10-23
updated: 2026-10-23
blocked: none
---

## Goal

模型目录编辑弹层的价格输入接受本机文化的小数格式（`0,5`），存储口径保持 invariant 不变。

## Background

- 2026-10-23 扩大扫描（文化维度）发现：`DataPricingViewModel.cs:842` `decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, ...)`，回显 `:710-720` 同为 `ToString(CultureInfo.InvariantCulture)`。
- 机理：de-DE 用户按本机习惯输入 `0,5` → 解析失败显示"必须是数字"、不落盘。存储/解析层 invariant 本身没错（防串味），错在**用户输入面**只有机器口径一条路。
- 佐证：2026-10-23 以 tr-TR / de-DE 钉死全测试套件默认文化跑全量皆绿——因为测试喂的都是 "0.5" 形态，覆盖不到本机格式输入路径（产品缺口在输入面，测试面是解析面）。

## Acceptance Criteria

- [ ] 输入解析先试 CurrentCulture 再回落 InvariantCulture（或产品拍板"输入即 invariant + UI 提示格式"，二选一在卡内定案）
- [ ] 存储与回显序列化保持 InvariantCulture（不随本机文化）
- [ ] 回归锁：CurrentCulture=de-DE 下 `0,5` 可录入且等值 0.5；全量测试绿

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；"接受本机格式"还是"提示用户用点号"是 UX 决策，先记录实证）。
