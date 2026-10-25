---
id: B-015
title: 计价页价格输入固定 InvariantCulture——de-DE 等逗号小数文化输入必被拒
type: bug
priority: P3
size: S
status: done
created: 2026-10-23
updated: 2026-10-25
blocked: none
---

## Goal

模型目录编辑弹层的价格输入接受本机文化的小数格式（`0,5`），存储口径保持 invariant 不变。

## Background

- 2026-10-23 扩大扫描（文化维度）发现：`DataPricingViewModel.cs:842` `decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, ...)`，回显 `:710-720` 同为 `ToString(CultureInfo.InvariantCulture)`。
- 机理：de-DE 用户按本机习惯输入 `0,5` → 解析失败显示"必须是数字"、不落盘。存储/解析层 invariant 本身没错（防串味），错在**用户输入面**只有机器口径一条路。
- 佐证：2026-10-23 以 tr-TR / de-DE 钉死全测试套件默认文化跑全量皆绿——因为测试喂的都是 "0.5" 形态，覆盖不到本机格式输入路径（产品缺口在输入面，测试面是解析面）。

## Acceptance Criteria

- [x] 输入解析双文化：**invariant Float（千分位排除）优先 + CurrentCulture Number 兜底**——偏离 AC 字面"先试 CurrentCulture"，定案理由见 Dev Log（本卡二选一已定案，且顺序倒置）
- [x] 存储与回显序列化保持 InvariantCulture（现状零改动：预填与列表价串本就恒 invariant；新增 de-DE 宿主预填往返回归锁钉住）
- [x] 回归锁：CurrentCulture=de-DE 下 `0,5` 可录入且等值 0.5；全量测试绿

## Dependencies

- none

## Development Log

### 2026-10-23

- 建卡（扩大扫描发现；"接受本机格式"还是"提示用户用点号"是 UX 决策，先记录实证）。

### 2026-10-25（实施收口）

- **取证修正（RED 实测推翻建卡背景）**：旧实现对 `0,5` 不是"解析失败显示必须是数字"，而是**静默千倍错标**——.NET 解析不校验千分位组大小，invariant Number 趟把 `0,5` 读成 5、`0,047` 读成 47；RED 三处实证（`0,5`→5 / `0,047`→47 / `1.234,5`→拒）。比建卡描述更严重：不打错值无提示。
- **定案（偏离 AC 字面顺序，理由留痕）**：两趟解析——第一趟 InvariantCulture + `NumberStyles.Float`（千分位排除），第二趟回落 CurrentCulture + Number。决定性理由是**回显闭环**：编辑弹层预填与列表价串恒 `ToString(InvariantCulture)`，de-DE 宿主上若按 AC 字面"CurrentCulture 优先"，元数据级保存就会把预填 `0.047` 按千分位偷换成 47；故点号串必须全局同义，本机形态走兜底。第一趟排除千分位还有一层收益：堵住 `0,075` 在 invariant 侧被读成 75 的对称坑（本机三位小数由兜底趟按小数读）。
- **已知边界（卡内登记）**：de-DE 宿主上点号千分位串 `1.234` 读作小数 1.234 而非本机千分位 1234——计价输入量级（每百万单价 ≤ 数百）不出现真千分位，接受该歧义。
- **测试 +7**：de-DE 宿主双分隔符 Theory 五形态（`0,5` / `0,047` / `0.5` / `0.047` / `1.234,5`）+ 编辑预填 de-DE 往返锁 + 负数/垃圾拒绝锁；`WithCurrentCulture` 临时钉文化 finally 还原（套件进程级串行，AssemblySettings DisableTestParallelization，无并行读文化的漂移窗口）。App.Tests 265（+7）；全量 1561 Passed / 0 Failed（19 跳过 = Reconciliation RealData 按本机 fixture 存在性跳过）。真机 de-DE/tr-TR 宿主机观感移交用户（测试只锁文化解析逻辑，不切宿主）。
- **明确不做**：存储/回显序列化不动（恒 invariant 现状即正确）；不加 UI 格式提示文案（双趟解析后无需教育用户）；不做完整本地化数字输入面（exponent/原生数字字形等兜底趟顺带能力，不单独立卡）。
