---
id: I-040
title: 界面图标矢量化——lucide 生成 AppIcon* 替换全部 Unicode 字形图标
type: feature
priority: P2
size: M
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

应用内所有 UI chrome 图标（顶栏刷新/设置、侧栏九页导航、品牌标、总览/样式预览 KPI chip、
托盘弹窗设置按钮）从 Unicode 字形文本换成矢量几何（`Styles/Icons.axaml` 的 `AppIcon*`
StreamGeometry，lucide 生成），尺寸跨平台稳定、随明暗翻转。

## Background（取证）

用户反馈"右上角设置/刷新按钮里的 Icon 都好小"。取证结论（纯 Python 解析系统字体 cmap/glyf）：

- 图标是文本字形（`⟳` U+27F3 / `⚙` U+2699，FontSize 14）放在 34×34 按钮里。
- 应用未激活任何 FontFamily（csproj 的 Avalonia.Fonts.Inter 为死引用），macOS 主字体 = SF Pro，
  **不含这两个码点**；全系统唯一同时覆盖的是 Apple Symbols.ttf → 两字形都回退到它。
- Apple Symbols 中字形墨迹只占 em 四到五成：⚙ ≈ 0.55em（14pt 下 7.6px）、⟳ ≈ 0.43em 高
  （6.1px）——34px 按钮里只有大写字母高的图形，视觉必然过小。
- 原型 HTML 同样结构（13px 字形 + 同一回退链），属"忠实复刻了原型的小图标"。
- 同类问题已有前科：overlay-spinner 当初放弃字形改矢量（MainWindow 注释）；Windows/Linux
  回退字体（Segoe UI Symbol / DejaVu）墨迹率不同，调 FontSize 无法跨平台修。

## 决策

1. **几何源**：lucide（ISC）`lucide-static@1.61.0`，stroke 语义 24×24；新脚本
   `scripts/generate-ui-icons.py`（同 generate-brand-logos.py 纪律：pin 版本、白名单逐项
   声明、不支持结构整体失败）一次生成 `Styles/Icons.axaml`。
2. **渲染**：`Path.app-icon`（SharedStyles）统一描边——Stretch=Uniform + StrokeThickness 1.5
   （Avalonia `Shape.Render` 对变换后几何直接套 pen，粗细**不随** Stretch 缩放，按渲染槽
   定值）+ 圆角端点；颜色一律令牌画刷（默认 AppTextBrush，状态色在消费视图样式给）。
3. **导航接线**：`ShellPages.Icon` 语义改为图标键名（house/table/bot/cpu/folder/clock/
   dollar-circle/database/settings），`Controls/PageIcon.Icon` 附加属性翻译成 pi-* 伪类 →
   SharedStyles 给 Data（同 AgentChip/ModelChip 机制；DynamicResource 不支持动态键名）。
4. **不替换**：agent/model 厂商兜底散列字形（§8 既有决策，数据语义非 chrome）、
   ModelLogos/AgentLogos（彩色官方标）、KPI 涨跌 ↑↓ 与文本连接符 →（文本语义）。

## 改动清单

- 新增：`scripts/generate-ui-icons.py`、`Styles/Icons.axaml`（14 键）、`Controls/PageIcon.cs`、
  `IconResourceTests.cs`（4 条：键名↔资源不漂移 / 两主题同实例 / app-icon 样式应用 / 附加属性解析）。
- 修改：App.axaml（注册）、SharedStyles（app-icon + pi-* + icon-chip Path 子选择器）、
  MainWindow（顶栏两按钮、品牌标、导航模板、禁用/导航状态色、refresh-running-glyph 改挂 Path）、
  NavigationItemViewModel（ShellPages 键名）、OverviewView / StylePreviewView（KPI chip）、
  StatusPopoverView（弹窗设置按钮 + 样式）、DesignTokenTests（AllTokenKeys 撞名白名单 +14）。
- 文档：design-tokens.md 新增 §10。

## 验收

- headless 全量 349 Passed（含 4 条新守卫）；App.Tests 208 Passed。
- 名单同步点固化：generate-ui-icons.py ICONS / ShellPages / IconResourceTests.GeometryKeys /
  DesignTokenTests.AllTokenKeys。
- 真机明暗 × 1x/2x 目检移交用户（图标视觉尺寸 / 描边粗细手感）。

## 修正项 1——KPI 成本卡图标换带圈版本（2026-10-24 目检反馈）

用户真机目检：总览「估算成本」卡的美元符号"是歪的"。像素级取证（逐行解码用户截图 vs
headless 1:1 复刻，行轮廓逐像素一致）：渲染无任何倾斜，竖杆垂直、端子水平——「歪」是
lucide 裸 dollar-sign 的字形设计在 15px 下的观感问题（竖杆上下长伸、横端子两侧大敞）。
处理：KPI 成本卡（总览 + 样式预览）换 DollarCircle（带圈版本，与导航成本图标一致，
用户目检已认可），裸 dollar-sign 从生成器名单退场（13 键），文档 §10 同步。headless 351
Passed（含并行的 Q-006 契约测试）。

## 修正项 2——渲染控件换 PathIcon，修墨迹不居中（2026-10-24 目检反馈）

用户目检：缓存读取占比卡的 zap 闪电"没有居中"，并指出与美元符号是同一问题——判断正确，
且根因挖到了底：Avalonia `Shape.CalculateSizeAndTransform` 只做"缩放适配 + 平移到原点"，
**没有 WPF 的居中项**——`Stretch=Uniform` 的墨迹被钉在元素左上角，槽内偏移 = (槽−墨)/2：
$ 墨迹 12×20 左偏 3px、zap 约 17×20 左偏 1.2px、近方形墨迹（table/coins）偏移≈0——
解释了为什么只有部分图标看着不对（$ 的像素取证当时已量到 -2.5px，被当噪声放过）。

处理：渲染控件从裸 Path 换 **PathIcon**（隐式 ControlTheme 进 Icons.axaml，随生成器再生）：
内层 Path 自适应墨迹尺寸 + Center 对齐 → 墨迹恒居中于定尺寸槽位；描边色改走 Foreground
模板绑定（导航选中/按钮禁用随祖先 Foreground 继承生效，删掉逐态描边样式；顶栏给
AppTextBrush 显式基线）。IconResourceTests 新增居中回归锁（zap/house/loader-circle/coins
墨迹中心 = 槽位中心 ±0.75）；像素复测 zap：水平偏差 0、垂直 0.5px（AA 噪声）。全量
1454 Passed / 0 Failed。

## 修正项 3——品牌标示换定稿应用图标本体（2026-10-24 目检反馈）

用户目检：侧栏头部「圆环 + 内弧」标示"是个啥、怪怪的"。取证：那不是任何定稿设计——
D-018 已两轮筛选定稿绿柱 v1 应用图标（app-icon.png 居中 Apple 网格变体 / app-icon-window.png
窗口出血变体），侧栏圈与弹窗「T 字芯片」均为 D-018 之前的占位。

处理：侧栏头部与弹窗头部品牌位改 `Image` 引用 `Assets/app-icon.png`（24/28px，自带圆角与
边距）；弹窗「T 字芯片」的 AppPopoverLogoBackgroundBrush 渐变令牌随之退场（Tokens +
popover-logo 样式；AppOnBrandForegroundBrush 保留——primary 按钮仍用）。headless 探针渲染
两处品牌位目检通过；全量 1454 Passed / 0 Failed。
