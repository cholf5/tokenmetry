---
id: D-020
title: 时间范围预设——内置日历档（本月/上月/昨天）+ 用户自定义相对档 + 顶栏范围选择器 Flyout 化
type: feature
priority: P2
size: L
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

顶栏时间范围从「6 档 ComboBox + 自定义档 inline 双 DatePicker」升级为：

1. **内置日历档**：新增 本月 / 上月 / 昨天 三档（与既有 今日/最近 7/30/90 天/全部时间 并列）；
2. **用户自定义相对档**：用户可添加「本周 / 上周 / 最近 14 天」这类相对值预设（封闭词表，不自由命名），
   持久化、随语言重译、可删除；
3. **交互升级**：ComboBox 换成「当前档名按钮 + Flyout」——档位清单、我的预设管理、自定义日期全部收进
   弹层，顶栏不再因自定义档 reflow（inline DatePicker ≈250px 抖动）。

## Background

- 用户提案（2026-10-25 会话）：下拉里加 本月/上月/昨天，自定义里允许加 本周/上周 这类相对值；并问下拉交互是否还够用。
- 口径取证：既有五档里除「今日」全是滚动窗口（终点恒为今天），本月/上月/昨天/本周/上周是日历对齐窗口——
  两族回答不同问题（"最近 30 天" vs "这个月"），后者与记账/预算心智对齐。查询层零改动：
  `GlobalFilterMapper` 只产出 FromDay/ToDay（本地自然日含端点），日历档 ≤31 天远小于 MaxDailySeriesDays=3653。
- 交互取证：内置 + 预设 ≈10–14 项仍在 ComboBox 舒适区，但①管理入口硬塞下拉尾项别扭；②自定义档 inline
  DatePicker 让顶栏整体 reflow（MainWindow.axaml 顶栏 Grid 列全 Auto），预设多了以后「切范围、顶栏跳」
  频率上升。Linear / GitHub Insights / Vercel Analytics 的成熟形态是「当前档名按钮 + Flyout」。
- **统一模型是本卡的承重墙**：不给内置档加枚举成员、另为预设造一套结构，而是内置档与用户预设共用同一
  `RelativeRangeSpec`（Kind × Offset × Days 闭合词表）——持久化、相等性、Label、Display 单点维护。

## 设计定案（会话拍板）

- **RelativeRangeSpec 闭合词表**：`RollingDays(Days)` / `SingleDay(Offset)` / `Week(Offset)` / `Month(Offset)`；
  校验收口：RollingDays 2..365、SingleDay/Week/Month 偏移 -1..0（不做上上月/前天——封闭词表才能自动命名，
  两份英文字典才可维护）。**明确不做**：自由文本表达式、季度/年度档（Kind 留扩展位）、用户自定标签。
- **语义口径**：全部本地自然日含端点（同既有映射）；周锚点固定**周一**（ISO/中文习惯，opinionated）；
  终点：偏移 0 的未完周期收口今天，其余为该周期自然末日（上月=上月末、上周=上周日）；本月终点=今天且随日生长。
- **内置档 = 预定义 Spec 实例**：Today=SingleDay(0)、Yesterday=SingleDay(-1)、ThisMonth=Month(0)、
  LastMonth=Month(-1)、Last7/30/90=Rolling(7/30/90)；All/Custom 保持哨兵（不进 Spec 体系）。
- **Label 自动生成**：Spec → Translations 键（Month(0)→本月、Week(-1)→上周、Rolling(14)→最近 14 天），
  中文字典为源、英文覆盖；用户预设不让用户打名字（i18n 纪律：唯一出处 Translations.cs）。
  TimeRange7/30/90 三键退场，统一走 RollingDays 格式键（中英文与旧文案逐位相同）。
- **持久化（向后兼容零迁移）**：AppSettings.TimeRange 继续存选中项稳定 Id——内置沿用旧枚举名
  （"Today"/"Last7Days"/…老 settings.json 照常解析），新内置加 "Yesterday"/"ThisMonth"/"LastMonth"，
  预设为 "preset:<guid>"；指向已删除预设的选中值如实回落默认档。预设清单 = AppSettings 新字段
  CustomTimePresets（Kind 存字符串，同 Theme/Language 风格；未知 Kind 装载时如实跳过）。
- **上限与去重**：预设 ≤6 个；与内置档或既有预设 Spec 重复时拒绝（诚实报错，不静默）。
- **选项清单动态化**：TimeRangeOption.Options 静态清单 → FilterStateService 动态清单
  （UpdateTimeRangeOptions，同 UpdateAgentOptions 替换+通知套路），顺序：内置 8 档 → 用户预设 → 自定义。
  相等性键从枚举值换 Id；语言切换重译由 FilterStateService 的空串广播处逐选项 RefreshLabel
  （静态构造器订阅退场——预设实例有生命周期，不能进程级直订阅）。

## Acceptance Criteria

- [x] 内置三档（本月/上月/昨天）进下拉，GlobalFilterMapper 映射口径与滚动档同表（含跨月/跨年/闰二月边界）
- [x] 用户可添加/删除相对预设，校验（范围/重复/上限）诚实报错；重启后预设与选中档还原；删预设后选中值回落默认
- [x] 旧 settings.json（"Last7Days" 等旧枚举名）照常解析；FilterStatePersistence 全部既有用例语义不破
- [x] 顶栏时间范围 = 按钮 + Flyout：选档即查、自定义日期收进弹层、预设管理在弹层内，顶栏不 reflow
- [x] 键盘可达（Tab 遍历到范围按钮）；全部文案走 Translations（En↔Zh 键奇偶不破）；零硬编码色值
- [x] 全量 `dotnet test Tokenmetry.sln` 绿（1535 Passed / 0 Failed / 22 Skipped）

## Subtasks

- [x] Core：RelativeRangePresetSetting 投影模型 + AppSettings.CustomTimePresets
- [x] App：RelativeRangeSpec（FromDay/ToDay/IsValid/DedupKey/Label）+ 测试（周锚点周一、跨月、跨年、闰二月）
- [x] App：TimeRangeOption 改 Id/Spec 形状 + FilterStateService 动态清单（UpdateTimeRangeOptions）
- [x] App：GlobalFilterMapper 支持 Spec + internal today 注入 + 日历边界测试
- [x] App：FilterStatePersistence 新旧 Id 兼容解析 + 预设 Id 回落测试
- [x] App：TimeRangePresetService（装载/增删/校验/落盘）+ 测试
- [x] i18n：新增键（zh 源 + En 覆盖）+ 删 TimeRange7/30/90（文案由 TimeRangeRollingDays 格式键逐位承接）
- [x] UI：TimeRangePicker（Semi DropDownButton + Flyout）替换 ComboBox + inline DatePicker
- [x] UI：TimeRangePickerViewModel + TimeRangePresetService DI 接线（AppServices 装载先于 Apply）
- [x] Headless：AppShellTests 顶栏/键盘用例改写 + Flyout 端到端/预设服务/VM 新用例 + IconResourceTests 登记 x
- [x] 全量构建 + 测试 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- **统一模型落地形态**：`TimeRange` 枚举退场，`TimeRangeOption` 改 `Id + Spec`（相等性键换 Id）；内置档 =
  预定义 `RelativeRangeSpec` 实例（Today=SingleDay(0)、ThisMonth=Month(0)、Last7Days=Rolling(7)…），
  旧枚举名直接充当内置 Id——旧 settings.json 的 `"Last7Days"` 等选中值照常解析，**零迁移**。
  选项清单静态 → 动态走 `UpdateTimeRangeOptions`（同 UpdateAgentOptions 套路），选中按 Id 找回、
  预设被删回落默认档；语言切换重译从静态构造器进程订阅改为 FilterStateService 广播处对当前清单
  RefreshLabel（预设实例有生命周期，不能进程级直订阅）。
- **触发按钮选型**：Semi `DropDownButton`（模板自带 chevron + Flyout 支持）——免自绘 chevron、免再生
  Icons.axaml 的 chevron；Button.Flyout 的 light-dismiss 在首按外点时吞掉按压（Avalonia
  LightDismissOverlayLayer），不存在 ComboBox/ToggleButton 方案的「关了又被 Click 重开」双 Toggle 问题。
  删除按钮的 × 走生成器白名单新增 lucide "x"（AppIconClose），IconResourceTests 名单同步登记。
- **弹层结构**：档位 ListBox（内置 8 → 用户预设 → 自定义哨兵）+ 自定义日期区（仅自定义档显示，
  顶栏 inline DatePicker 退场 → reflow 消失）+ 我的预设管理区（行删除 + 展开式添加表单：
  种类下拉 → 周期偏移下拉 / 天数输入 → 错误就地显示）。FlyoutPresenter/Flyout 内容样式放
  SharedStyles（Popup 自成视觉根，控件级样式够不着）。
- **映射测试口径**：`GlobalFilterMapper.Build` 增 internal today 注入重载——日历档边界用例
  （跨年 12 月→1 月、闰二月、周锚点周一）钉死「今天」，不随运行日期漂移。
- **并发注记**：与另一会话（时间分析页 KPI 扩展，TimeViewModel/Translations/board.md 同文件）同工作区
  并行——中途全量构建曾命中对方半途状态（Translations 字典条目先落、属性声明未到，CS1061 一批），
  重跑即绿；两边键奇偶测试互相守护，最终全量 1535 Passed / 0 Failed（22 跳过，Q-014 基线 1483 → +52，
  两卡各约 26）。
- **测试踩坑留档**：① xUnit2031 不许 `Assert.Single(seq.Where(pred))`（用 `Assert.Single(seq, pred)`）；
  ② 「上周」用例首版预期手算错一周（2026-01-07 的上一周是 2025-12-29 起而非 12-22），实现正确、修预期；
  ③ VM 表单用例须先置 IsAddFormOpen=true 再验证「失败不收起」（默认关闭态断言恒假）。
- **已知边界（有意不做）**：周锚点固定周一（ISO/中文习惯，不随系统区域浮动）；预设上限 6、去重键
  （非滚动档忽略 Days）；上上月/前天等更远偏移不在词表（留自定义日期档）；「自定义」选中态下按钮
  文案不拼接日期区间（保持档名简洁，弹层内即可见）。
- 真机观感（Flyout 打开/明暗/顶栏不再 reflow）移交用户目检。

## 修正项 1（2026-10-25 真机目检）：触发按钮文字变蓝

- **现象**：用户目检反馈档名按钮文字是主色蓝，旁边 Agent/Model/Project 三个下拉仍是白（暗色主题）。
- **取证**：Semi `DropDownButton` 的 ControlTheme 基样式显式设 `Foreground = ButtonDefaultPrimaryForeground`
  （默认按钮变体的主色蓝文字）；相邻 ComboBox 的 ControlTheme 不设前景 → 继承全局默认文字色。
  headless 探针实测：ComboBox 继承前景 Light `#1C1F23` / Dark 近白，与 Semi 自有 token `SemiGrey9`
  两变体逐位相同（App 级可解析）；首版修法用的 `AppTextBrush`（Light `#1C2735`）与继承值不同值——
  回归锁（与相邻下拉颜色相等）如实拦下，换 `SemiGrey9` 后同源同值。
- **修法**：SharedStyles 增 `DropDownButton.time-range-trigger` → Foreground = `SemiGrey9`
  （引用 Semi 自有画刷、零新增令牌、明暗跟随，Q-014 同款先例）；chevron 随模板绑定
  ContentPresenter.Foreground 一同回位；hover/弹层仍走 Semi 按钮家族观感（不逐属性复刻 ComboBox）。
- **回归锁**：AppShellTests 顶栏用例断言触发按钮与相邻 Agent 下拉的前景**颜色值相等**（实例断言不可靠
  ——同名画刷经不同装载路径非同实例，Q-014 教训；颜色值比较跨明暗变体成立）。
- 全量 1536 Passed / 0 Failed（+1 即回归锁）；真机明暗观感移交用户。
