---
id: Q-014
title: ComboBox 焦点环网页化——指针焦点不画环、键盘焦点环保留（顶栏筛选选完蓝框常驻）
type: bug
priority: P2
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

顶栏四个筛选下拉（及其余所有 ComboBox）选完一项后不再常挂焦点蓝环：鼠标/指针来源的焦点与未聚焦观感一致，键盘（Tab/方向键）来源的焦点保留 Semi 焦点环——语义对齐 web 的 `:focus-visible`。

## Background

- 用户反馈（2026-10-25 会话，附截图）：下拉选中某个选项后整个框进入高亮态，点窗口其他部分高亮不消失，点左侧页签才退场——"这正常吗？"
- 取证：截图蓝框不是"选中"态而是**焦点环**——Semi `Controls/ComboBox.axaml` 的 `^:focus` 块无条件换 `ComboBoxSelectorFocusBackground/FocusBorderBrush`（主色描边）+ 模板箭头 `ComboBoxIconFocusForeground`，且 ControlTheme 设 `FocusAdorner={x:Null}`（环完全来自该伪类样式）。Avalonia 里选完下拉项焦点留在 ComboBox 上，而**点击不可聚焦区域不转移焦点**（主内容区全是 Border/TextBlock/图表），点中可聚焦元素（左侧页签 = `ListBoxItem`）焦点才走——与用户观察逐点闭环。
- 关键机制（Avalonia 12.1.4 `InputElement` 源码核实）：`:focus-visible` 伪类存在且按 `GotFocus` 的 `NavigationMethod` 判定（仅 Directional/Tab 点亮，Pointer/程序焦点熄灭）——web 语义现成可用，覆写样式即可，无需动焦点管线。
- 方案取舍：曾考虑"选完 ClearFocus"，但键盘流转起点会丢（焦点清空后 Tab 从窗口根部重来）且键盘/指针选完无法区分；`:focus-visible` 覆写零代码、两全。

## Acceptance Criteria

- [x] 指针焦点（含选完下拉后的焦点回归）ComboBox 观感与未聚焦基线一致——背景/描边/箭头三属性全复位
- [x] 键盘焦点（Tab/方向键）Semi 焦点环原样保留（背景/描边/箭头均为焦点态配色），可达性无损
- [x] 零新增令牌（全引用 Semi 自有画刷，DesignTokenTests 撞名白名单不膨胀），明暗自动跟随
- [x] `:error` 数据校验态不参与复位（校验描边优先于焦点复位）
- [x] 全量 `dotnet test Tokenmetry.sln` 绿（1483 Passed / 0 Failed / 22 Skipped，+2）

## Subtasks

- [x] SharedStyles.axaml 增 `ComboBox:focus:not(:focus-visible):not(:error)` 覆写（属性复位 + 模板 `PathIcon#DropDownGlyph` 前景复位，flat 选择器同文件既有惯例）
- [x] headless 契约测试 `ComboBoxFocusRingTests`（TDD：指针用例 RED 蓝环实锤 → GREEN）
- [x] 全量测试 + 卡片移 Done

## Dev Log（2026-10-25 收口）

- **测试断言策略踩坑（两连）**：① Semi 部分画刷（`ComboBoxIconFocusForeground`）只活在 ControlTheme 作用域里，控件树的 `TryFindResource`/`FindResource` 任何变体都查不到（而 `ComboBoxSelector*Brush` 又查得到——Semi 键位摆放不一致），逐资源键取期望值不可靠；② 同名画刷经样式 DynamicResource 与 FindResource 两条路径解析出的实例不同（主题词典按变体装载），实例断言也不可靠。最终契约面 = **同窗双 ComboBox 基线对照**（A 获焦 vs B 未聚焦）：指针焦点三属性 ≡ 基线（环退场），键盘焦点三属性 ≠ 基线（环保留）——不耦合查找 API 与具体色值，跨明暗变体成立。headless 宿主实际跑在 Light 变体（系统默认），此写法两主题通吃。
- **已知边界（有意不做）**：键盘提交选择后环同样退场——弹层收起的焦点回归不携带键盘模态（非 Directional/Tab），焦点仍在 ComboBox 上、Tab 流转与方向键重开下拉不受影响，损失的只是"提交后到下一次 Tab 之间"的环；保留它需动核心焦点管线（Popup 收起的 Focus 恢复路径），不成比例。DatePicker 等 Semi 其它控件的同类焦点环不跟改（用户反馈仅涉及 ComboBox，等反馈再立卡）；Semi `.Bordered` 变体全仓未用，不处理。
- 全量 1483 Passed / 0 Failed / 22 Skipped（+2 即本卡两用例）；真机明暗观感移交用户。
