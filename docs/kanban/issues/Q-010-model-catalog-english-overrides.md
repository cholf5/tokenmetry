---
id: Q-010
title: 数据与计价页模型目录 Section 英文漏覆盖——29 键回落中文（I-035 欠账）
type: bug
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

英语 UI 下 Data & pricing 页「模型目录」Section（含模型编辑弹层）不再出现中文：目录标题/说明、表头（模型 ID / 厂商 / 单价 / 来源 / 操作）、来源徽标（内置 / 在线 / 自定义 / 已改）、未定价、编辑表单标签与校验错误、按钮（新增 / 编辑 / 移除覆盖 / 保存 / 清空 / 取消）、重算 toast 全部走英文覆盖。

## Background

- 用户反馈（2026-10-24 会话）："Data & pricing 页签里，当多语言设置为英语时，'模型目录'那个 Section 里仍然有大量中文。"
- 取证（键差集脚本 + `Translations.cs`）：I-035 落地时目录主体键只进了 Zh 字典，En 只补了后加的搜索 / 筛选 / 作用域 13 键——En 块注释自证「既有目录键暂无英文覆盖，zh 兜底」。En 缺项按合并语义回落中文（设计机制），缺项即英语界面漏中文；全字典差集恰好 29 键、全部属于本 Section，即用户所见「大量中文」。
- XAML / ViewModel 无硬编码中文（全部 `{Binding …, Source={x:Static loc:Translations.Instance}}`），排除第二根因。

## Acceptance Criteria

- [x] 29 个目录键全部补 En 覆盖（En 块重写为与 Zh 块同序全量对齐；既有 13 键措辞零改动）
- [x] 新增回归测试 `English_CoversEveryChineseSourceKey`（反射比对 Zh / En 键集合，TDD：RED 实锤 29 缺键 → GREEN）——回落机制本身仍由 `English_MissingKey_FallsBackToChineseSource` 守护，本测试让它不再被触发
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿（1449 Passed / 0 Failed / 22 Skipped，+1 即本测试）

## Subtasks

- [x] `Translations.cs`：En 模型目录块重写为全量覆盖（注释同步去除「暂无英文覆盖」欠账标记）
- [x] `TranslationsTests.cs`：键奇偶回归测试（`SourceDictionary` 反射取私有 Zh / En 字典）

## Dependencies

- I-035（模型目录落地，欠账来源）、S-003（模型目录设置 UI）
