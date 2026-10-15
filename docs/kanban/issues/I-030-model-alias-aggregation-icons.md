---
id: I-030
title: 模型聚合（别名映射）+ 内置模型图标
type: feature
priority: P1
size: L
status: done
created: 2026-10-15
updated: 2026-10-15
blocked: none
---

## Goal

不同来源对同一模型命名不同（GLM-5.3-Flash / glm-5.3-flash / glm-flash-latest / ark/glm-5.3-flash），统计分成多行。让用户在设置页维护「原始名 → 规范名」映射，统计按规范名合并；同时给模型列表配内置厂商图标（模型统计页 + 总览模型分布行）。

## Background

- 全覆盖的内置归一规则不可行（来源命名自由度无限），映射完全交给用户——对齐项目「宁可分开不猜」口径纪律：无映射时行为与原契约逐位一致。
- 交互约定：本地已有数据时给候选列表（数据里出现过的原始名，按用量降序），同时允许自由输入任意字符串（为尚未出现的来源预留映射）。
- 聚合放 Core 查询服务层（`UsageQueryService` 注入 `IModelNameResolver`）而非 App 层后处理：合并行的会话归属、关联 Agent、占比只有在逐会话数据上重算才诚实。
- 匹配用精确（Ordinal）而非忽略大小写：忽略大小写无法表达 "GLM-5.3-Flash → glm-5.3-flash" 这类恰为大小写变体的规则（字典键互相吞并、规范名会被其他变体规则误伤）；大小写变体是数据里独立的原始名，候选清单逐一列出，各映射一次。

## Acceptance Criteria

- [x] 设置页「模型聚合（别名映射）」卡：规则列表（原始名 → 规范名 + 规范名图标，可移除）+ 新增规则（两侧 AutoCompleteBox：候选下拉 + 自由输入）+ 行内校验（空值 / 自映射劝阻）
- [x] 规则增删即落盘（全自动保存纪律）并热更新解析器，统计页重进即按新映射重查
- [x] 模型维度聚合 / 会话数归属 / 关联 Agent / token / 成本 / 占比全部在规范名口径成立；未知成本纪律（null ≠ 0）不变
- [x] 会话页模型下拉与搜索同口径（规范名空间比较；搜索对原始名与归一名都命中）；会话行仍显示原始名（诚实）
- [x] 内置厂商图标（Claude/OpenAI/Gemini/GLM/DeepSeek/Qwen/Kimi/Llama/Grok/Mistral/Doubao/MiniMax/Cohere/文心/混元/Phi + generic 兜底），品牌色令牌不随明暗翻转，伪类驱动明暗自动适配
- [x] 模型统计页第一列与总览模型分布行显示图标（项目行保留系列圆点）
- [x] 用户自定义图标本期不做（内置集 + generic 兜底；将来如需，建议「从内置厂商集中指定」而非导入图片）

## Subtasks

- [x] Core：AppSettings.ModelAliases + IModelNameResolver/ModelAliasResolver（链式映射防环、UpdateAliases 热替换）+ GetRawModelNamesAsync（绕过映射的原始名候选）
- [x] Core：UsageQueryService 模型维度聚合/会话筛选/搜索接解析器
- [x] App：ModelVisualMap 厂商识别 + ModelChip 附加属性 + Tokens/SharedStyles 令牌与样式
- [x] App：设置页聚合卡（SettingsViewModel 规则管理 + SettingsView XAML + Translations 中英文案）
- [x] App：Models 页 / Overview 分布行图标接线（行 VM + XAML）

## Dependencies

- I-028/I-029（查询服务）、S-002（设置页信息架构）、I-027（令牌体系）

## Test Cases

### TC-001: 别名聚合（真实 SQLite fixture）

Steps: 种子数据含同一模型的多个原始名（大小写变体 / 厂商前缀），配映射后查模型维度。

Expected: 合并为一行——token 相加、已知成本相加、缺价原始名贡献未知标记（不折 0）、会话按归一名归属、DominantAgent/AgentCount 取合并后并集口径、占比在合并后全集重算；无映射对照组仍为多行（原契约不变）。

Automated: ModelAliasAggregationTests（Infrastructure，含窗口内合并与原始名候选排序）。

### TC-002: 规则管理（即存即热更新）

Steps: 设置页添加 / 重复添加 / 移除映射规则。

Expected: settings.json 即时落盘、解析器热更新（新映射对后续查询立即生效）、同原始名覆盖旧规则、行内校验不落盘。

Automated: SettingsViewModelTests.AddAliasRule_* / RemoveAliasRule_* / AliasCandidates_*、ModelAliasResolverTests。

### TC-003: 别名面板端到端（headless）

Steps: 渲染设置页 → 向 AutoCompleteBox 键入（KeyTextInput）→ 添加规则。

Expected: Text 逐键双向绑定到 VM（本功能交互关键路径），规则行 + 图标落表、空态消失。

Automated: SettingsPageTests.Alias_panel_renders_and_autocomplete_text_binds_to_add_rule。

## Dev Log

- 2026-10-15：XamlIl 怪癖——`AutoCompleteFilterMode.ContainsOrdinalCaseInsensitive` 在 XAML 编译期不可解析（AVLN2000/AVLN3000，StartsWith/ContainsOrdinal 档可解析），候选过滤改走代码侧 `TextFilter` 谓词（Controls/AutoCompleteFilters.cs，经 x:Static 静态属性暴露——x:Static 不支持方法组）。
- 2026-10-15：会话模型下拉/右栏快筛选项沿用 AgentCatalogService 生命周期缓存（D-008 既有取舍），映射修改后清单重进页面/重启才刷新；统计数字重进即生效。
- 2026-10-15：验证期间工作区多次被并行操作部分回退（README/AGENTS.md docs 提交同一时段），丢失改动已全部重放并经全量测试复核。
