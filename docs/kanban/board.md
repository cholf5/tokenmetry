# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

> 2026-10-11 迁移说明：按 markdown-kanban 规程，旧 7 列看板收敛为 Todo | Done。原 In Review 的 7 张 v0.1/v0.2 卡片经 git 记录核实（功能均已提交、卡片内有测试通过记录）后记为 Done，卡片文件移入 `issues/archive/`（保留旧格式）。产品定位转向见 `docs/plans/2026-10-11-opinionated-usage-stats-pivot.md`。

> 2026-10-28：**D-026 Windows 原生标题栏深色适配 + 窗口标题缩短收口**。用户反馈（2026-10-27 截图）Windows 深色界面顶一条纯白系统标题栏。取证：经典 Win32 caption 不跟随应用主题，需应用经 DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`（属性 20，Win10 2004+；1809–1903 回落 19）主动声明。修法：新增 Windows-only `WindowsCaptionTheme`（`Attach` 挂 `Opened` + `ActualThemeVariantChanged`，Dark→1 / Light→显式清零防残留；非 Windows / `TOKENMETRY_HEADLESS` / 空句柄在触达 DWM 前返回——headless 句柄非真 HWND，B2 双轨纪律同 StatusPopoverWindow DWM 圆角先例），主窗 / 首关提示窗 / 启动错误窗三处接线共用；标题缩短为 "Tokenmetry"（副标题在 caption / 任务栏重复侧栏品牌）。明确不做：自绘标题栏与 `DWMWA_CAPTION_COLOR`（D-003 系统边框决策不变；后者压掉 active/inactive 与高对比度适配）。TDD（CS0246 RED → 6 用例 GREEN）+ headless 398 全绿守卫零 P/Invoke。真机验证（本机 Win10 19045，系统深色）：DWM 属性 20 回读 = 1（hr=0x0）、`GetWindowText` = "Tokenmetry"、caption 截图深色与顶栏无缝（截图存会话临时目录不入库）。计划两处偏差已登记卡内：测试局部元组补具名（计划代码 CS1061 编译错）；ThemeVariant 实为 record（== 与 Equals 同语义），实现注释改为陈述真实约束。运行期明暗双向切换、边框原生行为、首关提示窗 caption 目检移交用户。

> 2026-10-27：**B-022 托盘弹窗首显闪动收口（B-021 修正项 1）**。用户反馈 B-021 后首次打开快捷窗「闪一下」：`Show()` 先显示空态窗口（headless 实测 420 DIP）再发查询，数据回填后 SizeToContent 撑高（1 Agent + 1 模型 543 / 满载 719 DIP）；B-021 的底缘锚定把这段增长变成可见的整体上跳（123/299 DIP），占位「—」同时被真实数据替换。二次打开不闪（VM 已持数据）。修法走启动预热：`StatusPopoverController.WarmUpAsync()` 显示前只读装数（不建窗/不置可见态/可重复），App 启动立即装一次 + 首轮采集结束后再装一次（覆盖老用户库内已有数据与首次使用首轮才入库两种形态），失败只退回首显重查。headless 回归锁 +1（GateEnabled 先关后开，断言预热后首帧高度 == 放行后高度；预热空操作实测 RED → GREEN）。全量 Core 189 / Reconciliation 49（19 跳过）/ Infra 681 / Headless 397 / App.Tests 306 全绿。真机首显观感移交用户；若仍有横向抖动，归首显毛玻璃 498→438 宽度切换（本卡未动）。

> 2026-10-27：**B-021 托盘弹窗首显仍偏下收口（B-016 修正项 1）**。用户反馈 B-016 修复后首显位置仍偏下（截图中 footer 被屏幕底缘裁掉），收起再点第二次就正常。取证：B-016 只修了「首显取陈旧 Bounds 高度」，漏了「显示后内容高度继续增长」——`Show()` 一次性锚定时 VM 还是空态（headless 实测 ClientSize 420 DIP），查询在 Show 返回后才回填，`SizeToContent="Height"` 窗口只向下长（满载 719 DIP），位置不再更新 → 底缘越过任务栏；二次 Show 时数据已就绪故正常。修法：Show 时捕获本次点击光标为首锚（每次重取，重锚复用不跟当前光标跑），壳窗显示期 `SizeChanged` 按同锚重算位置——Windows 底缘钉在光标上方，macOS/回落顶缘锚定天然幂等；隐藏期尺寸变化不处理。headless 回归锁 +1（GatedQueryService 挂起查询复刻异步回填 + 注入确定锚点；去掉接线实测 RED 底缘漂 123 DIP → GREEN）；`PopoverPositioner` 零改动。全量 Core 189 / Reconciliation 49（19 跳过）/ Infra 681 / Headless 398 / App.Tests 306 全绿（App.Tests 首轮 1 失败 = `SingleInstanceGuardTests` 既有 B-020 环境性 flake，单类与重跑均过）。真机 Windows 首显目检移交用户。

> 2026-10-26：**D-018 修正项 1——Windows 标题栏图标只显示一半收口**。用户 Windows 真机反馈标题栏图标只见半张。取证（纯 Mac 可完成）：`app-icon-window.png` 本身是裁切图——alpha 解码不透明 bbox 仅 (122,0,255,255)（左 122px 全透明、右/上/下贴死画布边），标题栏只是忠实显示；ico 7 帧阶梯（16–256）与 1024 Apple 网格主图均完整且不经 Window.Icon（exe/Dock 免疫），故只有标题栏暴露。病根：该资产当日"Chrome 直出 256"系烘焙管线外手工产物（make-icon.py 的 full-bleed 自检只覆盖 ico/png），自 8d2ab6f 落地起即坏。修法：ico 内 256 帧全出血渲染原样覆盖（与 exe 图标像素级同源，代码零改动）；headless 回归锁 +1（alpha bbox 必须铺满画布，RED 实锤 minX=122 → GREEN）。全量 Core 189 / Reconciliation 49 / Infra 678 / Headless 397 全绿；App.Tests 4 失败均为 SingleInstanceGuardTests——本机 TMPDIR 路径超 Unix 域套接字 104 字符上限（stash 干净工作区复现同款，B-020 同族环境性，与图标资产零交集）。真机 Windows 标题栏目检移交用户。

> 2026-10-26（D-023 回退）：**Windows 托盘双击唤出主窗——真机翻车同日回退，卡片退回 Todo**。用户 Windows 真机实测反馈「体验极差，交互上会和单击 Switch 开关冲突」→ revert 7416e82 整体回退：`OnTrayClicked` 双击时间窗拦截 / `HideIfVisible` / `GetDoubleClickTime` P/Invoke / 三 internal 注入点 / +3 headless 用例与既有单击用例假时钟改造全撤，不留死代码，单击分派恢复 B-017 语义原样。冲突机理：B-017 收口后托盘左键单击是 Switch（弹层开着点=收层）——弹层可见时快速第二击本意收层，却被时间窗判成双击直接唤出主窗；两次 WM_LBUTTONUP `Clicked` 流上的时间窗判定无法区分「快速连击收层」与「双击开窗」，方案级冲突调参不可救。另澄清：收口注记引用的设计文档 `docs/plans/2026-10-26-tray-double-click-design.md` 从未入库（悬空引用）。后续出路（彻底放弃 / 另寻双击检测手段）待用户决策。

> 2026-10-26：**Mac 体验调查——B-018 收口 / B-019、B-020 建卡**。用户报告三症状：①关主窗后 Dock 图标仍在且亮灯（**非 bug**：package.sh:110 既定决策"普通应用不设 LSUIElement，Dock 图标是产品语义"，本次会话用户再次拍板维持）②点 Dock 图标无反应——根因：macOS 惯例经 `applicationShouldHandleReopen` 响应"点 Dock 无可见窗"，Avalonia 12.1.4 的 Avalonia.Native 不实现该方法（旁证上游 #20683 dock Quit 不可取消）→ **B-018 同日收口**：`MacTrayInterop.TryInstallDockReopen`（class_addMethod 挂委托类，add 失败=他人已实现则降级）+ App 接线直调 `trayService.ShowWindow`（B-013 同语义）；Core 189 / Reconciliation 49 / Infra 681 / Headless 399 全绿，真机 TC 四项移交用户；③Dock 右键「显示所有窗口」崩溃——环境 macOS 26 Tahoe，上游家族 #19028/#19788 均非精确匹配，假设为窗口概览层枚举全隐藏（orderOut）窗口时崩，**B-019 blocked 待用户取回 ~/Library/Logs/DiagnosticReports/Tokenmetry-\*.ips**。临时解法已存在：双击 .app 二开经单实例管道唤回。**B-020 顺手立卡**：调查③时 App.Tests 又见 `SingleInstanceGuardTests` flake，本次钉死两条证据——测试名 Guid 隔离与真 App 进程无关；失败唯一产出路径是 `TryAcquire` 的 AbandonedMutexException 分支（second 看到被遗弃互斥体 → 持有者句柄被提前关闭或线程退出），疑 GC/线程退出时序，查清前各卡验证遇此失败按 B-020 豁免。

> 2026-10-26：**D-023 Windows 托盘双击唤出主窗收口**。用户提案"双击托盘图标显示主窗口合理吗"→ brainstorm 评估拍板：双击开主程序是 Windows 老牌托盘惯例，与 D-016 单击弹层构成「单击瞄一眼 / 双击看全貌」OneDrive 式分层互补；**平台仅 Windows**（macOS 菜单栏无双击习惯且 interop 左键不经 OnTrayClicked，Linux 各 DE 行为不一）。技术事实：Avalonia TrayIcon 无 DoubleClick 事件、Win32 双击 = 两次 Clicked——`OnTrayClicked` 前置时间窗拦截（默认 `GetDoubleClickTime()` 尊重系统设置），第二击不 Toggle 改收弹层（仅可见时，防 OnHidden 重复触发）+ ShowWindow；双击消费制清时间戳（三击回单击语义）；与 B-017 失焦守卫互不干扰（守卫只作用第一击 Toggle，计时独立）。三注入点（窗长/时钟/门控）保 headless 确定性（任意宿主平台可测），单击零新增延迟（延迟单击判定明确否决）。TDD（RED 8 处编译失败实锤 → GREEN），测试 +3 + 既有单击用例改假时钟显式化"非双击独立单击"，Headless 399 全绿；App.Tests 266 重跑全绿（全量并发轮 1 处 SingleInstanceGuard 环境性失败同 B-017 家族）。设计全文 `docs/plans/2026-10-26-tray-double-click-design.md`。真机双击手感移交用户。**（同日真机翻车，回退见顶部注记）**

> 2026-10-26：**B-017 Windows 托盘左键永远 Open 不 Switch 收口**。用户报告"Windows 每次左键点托盘都是 Open，弹窗开着点击也不收；macOS 是正常 Switch"。根因是两条收起/弹出通道的时序竞争：点托盘图标系统在按下瞬间把焦点交给任务栏，弹窗 `Deactivated → 失焦收起` 先发生，`TrayIcon.Clicked` 等 WM_LBUTTONUP（抬起）才 raise——`Toggle()` 看到的已是"关着"，照常 Show 重新弹出，净效果左键永远只会 Open（开着时表现为收起即再弹的闪烁）。macOS interop 左键直回调无此时序分叉故免疫。修法：失焦收起接线从窗口构造移入 `StatusPopoverController`（顺带补上窗口自接线漏掉的 VM.OnHidden），`Toggle` 拆双入口——`ToggleFromTrayClick`（托盘左键专用，前置 400ms 失焦守卫：刚失焦收起视为同一次点击的收起意图维持隐藏，守卫消费制连点可再开）/ `Toggle`（托盘右键菜单项等显式入口不带守卫，打开菜单本身会抢焦点）。测试 +2（headless 经 internal 处理器模拟 Win32 先失焦后 Clicked 时序），Core 189 / Reconciliation 49 / Infra 681 / Headless 396 全绿；App.Tests 265 + 1 失败（SingleInstanceGuardTests）经 git stash 干净工作区复现同款、单类过滤必过——环境性既有问题与本卡零交集（同 2026-10-25 深夜八注记家族），留待独立排查。真机托盘连点目检移交用户。

> 2026-10-25（夜·内存批次）：**内存调查批次——S-007 / P-003 / I-042 同日收口 + I-043 设计卡建卡**。起点是用户反馈"启动后内存几百 MB、没选 Electron 选 Avalonia 就图个省资源"：实机测量（Release 时间线 + dotnet-gcdump + 最小 Avalonia 12 对照应用）拆出构成——稳态 Private ~230-280 MB 而托管堆仅 35 MB，大头是 .NET 运行时/JIT/原生渲染栈（裸 Avalonia ~60 MB、+Semi ~100 MB 的框架地板，igc64/ANGLE/IME 注入占 WS 大头），数据侧问题在采集管线（7 个 JSONL 扫描器全历史驻留 + 每轮全量重建 + 默认 1 分钟空转）。三卡：**S-007** 刷新默认 1→5 分钟（8ab392c；Core.Tests 两处断言追补 e55fc26）；**P-003** 发布产物 PublishReadyToRun（6c545fb；JIT 代码堆缩小/启动提速，包体积 +30-50% 已登记，本地 win-x64 R2R 冒烟通过）；**I-042 无变化轮次短路**——`JsonlScanDiagnostics.FilesChanged: int?`（null=不追踪保守全量）+ 扫描器零变更轮缓存去重产物（不再全历史 SelectMany+去重重跑）+ Copilot 零变更结果缓存 + ZCode count/max(id) 变更探针 + 数据源全源无变化时跳过计价/聚合/落库（facts 空则落穿自愈）+ `IAggregationInvalidator` 一次性强全量旗标接 `TriggerReprice`（I-037 改价格即生效在短路下的新编码，`GetDailyUsage_RepricesRecordsWhenOverridesChange` 迁移到显式失效语义）。测试 +10，全量 1571 Passed / 0 Failed（19 跳过）。I-043（message-grain 记录账本，驻留内存 O(全历史)→O(本轮增量) 的深水区改造）建卡待排期。**并发注记**：与 B-016 会话同工作区并行（其"深夜八"注记所查的两处 App.Tests 单例失败即本批次并行互踩，Core.Tests 欠账已由 e55fc26 收口）；本批次与 B-016 文件面零交集。

> 2026-10-25（深夜八）：**B-016 Windows 托盘弹窗首显位置修正收口**。用户报告"Windows 首次点托盘，弹窗出现在屏幕中间偏右，多点几次才对；macOS 无此问题"。取证（Avalonia 12.1.4 源码 ShowCore 时序 + %TEMP% 最小复现程序 + 真实应用临时打点实测，打点已还原）：弹窗 HWND 以 CW_USEDEFAULT 创建（本机解析出 ≈720 DIP 高默认矩形），`SizeToContent=Height` 的实测高度在 `Show()` 返回时已进 `ClientSize`（438×376）而 `Bounds` 高度要等首个布局 tick 才回写（此刻仍 438×720）——首显锚定按陈旧 Bounds 算出 y=290（应 634），窗口落屏幕中间偏右；二次起 Bounds 正确即恢复。macOS 免疫原因：`AnchorBelow` 以图标 frame 为顶锚，窗口高度只参与越界钳制、不移动可见卡片，与用户观察吻合。修法一行：`ScreenSize` 的 `window.Bounds.Size` → `window.ClientSize`（无边框窗口 frame=0，非首显语义等价；`Show()` 返回时即正确），`PopoverPositioner` 纯函数零改动。真机首显留证 `target=(1172,634)`、`GetWindowRect=(1172,634)-(1610,1008)`——底缘贴光标上方 12px、右缘贴托盘。本卡相关面 App.Tests 265 / Headless 386 / Infra 672 全绿。**并发注记**：与 S-007（8ab392c）/ I-042 / I-043 会话同工作区并行——其间两次 App.Tests 全量单例失败（SingleInstanceGuard / JsonAppSettingsStore）经查属并行互踩非 flaky（后者已被 8ab392c 同步收口）；Core.Tests 现 2 失败（AppSettingsTests/DashboardSummaryTests 仍断言 RefreshMinutes=1）属该会话默认值改动的测试同步欠账，与本卡无交集，本卡经选择性暂存分账（App/Tray 一文件 + 看板两件）。

> 2026-10-25（夜）：**D-022 官网功能页富数据截图套件收口**。官网功能页配图此前是隔离空库的空态渲染（"—" KPI），不可用。本卡把 D-021 的「裸窗 + StubQueryService」stub 留档模式产品化到全部对外页面：新增 `SiteShotsTests`（自包含，零产品 src 改动），受控演示数据（7 天 36.44M tokens / $81.22 / 214 会话，五分量 12/5/5/78，kimi-k2.5 缺价走「未知」）× zh/en × 明暗，覆盖会话/模型/时间/成本/项目/Agent/数据与计价 7 页 + 托盘弹窗，命名 `{view}-{locale}-{theme}.png` 直配站点仓库。fixture 锚定真实今天（相对时间列/默认 7 天档/日历热图要活数据——这也是此前站点托盘图输入/输出/缓存三卡恒 "—" 的根因：fixture 日期非今天）。踩坑两处：① 托盘弹窗独立 TopLevel 不继承 owner 的 RequestedThemeVariant，不设则明暗两张同字节（md5 相等实锤），修法主题直接设弹层窗体；② 弹层窗体四周透明落白成白边，PIL 双阈值（18/120）量出卡片实体边 39,23,420×546，测试内 CopyPixels→WriteableBitmap 安全裁剪后留档。数据与计价页 8 源状态丰富（Ok×5/Partial/NoData/已注册未扫描）。断言语言无关（结构谓词），语言循环 finally 恢复单例基线。8 测试全绿（3s），32 张 PNG 目检（密度/主题/裁剪）通过并入站点仓库。明确不做：整窗带侧栏富数据渲染（需库喂数或 DI 覆写缝，另立卡）；截图 fixture 不进任何功能断言。

> 2026-10-25（深夜七）：**B-015 价格输入文化收口（深夜六清账后唯一存卡，本卡清完 Todo 归零）**。建卡背景"de-DE 输入必被拒"经 RED 实测修正：旧 invariant Number 趟对 `0,5` 不拒而**静默千倍错标**（.NET 解析不校验千分位组大小：`0,5`→5、`0,047`→47），比建卡描述更严重。定案偏离 AC 字面"CurrentCulture 优先"：**invariant Float（千分位排除）优先 + CurrentCulture Number 兜底**——决定性理由是回显闭环（编辑弹层预填/列表价串恒 invariant ToString，de-DE 宿主本机优先会把预填 `0.047` 当千分位偷换成 47）；兜底趟承接 `0,5` / `0,047` / `1.234,5` 本机形态，第一趟排除千分位同时堵住 `0,075` 在 invariant 侧读成 75 的对称坑。已知边界：de-DE 宿主 `1.234` 读作小数（点号全局同义优先于本机千分位，计价量级无真千分位）。测试 +7（双分隔符 Theory 五形态 + 预填 de-DE 往返锁 + 负数/垃圾拒绝锁；WithCurrentCulture 钉文化 finally 还原，套件进程级串行无漂移），App.Tests 265（+7），全量 1561 Passed / 0 Failed（19 跳过）。

> 2026-10-25（深夜六）：**看板 Todo 清账**。全库 97 张卡 status 与看板双向扫描，错位两处清零。**D-017**（右栏撤除 + 顶栏四维全局筛选 + 会话行元数据升级）属"先实施后补卡、补卡后忘关"：六笔实现提交（fe78b8d / d55f722 / 10b2878 / 429a2e3 / b686e23 / fe530b2）全落 2026-10-18 且早于补卡提交 520738b，卡内 Dev Log 有 1359 全绿验证记录；代码面复核右栏零残留（`SessionsPanelBorder` 全库无命中、AppServices 仅存删除说明注释）、`UsageQueryFilter.Model/Project` 在位——记 Done（2026-10-18）。**P-002** 卡片 status 早已 done（实现提交 b987e4c 等在 main，715a9d0 曾正式移入 Done），唯看板行成了无表头的孤散行挂在 Done 表头之上——归位 Done 表（2026-10-23）。Todo 清完仅存 B-015（计价页价格输入文化，2026-10-23 建卡未动工）。

> 2026-10-25（夜）：**B-010~B-013 Windows 平台缺陷批次同日清账 + B-014 关卡**（2026-10-23 Windows 冒烟扩大扫描产出的五张卡全部出清，B-015 计价页文化区域属跨平台问题不属本批）。**B-010（P1）**：三处日志读取入口（DSH OpenLogReader / 扫描器默认 reader 工厂 / Copilot ParseFileAsync）统一走新增 `Jsonl/AgentLogReader`（`FileShare.ReadWrite|Delete` 单点）——Windows CreateFile 共享规则下旧 FileShare.Read 对 agent 写句柄即 sharing violation，被 catch 降级 skip 后表现为写句柄保持期间该会话持续采不到（Unix 空操作故 macOS 永不复现）；本机真跑 TDD：持真实写句柄走生产读路径 RED 三处全红、修后 4/4 绿。**B-011（P2）**：Dsh/Copilot 两处目录递归补 ReparsePoint 跳过（本就是相对共享收集器的移植偏差）——junction 成环旧实现无限递归 StackOverflow 进程死、不成环时 DSH 无消息级去重直接双计；真 junction 实跑 RED 双计实锤，修后 3/3 绿（含 sessions 根自指环），测试 Dispose 递归删除 catch 顺手补 UnauthorizedAccessException（.NET 会探入 junction）。**B-012（P3）**：Claude/Codebuff 两处 "projects" 文件名判定改 OrdinalIgnoreCase（文件系统大小写不敏感而比较是 Ordinal → 配置根被拒收、源静默空）；RED 双实锤（Claude 候选返 null / Codebuff records 空）后修平，Codebuff 类型注释登记对蓝本的有意偏离。**B-013（P3）**：单实例守卫落定 `App/Services/SingleInstanceGuard`——命名 Mutex 独占（.NET 10 三平台原生，免掉卡面括注的 Linux 文件锁分支）+ 命名管道激活通道；UX 卡内定案"激活既有窗口"（托盘常驻应用双开最常见诉求是把界面叫出来），第二实例在 Program.Main 最早期（DI/SQLite 前）拿不到锁→发 `show` 请求→静默退出，首实例经 Dispatcher.Post(trayService.ShowWindow) 唤回主窗（ShowWindow 提 internal 与托盘菜单共用语义）；headless 宿主 TOKENMETRY_HEADLESS 整体旁路。**Windows 实机双开验证**：--autostart 静默首实例 + 裸启第二实例 → 1.0s 码 0 退出、存活恰 1 进程；macOS/Linux 实机双开移交用户。踩坑两枚留痕：Mutex 线程递归（同线程重复 WaitOne 重入成功，守卫用例须跨线程断言）；.NET 递归删除会探入 junction 触发拒绝访问（fixture Dispose catch 相应加宽）。B-014 随 Q-016 失效关卡：异常 toast 通道已整体删除，唯一出口（弹层/头部 KPI 徽标）与主窗可见性无关，问题前提不复存在。全量 1554 Passed / 0 Failed（19 跳过 = Reconciliation RealData 用例按本机 fixture 存在性跳过；+14 = 4+3+2+5）；单卡提交 9e538a8 / cf7a9aa / 74b27eb / e7a13c3。

> 2026-10-25（深夜五）：**Q-018 徽章 tooltip 橙字漏染 + Semi 默认底错配修复收口**。用户目检（明暗双截图）：头部异常徽章 hover 提示两主题均橙字——暗色浅底橙字不可读、明色深底橙字可读但离主题色，问是否故意。取证：不是，两因叠加——① ToolTip 在逻辑树上是挂载目标的子节点，Q-011 写下的嵌套样式 `Border.popover-cost-warn-badge TextBlock`（后代选择器）把 tooltip 内容 TextBlock 一并染橙/压 10px；② 底色走 Semi 12.1 默认 ToolTip 配色（B-006 已证错配：Light 浅底白字 / Dark 深底黑字，图表侧当年绕行）。修法：tooltip 显式挂 `chart-tip` 令牌底（chip 底 + 卡片描边 + AppTextBrush，与图表悬浮面同源）；徽章文本改自类 `popover-cost-warn-text`（后代选择器撤除，漏染免疫），本体橙 10px SemiBold 不变。踩坑留痕：中间误判「`>` 子选择器不命中」——实为单参 FindResource 不解 ThemeDictionaries（橙令牌在主题字典），三参 TryFindResource 才对，Q-014「FindResource 两路径非同实例」教训同族坑。明确不做：其余视图字符串 tooltip 仍 Semi 默认底（无染色组合、无实报），app 级 tooltip 统一另立卡。测试 +1 三段锁（显式 ToolTip+类 / 徽章文本自类染橙 / badge 类下无类深层不染），headless 弹层 21/21。全量 1537 Passed / 0 Failed（22 跳过）。真机观感移交用户。

> 2026-10-25（深夜四）：**Q-017 弹层 KPI 三格同字号 + 异常徽章迁头部收口**。用户拍板：左格 22px 主次层级「不协调」，统一三格同字号；橙色 ↑x.x 倍徽章在 KPI 花费格「太丑、挤占本就不宽裕的布局」，迁头部 Tokenmetry 品牌/状态之后作应用级小徽章。修法：KPI 网格 `1.3*,*,*`→`\*,\*,\*`，左值 22→17（-0.5 字距随主格退场，三值 17 Bold 逐位一致；取 17 非 22 因五位数成本串在等宽列 22px 有溢出风险），中格徽章挪走后水平 StackPanel 拆除；徽章迁头部状态文字后（Spacing 5 + Margin 6），类名/绑定/Tooltip/VM 契约零改。Q-016 收口所述异常唯一出口「弹层 KPI 徽标」的落位随本卡迁头部，语义不变。原型偏离登记三处（22 主格 / 1.3fr / Q-011「贴数字旁」）。卡号注记：本卡最初编 Q-016 与并行会话 toast 整删卡（8252c2a）撞号，弹层三文件编号统一改 Q-017。测试：徽章视图用例改锁头部位置（popover-head 后代）+ 新增三值同字号/同粗细/三列星号等份额锁（RED 2 失败实锤），headless 弹层 20/20。全量 1536 Passed / 0 Failed（22 跳过）。真机观感移交用户。

> 2026-10-25（深夜三）：**Q-016 异常通知 in-app toast 通道整体删除收口**。用户二轮反馈主窗提示"还是一直弹弹弹"拍板整删——仍弹的直接原因是 Q-015 修复只在源码、用户跑的旧构建未重建重启；本轮按删除口径彻底拆通道（同样需重建重启生效）。删除面：`ISystemNotification`（Core 抽象）/`NullSystemNotification`（Infrastructure）/`ToastSystemNotification`（App 适配）三文件 + DI 注册 + `DashboardViewModel` 通知依赖与 Q-015 当天账本 + 设置页「启用异常通知」开关 + `AppSettings.AnomalyNotificationsEnabled`（旧 settings.json 多余键反序列化忽略）+ Translations 两键（键奇偶不破；`AnomalyCostSpikeFormat` 留给徽标 Tooltip）；`AnomalyRaised` 删 Title 只剩 Message+Ratio。异常唯一出口 = `AnomalyDetected` 事件 → 托盘弹层今日花费 KPI 徽标（Q-011），检测器与阈值不动；`IToastService`/`ToastService` 留（设置页重扫反馈等仍消费）。A-11 假开关红线反向适用：通道删了开关必须一起走。测试：Dashboard 用例重写为事件契约（载荷/每轮 raise/正常不 raise），全量 1535 Passed / 0 Failed（22 跳过）。

> 2026-10-25（深夜二）：**Q-015 异常 toast 当天只弹一次收口**。用户反馈主窗口异常提示太频繁、扰民想干掉，猜测与 Q-011 同源。取证：Q-011 只动弹层侧（提交自述「toast 侧行为不动」），每轮重弹是更早既有行为——AnomalyDetector 日累计判定触发后全天为真 × BackgroundRefreshService 默认分钟级刷新 → 主窗可见期同一句 toast 每分钟重弹（隐藏期本就不可见，即待办 B-014）。修法：`DashboardViewModel` toast 门从「设置开」收紧为「设置开 && 当天账本 Add(Kind) 首次」（`Today()` invariant 串作日键、日期翻页清账；设置关闭的轮次短路在记账之前不占当天名额；跨日异常重新成立会再弹）——与 Q-011 同哲学的 toast 侧补齐：持续状态归常驻 KPI 徽标，attention 通道只报「新发生」。`AnomalyDetected` 事件每轮照发不动（弹层徽标「异常轮置回 / 无异常轮清空」契约，回归锁钉死）。toast 整删不做：设置页开关是真实出口（A-11 假开关教训），当天首次触发仍有提醒价值，彻底安静走设置关闭。全量 1540 Passed / 0 Failed（22 跳过，+4）。

> 2026-10-25（深夜）：**D-020 时间范围预设 + 顶栏范围选择器 Flyout 化收口**。用户提案三问（想法如何/如何落地/下拉还够不够用）→ 会话拍板两期一起做。承重墙是统一模型：`TimeRange` 枚举退场，`RelativeRangeSpec`（RollingDays/SingleDay/Week/Month × Offset × Days 闭合词表）同时承载内置档（Today=SingleDay(0)、本月=Month(0)、最近 7 天=Rolling(7)…）与用户预设——旧枚举名直接充当内置 Id，旧 settings.json 选中值零迁移照常解析；标签由词表自动生成（用户不自由命名，i18n 单点 Translations），TimeRange7/30/90 三键由 TimeRangeRollingDays 格式键逐位承接删除。映射层零查询改动（GlobalFilterMapper 只吃 FromDay/ToDay，新增 internal today 注入让跨年/闰二月/周锚点用例钉死「今天」）。交互二期同步落地：顶栏 ComboBox + inline DatePicker → Semi DropDownButton + Flyout（档位清单/自定义日期/我的预设管理全在弹层内，顶栏 reflow 消失；light-dismiss 首按吞点天然无双 Toggle 问题；chevron 用 Semi 模板自带 glyph，仅新增 lucide "x" 作行删除图标）。预设服务封闭校验（域内/与内置及既有预设去重/上限 6）+ 即存，选中持久化为 "preset:<guid>"、指向已删预设如实回落默认档。并发注记：与时间分析页 KPI 扩展会话同工作区并行共享 Translations/board——中途构建命中对方半途状态（字典先行属性未到）重跑即绿，全量 1535 Passed / 0 Failed（22 跳过，+52 两卡分账）。真机观感移交用户。**修正项 1（目检反馈）**：档名按钮文字主色蓝（Semi DropDownButton 基样式 Foreground=ButtonDefaultPrimaryForeground），旁边三个下拉继承全局默认文字色——headless 探针实证该默认色两变体逐位等于 Semi 自有 token SemiGrey9（Light #1C1F23 / Dark 近白），触发按钮类样式指回该键同源同值（首版 AppTextBrush #1C2735 不同值被新回归锁如实拦下）；chevron 随模板绑定一同回位，hover/弹层仍走 Semi 按钮家族。AppShellTests 顶栏用例 +1 颜色相等锁（按颜色值比较——实例断言跨装载路径不可靠，Q-014 教训）。**修正项 2（目检二）**：字重偏粗——DropDownButton 基样式 FontWeight=ButtonDefaultFontWeight（DemiBold 600）vs 下拉继承 Normal 400，字号同为 14；样式补 FontWeight=Normal，回归锁扩为颜色+字重+字号三维。全量 1536 Passed / 0 Failed。

> 2026-10-25（晚）：**Q-014 ComboBox 焦点环网页化收口**。用户截图反馈顶栏四筛选下拉选完一项后蓝框常驻，点窗口其余部分不消失、点左侧页签才退场。取证：蓝框是 Semi ComboBox `:focus` 态的焦点环（非"选中"态，ControlTheme 的 FocusAdorner=null、环完全来自伪类样式）——Avalonia 选完下拉项焦点留在 ComboBox 且点击不可聚焦区域不转移焦点，直到点中可聚焦元素（页签 = ListBoxItem）才走；web 的 `:focus-visible` 语义下鼠标选完不留环。修法：SharedStyles 增 `ComboBox:focus:not(:focus-visible):not(:error)` 覆写（晚于 SemiTheme 挂载即覆盖 ControlTheme 同名 setter），背景/描边/模板箭头色三项复位默认，全部引用 Semi 自有画刷零新增令牌、明暗自动跟随；`:focus-visible` 由 InputElement 按 NavigationMethod 判定（仅 Tab/方向键点亮），键盘 Tab 进 ComboBox 环照旧（可达性无损），:error 校验描边优先。曾否决 ClearFocus 方案：键盘流转起点会丢且键盘/指针选完无法区分。测试两用例同窗双 ComboBox 基线对照（指针三属性 ≡ 未聚焦基线 / 键盘三属性偏离基线）——踩坑：Semi 部分画刷（ComboBoxIconFocusForeground）只活在 ControlTheme 作用域、控件树 TryFindResource 不可见，且同名画刷经样式 DynamicResource 与 FindResource 两路径非同实例，逐键/实例断言均不可靠，基线对照才是跨变体稳定契约面。全量 1483 Passed / 0 Failed（22 跳过，+2）。已知边界：键盘提交选择后环同样退场（弹层收起的焦点回归不携带键盘模态；焦点仍在 ComboBox，Tab 流转不受影响），DatePicker 等其它控件同类环不跟改。真机观感移交用户。

> 2026-10-25：**Q-013 自建模型误标「已改/移除覆盖」修正收口**。用户反馈 Add model 添加的新模型哪怕没覆盖任何内置模型也显示 Overridden、Actions 出现 Remove override。取证：`Bind(imported: null)` 对自建条目同样置 `IsPriceOverridden`/`IsMetadataOverridden`（用户确实填了价与元数据，字段来自用户层），行 VM 把「字段来自用户层」错当成「覆盖了导入层」——Core 标志的「字段来源」语义本身无恙（全仓仅行 VM 消费），修 UI 层即可。修法：行 VM `IsOverridden` 加 `Origin != User` 前提（覆盖语义锚定导入层）+ 新增 `IsUserCreated`；操作列拆「删除」（自建）/「移除覆盖」（覆盖导入层）互斥按钮，同一 RemoveModelDefinition 命令；Translations 增 `ModelDeleteButton`（键奇偶不破）；「已覆盖」chip 词义随徽标同步收紧（同一判定，自建条目去「自定义」chip 找），钉住旧词义的筛选测试改新语义。全量 1481 Passed / 0 Failed / 22 Skipped。

> 2026-10-24（晚八）：**Q-011 弹层 Warn 独占修正收口**。用户反馈"当日花费超 7 天均值的常驻警告一出现，'当前正在使用中，消耗速度 xxx'提示就不再显示"。取证三处叠加：AnomalyDetector 判定日累计型（成本只增不减、均值全天固定——触发当天不可能回落）+ RefreshAsync 每轮重检测重 raise + RecomputeState 严格优先级 Warn>Active 提前 return——D-015「下一轮无异常清回」的前提对日累计型异常不成立，触发日速率提示全天消失。修法（三方向中用户拍板"信息贴在它描述的数字旁"）：三态机收敛两态，banner 只承载分钟尺度实时速率；异常归位今日花费 KPI 徽标（↑{ratio}× 均值 + Tooltip 完整消息，AppBannerWarn* 令牌复用零新增），`AnomalyRaised` 增 Ratio；warn 状态灯一并撤除（灯单色语义，不在灯上重演优先级抢占）。headless 弹层 19/19（+4：并存回归锁/无 Ratio 回落/清除/视图层渲染）。**并发注记**：与模型头像特性会话同工作区并行且共享 4 文件——本卡经选择性暂存分账；全量套件 6 失败全属对方进行中用例；互为对方解阻塞两笔（本会话补 DataUriImageConverter 的 Avalonia.Media using，对方把本卡测试 Assert.Single 改谓词重载）。

> 2026-10-24（晚七）：**Q-010 模型目录 Section 英文漏覆盖收口**。用户反馈英语 UI 下 Data & pricing 的「模型目录」仍有大量中文。取证（键差集脚本）：I-035 目录主体键只进了 Zh 字典，En 只补了后加的搜索/筛选/作用域 13 键（En 块注释自证「既有目录键暂无英文覆盖，zh 兜底」）——En 缺项按合并语义回落中文，缺项即英语界面漏中文；全字典差集恰 29 键、全属本 Section，XAML/VM 无硬编码中文排除第二根因。修法：En 目录块重写为与 Zh 同序全量对齐（既有 13 键措辞零改动）；新增 `English_CoversEveryChineseSourceKey` 键奇偶回归测试（TDD RED 29 缺键实锤 → GREEN），回落机制本身仍由既有用例守护、本测试让它不再被触发。全量 1449 Passed / 0 Failed（22 跳过，+1 即新测试）；英语界面观感移交用户。

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| B-019 | macOS Dock「显示所有窗口」崩溃——待 .ips 崩溃日志定位（macOS 26 Tahoe） | P2 | M | 用户提供崩溃日志 | none |
| B-020 | SingleInstanceGuardTests 间歇失败——AbandonedMutexException 分支是唯一 true 路径，疑 GC/线程退出时序 | P3 | M | none | none |
| D-023 | Windows 托盘双击唤出主窗——真机与单击 Switch 冲突已回退，出路待用户决策 | P3 | S | 用户决策 | none |
| I-043 | 采集真增量（message-grain 记录账本）——驻留内存从 O(全历史) 降到 O(本轮增量) | P2 | L | none | I-042 |

> 2026-10-24（晚六）：**Q-009 修正项 1——? 字形太小，改 circle-help 矢量兜底**。用户看后反馈「问号太小了，能不能大点，或者用 svg 画个 Icon」。取证：文本 `?` 墨迹只占 em 四成（11px 字号实际 ~7px，I-040 同源问题——系统字体渲染墨迹占比低且跨平台不可控），加大字号受 16px 槽位与字形 line-height 限制。修法：走 I-040 管线——生成器白名单加 circle-help（lucide-static@1.61.0）再生 Icons.axaml，model-chip 增 `PathIcon.model-fallback` 槽（13px / lg 22px、中性芯片前景、generic 伪类显隐），5 处消费点（模型页/总览/计价页两处/托盘 lg）接入；`?` 字形退到数据层语义（ModelVisual.Glyph，UI 不渲染），phi 的 Φ 不动。测试：generic 断言翻转（字形隐藏+图标显示）+ 新增墨迹几何量测锁（16px 芯片内 ≥10px，防「太小」回归）；IconResourceTests.GeometryKeys 补 circle-help 并顺手补 Q-007 漏登记的 hard-drive-upload/download 两键。headless 346 Passed（+1），全量 1448 Passed / 0 Failed（22 跳过）；真机观感移交用户。

> 2026-10-24（晚五）：**Q-009 未识别模型兜底字形 ◇ → ?**。用户截图反馈未识别模型（ark-code-latest）的兜底小菱形「看起来很山寨」。取证：◇ 是 `ModelVisualMap.GlyphOf` 的 generic 兜底字形（U+25C7 文本字形，model-chip TextBlock 显示），与 AgentVisualMap 散列字形家族（✳◈⬡◉⬟）视觉同类、易被误读成品牌标。修法：兜底换 `?`——问号直白表达「未知厂商」，全部消费点（模型统计页/总览/托盘弹窗/计价页目录）经 `ModelVisualMap.Resolve` 单点生效，「其他」聚合行同语义一并变 ?。断言同步（ModelVisualMapTests 锁 '?'、ModelLogoTests 文案修正）；phi 的 Φ 与各厂商 logo 映射不动；AgentVisualMap 散列兜底是有意设计不跟改（I-040 边界内）。全量 1447 Passed / 0 Failed（22 跳过）。**当晚修正项 1 见晚六：字形太小，改矢量图标。**

> 2026-10-24（晚四）：**D-004 修正项 1——Overview 成本趋势补零日 $0 成线**。用户反馈成本趋势"没数据时用 0 填充"：原 P2 项（成本形态视觉诚实性）的"只连已知成本日"把 I-028 补零窗口里的无用量日一律断线，趋势碎成孤点段（fixture 形态 7 个已知日只能连出一段）。口径取证：补零日（`HasData=false` / `AggregatedCost.IsEmpty`）是窗口内无任何 facts 的日子，真实成本就是 $0，与"有用量但计价缺失"的未知成本异类——前者折 0 是诚实日历语义（同 DailyTokens 形态补零约定），后者折 0 仍触"未知成本绝不静默折算为 0"红线。修法：`BuildCostLine` 成线口径改 `connectFlags` = 已知成本 ∪ 补零日，补零日 $0 进线、淡点辨识保留、悬停文案仍 "no cost data"；未知成本日（(null,true)）断线 + 淡点不变，tooltip 四态契约零改动，面积填充维持关。原断线用例拆为"补零日成线"（fixture 全窗口单段）+"未知成本日仍断线"（补零桥接 / 未知断线 / 孤立已知日不成线）两用例。**并发注记**：与 Q-008（悬停浮窗单位随语言）同工作区并行——其提交（8f5997c）先行落地，本卡改动经其选择性暂存保留于工作树，随本提交收口；合并树上全量 1447 Passed / 0 Failed（22 跳过）。真机目检移交用户。

> 2026-10-24（追记）：**Q-008 图表悬停浮窗 token 单位随 UI 语言——中文 万/亿**。用户反馈"Hover 浮窗里 token 单位在中文状态下仍显示英文（3.25M），应改 325万，本项目惯例的漏网之鱼"。取证（git log）：`FormatCompactTooltipValue`（恒英文 K/M）的 ASCII 约束源自 ScottPlot 自绘 tooltip 时代（d268368），cd1e2bd 已把浮窗换成 Avalonia 浮层（TextBlock 渲染 CJK 无障碍），但消费点与注释没跟着更新——纯残留。修法：`DailyTooltipText` 与小时形态 label 两处消费点改 `FormatCompactTokens`（随语言 万/亿 vs K/M）；零消费方后删除 `FormatCompactTooltipValue`；TrendChart 补 `CurrentLanguage` 订阅重译（B-007 同 DonutChart/Heatmap 纪律，语言切换不换数据即重建换单位）；过时注释与 ChartTooltips 类 doc 交叉引用一并修正；`FormatCompactAxisValue` 轴刻度仍恒 ASCII（ScottPlot 自绘、无 CJK 字形）。测试：既有单位断言显式锁英文口径（WithLanguage）、新增中文万口径 + 语言切换实时性用例、删废弃 theory；"in/out/tot/tok" 记号保持 ASCII（同环形图/热力图 " tok" 后缀惯例），文案本地化不做。全量 1447 Passed / 0 Failed（22 跳过）。**并发注记**：与另一会话的成本折线补零日成线改动（OverviewView/TrendChart BuildCostLine）同工作区并行——本次提交经选择性暂存（git apply --cached 按 hunk 摘取）只含本卡改动，其工作区状态原样保留。

> 2026-10-24（晚三）：**I-040 修正项 4——PathIcon 削边修复**。用户目检反馈 clock/dollar-circle/database/house 等图标"像是被削去了最外面一部分"。取证（headless 像素量测实锤）：Avalonia 12 起 `TemplatedControl`（IconElement/PathIcon 继承）把 ClipToBounds 元数据默认翻成 true，而模板内 Path 按几何 bounds 缩放填槽、居中描边的外半环（0.75px）溢出 PathIcon 边界即被削——16px 槽渲染 clock 墨迹盒恰 16×16、赤道整行贴边（平弦），同几何裸 Path 墨迹盒 18×18 圆弧收尾（Shape 元数据仍默认 false 故不受影响）；table/bot/cpu 直线边缘与削切重合故目检不可辨。修法：PathIcon 隐式 ControlTheme 显式 `ClipToBounds=False`（生成器源头改注释 + 再生 Icons.axaml，几何串零漂移），不回退"墨迹填槽"的既有尺寸决策；IconResourceTests +1 削边回归锁（ClipToBounds=false 属性断言 + 渲染墨迹盒必须大于几何盒的像素断言）。全量 1455 Passed / 0 Failed；真机目检移交用户。

> 2026-10-24（晚二）：**Q-007 修正项 1——KPI 六卡改两排三列**。用户目检截图反馈单排六卡太挤、首卡（估算总成本）标题溢出卡外。修法：KPI 行 `RowDefinitions="Auto,Auto" ColumnDefinitions="*,*,*"` + RowSpacing=12（第一排 = 总成本 + 输入/输出，第二排 = 缓存读/写 + 待确认价格），1180px 窗口卡宽 ≈179px → ≈370px；几何契约测试锁两排三列（Y 两簇 / 每行 3 卡 / 卡宽 ≥240 防单排回归）。留档截图 cost 明暗重生成（time 两张随同一测试顺带刷新；踩坑：D-013 记的相对路径落 testhost bin 目录，须绝对路径）。headless 17/17，全量见下。真机观感移交用户。

> 2026-10-24（晚）：**Q-007 成本分析页补缓存读/写占比卡收口**。用户反馈「输入成本占比 2.8% + 输出成本占比 0.6% 加不满 100%」——取证：两卡是 Token 构成口径（D-013 方案 b），分母 Σ TotalTokens 含缓存读/写而分子没有，数字本身没错、展示不自洽。修法把「缓存单列说明」升级为「单列成卡」：CostViewModel 占比扩为四元（CreateCompositionShares，分子 ΣInput / ΣOutput / ΣCacheRead / ΣCacheCreation 合计 = 分母），KPI 行 4 卡 → 6 卡（缓存读/写 Neutral 芯 + 新 lucide hard-drive-upload/download 几何，方向相对盘体与输入 ↓ / 输出 ↑ 同构），脚注去「（缓存单列）」过时括注、改「四项合计全部 Token」（F1 舍入 ±0.2pp 不写死 100.0%）。TDD：四项断言编译 RED → GREEN，无基数 "—" 与渲染可见性随补；全量 1452 Passed / 0 Failed（22 跳过）。**并发注记**：与 I-040 修正项 2（PathIcon 居中）同工作区并行——两个新图标几何随其提交（6e3c8e9）落地；其收尾一度把本卡实现文件扫入后 amend 剔除，本卡实现最终独立成提交（卡内如实登记经过）；明暗目检移交用户。

> 2026-10-24：**Q-006 页面滚动面贴窗口边缘收口**。用户反馈（Q-005 验收后）"滚动条总是贴不住最右边"——取证：滚动条贴视口缘无任何空隙（模板查证 + Q-004 headless 契约），22px 空隙唯一来源是 PageHost `Margin="22,22,22,30"` 把页面根 ScrollViewer 整体推入，视口够不到窗口缘；属 Q-004"贴视口"语义与用户"贴窗口"预期的偏差，非回归。修法（macOS 式滚动面语义）：PageHost 外边距归零，滚动面铺满主内容列，页面留白由根滚动面自持——新共享类 `ScrollViewer.page-root`（Padding=22,22,22,30 单一出处；Semi 模板查证透传 Padding 给 presenter、不影响滚动条位置）挂 8 个页面根 SV；会话页无页面级滚动面，头尾/面板逐边自补（列表滚动条贴卡内缘属有意设计）。滑条纵向全程覆盖顶栏底→窗底、横向条贴底缘。TDD：PageScrollEdgeTests 真实 MainWindow 契约，RED 实锤视口 938 vs 960 差 22 → GREEN 贴缘 + 留白自持；踩坑：headless 进场过渡（Y 8→0）不随 RunJobs 走帧（74+8=82 实录），几何断言前须泵渲染帧到落定且泵判定须严于断言精度（74.937 尾段翻车实录 → 收紧 0.01）。全量 1452 Passed / 0 Failed；真机逐页滚动观感移交用户。

> 2026-10-24：**I-040 界面图标矢量化收口**。用户反馈"右上角设置/刷新按钮里的 Icon 都好小"→ 字体取证（纯 Python 解析系统字体 cmap/glyf）：图标是文本字形（⟳/⚙，14pt），主字体 SF Pro 不覆盖这两个码点，全系统仅 Apple Symbols 覆盖且墨迹只占 em 四到五成（34px 按钮里实际图形仅 6~8px）；原型同构，属"忠实复刻了小图标"。范围应用户要求扩到全应用：新增 `scripts/generate-ui-icons.py`（lucide-static@1.61.0，ISC，pin 版本 + 白名单纪律）一次生成 `Styles/Icons.axaml` 13 个 `AppIcon*` StreamGeometry；`Path.app-icon`（SharedStyles）统一描边（Stretch=Uniform 下 StrokeThickness 不随几何缩放——Avalonia Shape.Render 对变换后几何直接套 pen——按渲染槽定值 1.5，颜色一律令牌画刷）；导航图标经 `ShellPages.Icon` 键名 + `Controls/PageIcon.Icon` 附加属性 → pi-* 伪类（同 AgentChip 机制）。替换点：顶栏刷新/设置、侧栏九页导航、品牌标、总览+样式预览 KPI 四 chip、托盘弹窗设置按钮；**不替换**厂商兜底散列字形与品牌 logo（§7/§8 既有决策）与 KPI 涨跌箭头（文本语义）。headless +5 守卫（键名↔资源不漂移 / 两主题同实例 / 样式应用 / 附加属性解析 / 几何出框护栏）；真机目检反馈修正项 1（成本卡换带圈 dollar，裸 dollar-sign 退场）后全量 1454 Passed / 0 Failed；真机明暗 × 1x/2x 目检移交用户。

> 2026-10-24：**Q-005 滚动条显示条件收口**。用户指出真机表现"鼠标移到受滚动影响的区域就显示"与预期"移到滚动条附近才显示"不符；取证确认实现与 Q-004 卡规格一致（"指针在滚动区内活动即显"）——是当初设计偏离用户预期而非回归。显示门控改为纯几何邻近判定（`ScrollBarOverlay.NearDistance = 24`：命中条 10px 矩形外扩 24px，隐藏态不可命中也照样可"接近"），内容区中部活动不再唤醒滚动条；滚动即显、闲置 1s 淡出、悬停/拖拽钉住不变。取代 Q-004 验收第 3 条（Q-004 卡留历史）。TDD：翻转 TC-005 拆两条（远离不显示 / 移近即显闲置收回），RED 实锤后 GREEN。全量 1446 Passed（+1）；邻近窗手感（右缘 34px 是否合适）真机目检移交用户。

> 2026-10-24（追记二）：**D-019 修正项 2/3——行右列统一「紧凑 tokens / 价格」（验收拍板，用户明示不讨论合理性）**。① Agent 行右列「千分位 tokens + token 占比」两行 → 单行「紧凑 tokens / 价格」（例 "1.14亿 / $3.97"，FormatCost 四态，无成本数据 "… / —"）；② 修正项 1 的 token 副题退场（"不在副标题显示"），模型行右列与 Agent 行统一同格式，成本占比文案同步退场。两区 mini bar 口径不变（Agent=token 占比 / 模型=成本占比），占比信息全由 bar 承载；排序口径与主窗其他页面展示均不动。fixture Agent 行补 Cost，断言改同源组合。全量 1446 绿。

> 2026-10-24（追记）：**D-019 修正项 1——模型行副题加今日 token 紧凑数**。用户真机目检"整体没问题"后指出模型行只有金额、缺"量"的参照——拍板轻量补法：模型名下副题行放 FormatCompactTokens（KPI 同口径），成本金额保持主数字、mini bar 保持成本占比（不推翻"钱是主语"的区块定位，tokens 供判断"贵模型烧的量值不值"）。行 VM +1 字段、模板 +1 行、+1 同源断言零新用例。同轮 headless 计数 344→345 漂移经 stash 实测排查：来自并行落地的 Q-005 提交，与本卡无关。

> 2026-10-24：**D-019 弹层模型用量区收口**。用户发起"托盘信息窗里要不要加一下模型用量"→ 两轮问答定案：**日累计成本口径**（非即时速率口径）+ 模型区块。Agent Top4 与"钱花在哪个模型"正交（模型价差大，token 占比 ≠ 成本占比）——Agent 区下新增「主要模型」区：Top3 按**已知成本**降序（`KnownCostUsd ?? -1m` 哨兵使未知成本沉底，低于任何已知值含"已知为零"；占比直接吃 CostShare，含未知成本行诚实 "—"），行复用 Agent 行结构 + 新 `model-chip-lg` 大槽（ModelVisualMap 机制），行点击 → 模型统计页（会话页无按模型路由），无数据整块隐藏（不重复 Agent 区空态行）。IA 偏离原型属用户拍板的既有架构增量，原型文件不动、口径拍板落卡（D-019 卡「口径拍板」节）。数据零新口径（GetModelBreakdownAsync 今日窗口 + 序号防护）。测试翻车留痕：排序用例首轮 fixture 期望写错（第五行已知 1.10 本就该排在"已知为零"前），修正 fixture 后实现零改动。全量 1445 Passed（+3）；真机明暗 × 1x/2x 目检移交用户。

> 2026-10-20：**Q-004 全局滚动条重主题收口（用户三点反馈一次清账）**。Semi 12 的 ScrollBar 收合态仍是「2px 常驻细条 + 右缘 6px 空隙 + 悬停出箭头」，不满足现代 overlay 语义——全量替换 `{x:Type ScrollBar}` ControlTheme（`Styles/ScrollBarTheme.axaml` 以 ResourceInclude 进 Application.Resources，资源查找先于 Styles 里的 SemiTheme 即完成覆盖）：6px 细条贴视口右/下缘、无箭头（Line 按钮物理移除，Track 页按钮透明保留翻页）、无轨道底；显隐状态机在 `Controls/ScrollBarOverlay`（附加行为经 ControlTheme setter 全局启用，含 TextBox/下拉内部）：滚动值变化或指针在滚动区内活动即显、闲置 1s 淡出、悬停/拖拽钉住，隐藏态不参与命中（不挡贴边内容）。配色 `AppScrollBarThumbBrush`/`HoverBrush` 进 ThemeDictionaries（design-tokens §9）。踩坑留卡：Avalonia 12 禁外部加 `:` 伪类（改普通类）、ControlTheme 须进 ResourceDictionary、headless Window 内容居中（指针坐标经 TranslatePoint）、1ms 闲置窗与 120ms 淡入竞态；行为在 DetachedFromVisualTree 清理（模板重建遗弃旧实例的泄漏实锤修复）。headless 6 条契约测试固化（ScrollBarOverlayTests）；离屏截图 Dark/Light 各一帧目检通过。全量 1392 Passed（+6）；真机明暗 × 1x/2x 手感目检移交用户。

> 2026-10-17（晚五）：**D-015/D-016 状态栏快捷信息窗收口（同批落地，Todo 清空）**。内容层（D-015：`StatusPopoverView` 438px 卡 = 三格 KPI + Token 构成条 + Agent Top4 + 三态 banner，数据全走 IUsageQueryService / DashboardViewModel 既有回写）与壳层（D-016：无边框透明窗 + 失焦/Esc/再点收起，Windows `GetCursorPos` 光标上方 / macOS 主屏工作区右上零 interop 近似）一次成对实现；托盘左键改 Toggle 弹层，右键菜单不动。修正项六条逐条落卡（footer 取「最近会话 →」）；速率窗口实现为 45–90s（下限防启动缓存热载误判，卡内留证）；`DashboardViewModel` 增 `AnomalyDetected` 同源事件（无论 toast 开关）。XAML 限制留档：`Grid.ColumnDefinitions` 不可绑定 → `ProportionalColumns` 附加属性翻译。全量 1310 Passed（+18）；真机明暗 × 1x/2x 目检（含 macOS 锚点近似可否接受）移交用户。

> 2026-10-17（晚六）：**D-016 真机反馈修正（B2）——macOS 点击事件取证**。用户反馈"状态栏跟以前一样"：Avalonia `TrayIcon.Clicked` 在 macOS 从不 raise（TrayIcon.cs 文档原文 + trayicon.mm 无点击回调，任何点击被 NSStatusItem 菜单接管；Windows WM_LBUTTONUP 正常）——左键/右键分工只在 Windows 成立，晚五注记中"托盘左键改 Toggle 弹层"仅对 Windows 成立。零 interop 内修正：托盘菜单新增「快捷信息窗」项 Toggle 弹层（macOS 唯一入口，Windows 冗余共享）；"自建 NSStatusItem interop 实现真·左键弹层"登记为待用户决策的升级项（超出预授权的 CGRect 锚定 interop，独立卡级）。全量 1312 Passed（+2）。另：状态栏图标本身不变是 D-014 决策 C 的既定语义（模板图标），弹层价值在菜单项/左键（Win）之后。

> 2026-10-17（晚七）：**D-016 B2 升级项实施——macOS 点图标直接弹层（CodexBar 同款，用户拍板）**。自建 NSStatusItem interop（`MacTrayInterop`）：objc_allocateClassPair 派生按钮子类接管 `mouseUp:`/`rightMouseDown:`（无 ivar + 静态 IMP，不涉 ObjectiveCMarshal），左键 Toggle 弹层、右键弹同构原生 NSMenu；图标复用模板资产 setTemplate；顺带拿到图标精确 frame——`ConvertPointY` 公式对齐后弹窗锚定从"工作区右上近似"升级为"图标正下方右缘对齐"。TrayService 双轨：mac 生产走 interop（异常自动回落 Avalonia 托盘+菜单项入口），Windows/headless 走 Avalonia（测试宿主打 TOKENMETRY_HEADLESS 标记）。真机冒烟：stderr 无回落 + System Events 查得唯一 "status menu" 状态项；headless 1314 Passed（+2 纯函数对账）。约束：仅 arm64 验证过 ABI（Intel stret 未实现，需则立卡）；点击手感/明暗截图移交用户。

> 2026-10-18（B3）：**D-016 真机反馈二连修——左键直弹落位闭环**。①左键无反应根因：NSButton 在 mouseDown 跟踪循环内消费 mouse-up，`mouseUp:` 重写不派发——改 target/action（button → 动态类 `tokenmetryTrayButtonAction:` IMP → Toggle）；②修正确过程中 SelSetAction 漏赋值的空 selector SIGSEGV（.ips 三连）；③锚定经 AX 探针闭环：状态窗口顶贴栏顶（高 30=栏高），可视单元 24×24 钳定，AppKit constrainFrameRect 强制窗口顶≥栏底（不受应用控制）——最终弹窗贴栏底弹出（卡片顶=栏底+6px）、右缘对齐图标，AXPress 三连击验证开/收/开。顺手修 Reload 测试的跨零点日期敏感缺陷。全量 1314 绿。点击手感/明暗截图仍待用户目检。

> 2026-10-18（B4）：**弹窗水平位置改右弹优先**。用户反馈"太靠左"——旧"右缘对齐图标+8"语义使卡片整体向图标左侧延伸；`AnchorBelow` 改左缘贴图标左缘（状态窗口 frame 左，比 AX 可视图标左 7px，目检基准留卡）、右缘越出工作区由既有 ClampToArea 整体左移贴右，`rightOverlap` 参数删除；Windows 光标上方分支不动（托盘贴屏底，向上向左展开是平台惯例）。真机核账：图标 (2092,3,24,24) → 弹窗 (2055,30,498,477)，卡片左缘 2085=状态窗口左、右弹未触钳制，开→收→开三连过。

> 2026-10-18（B5）：**毛玻璃两平台一套落地（AcrylicBlur + 协商失败回落不透明）**。评估定论：Avalonia 12 `TransparencyLevelHint=AcrylicBlur` 是跨平台一等能力（mac vibrancy / Win WinRT backdrop，Win10 1803+/Win11），零 interop——"Mac-only 破坏统一性"之问不成立，各平台渲染各自原生瞬态材质即设计语言统一。实现：hint 改 AcrylicBlur + 内容卡 `popover-card.acrylic` 半透明变体（新令牌 `AppPopoverCardBackgroundBrush` 明暗各 #E6FFFFFF/#E6111C2E，与 AppPanelBrush 同基调）+ 壳层按 `ActualTransparencyLevel` 切类，协商失败（透明效果关/RDP/旧 Win10）保持不透明，回落分支与 macOS"减少透明度"对称。踩坑：Avalonia.Native 对 hint 即设即报、XAML inflate 期 ShadowHost 未赋值即回调 → 生产独有 NRE（headless 全绿不可见），空守卫修复；interop 左键失败日志升级带类型+堆栈。明确不做：Win11 官方瞬态 backdrop P/Invoke（原生路径足够）。全量 1314 绿；acrylic 观感（明暗×1x/2x）与投影边距带被模糊铺满的 fringe 取舍移交用户目检。

> 2026-10-18（B6）：**毛玻璃观感返工——"整面板即原生毛玻璃"（CodexBar 同款）**。用户目检否决 B5（半透明卡浮在更大的方形毛玻璃板上，两层皮 fringe）；取证 Avalonia.Native 12.1.4：AcrylicBlur = AutoFitContentView._blurBehind 铺满窗口矩形，毛玻璃天然窗口级、不随卡片成形。B6 改几何：acrylic 生效时窗口收缩到卡片（498→438、投影边距归零、去 BoxShadow），圆角与成形交平台原生——mac 新 interop 把 blur 视图 layer 裁 15px 圆角 + 窗口垫色置 clearColor + 开系统投影（MacPopoverBlurInterop，全指针/double 传参 Intel 兼容、失败回落方形）；Win11 DWM 原生圆角（Win10 方形=原生观感），卡片 Windows acrylic 态方形去描边。回落语义不变（失败=投影形态=B5 之前观感）。真机核账 (2051,30,438,437) 三连过、stderr 干净；blur 成形/明暗观感移交用户目检。

> 2026-10-18（B6 返工）：**四角灰底根因——interop 从未执行**。一次性 stderr 诊断：TryGetPlatformHandle 实测 descriptor=NSWindow（非首版假设的 NSView），校验直接放弃 → blur 未裁圆角、窗口垫色（windowBackgroundColor）从四角圆角外露出。修正：描述符双形态接法（NSWindow 直用/NSView 走 [view window]）+ 内容容器层同步裁圆角（投影轮廓走圆角）+ Activated 幂等再成形（防平台重申垫色）+ interop 每进程一次性诊断。诊断留证 "blur 视图 #2 已成形（圆角 15、垫色置透明、开投影）"，全量 1314 绿。用户定调：放弃 CodexBar 式全毛玻璃的进一步追求（ROI），保留现面板、灰底移除即收工。

> 2026-10-17（晚四）：**D-014 决策 C 收口**。用户三选一定案：**回落模板图标、关卡**——状态栏文本直显经 B1 取证在 Avalonia TrayIcon 上不可行，文本信息走菜单首行 + D-015/D-016 弹窗。位图路径（渲染器/字形分派/托盘接线）撤除回归原 A-0 语义；`TrayStatusText` 格式化器及纯函数测试保留给 D-015 复用。全量 1292 Passed。上游 `setLength` 修复未承诺，若未来仍要免点击文本走 interop 方案②携 B1 证据开新卡。

> 2026-10-17（晚三）：**D-014 回退 Todo**。真机目检（用户截图）发现状态栏只见「M ·」——Avalonia 12.1.4 `trayicon.mm` `statusItemWithLength: NSSquareStatusItemLength` 固定方形且无 `setLength`，宽文字位图中段被裁（本机文本 "324.2M · $0.00" 中段恰为 M ·，证据链闭环记入卡内 B1）。**结论：硬裁剪非画质问题，位图方案在 macOS 不可行**，调优不可解。出路三选待用户决策：A 升级 interop（NSStatusItem 真文字，卡内预留方案②）；B 方形紧凑字妥协（3–4 字符）；C 回落模板图标关卡（等 D-015/016 或上游）。格式化器/降级分派/headless 断言按出路复用。

> 2026-10-17（晚二）：D-014 首轮实施收口（TrayService/TrayStatusText/TrayStatusIconRenderer，全量 1300 Passed）。紧凑口径沿原型 ASCII（K/M/B/T，不随 UI 语言，有意偏离图表万/亿）；三态降级 = 无数据「—」/ 首轮扫描「…」/ 刷新失败回落纯模板图标；2x 像素空间渲染依据 Avalonia.Native trayicon.mm 的 17pt 统一缩放。TC-002 真机目检移交用户——**当晚目检即翻车，见晚三**。D-015/D-016 解除等待。

> 2026-10-17（晚）：**状态栏路线定稿——用户经 ChatGPT 产出 UI 原型，原样收入 `docs/design/tokenmetry_statusbar_prototype.html`**（沿 ccusage_analytics 原型先例：原型为设计参照，落地按卡内修正项偏离，不改原型）。CodexBar 式「状态栏文本 + 点击弹固定内容面板」确认采纳：内容层一份（D-015：今日 KPI 三格 + Token 构成 + 主要 Agent Top4 + 三态 banner，全走 IUsageQueryService 同源；六条修正项卡内锁定——真品牌图标 / 全令牌化 + 亮色变体 / 最小 10px / 速率=刷新差值÷间隔 / footer 左键改语义 / 点击行为收敛既有导航）、壳层分平台（D-016：macOS 顶部近似锚定先行、Windows GetCursorPos flyout，零 interop 台阶式升级）；D-014 升 P2 并入 tray 文字规格（紧凑 tokens + 成本）。常驻正当性定调：数据重启可补扫不丢失，常驻价值 = 状态栏 + 未来推送；默认关窗行为待状态栏落地后复评（S-005 开关已是用户侧出口）。

> 2026-10-17：**S-005 关窗行为收口（用户提案直接落地）+ D-014 建卡**。A-0「关窗即托盘」补齐两缺口：设置页「关闭窗口时最小化到系统托盘」开关（默认开，关闭后点 X 即经生命周期退出）+ 首次关窗一次性模态提示窗（in-app toast 在主窗隐藏后无处渲染、Avalonia 无跨平台系统通知 API——balloon 需 Win32 内部句柄、macOS 通知需 interop+权限流程，模态窗是零 interop 且关窗时点告知的方案）。Mac 状态栏直显成本（tooltip 在 Mac 渲染不出，成本唯一出口是菜单首行）立 D-014 单独处理；OS 级 balloon/toast 有意不做（不成比例，待真系统通知需求成卡）。

> 2026-10-17：**B-007 / Q-003 收口，Todo 清空**（B-006 遗留双卡一次清账）。Heatmap/DonutChart 挂 `CurrentLanguage` 重译订阅（挂树/脱树对称）→ Rebuild，切语言后 tooltip（单位口径 / "无数据" / 空态副题）即时换语言；DonutChartTests 补 chart-tip 类 + 紧凑文案断言，独立审查复现反向验证锁有效。有意不做：TrendChart 图内 tooltip/轴刻度恒 ASCII（I-032 锁定决策）、非空态 `CenterSubtitle`（属主注入 StyledProperty），留待属主页卡处理。

> 2026-10-16：**B-006 收口（d268368）后的 code review 建 P3 小卡两张**。B-007：Donut/Heatmap 的 Avalonia 弹层 tooltip 是 build-time 固化字符串且控件未订阅 `CurrentLanguage` 重译信号（切语言后陈旧至下次数据重建；热力图"无数据"固化是 B-006 前旧账一并纳入）；Q-003：DonutChartTests 零 tooltip 断言，共享 helper/样式回归时环形图侧静默绿。均为 S 级独立可收。

> 2026-10-16：**S-004 数据与计价页拆分收口**（用户决策）。S-002 的合并页「数据源与设置」沿"影响数字的口径 vs 应用偏好"拆为「数据与计价」（数据源 / 口径 / 模型目录 / 别名映射）与「设置」（主题 / 语言 / 刷新 / 通知 / 路径）两个页签，侧栏 8→9 项；行为语义零变化，P0 重扫回归锁随迁。pivot 文档 §5 同日追记。

> 2026-10-15：**模型目录批次 I-035/I-036/I-037/S-003/I-038 同日收口**。起点是用户提出的两个问题：缺价模型没有地方设置价格（`PricingOverrides` 当时没有任何 UI 出口），以及 token 聚合的模型映射"不干净"（只影响统计、关联不上价格）。方案见 `docs/plans/2026-10-15-model-registry-design.md`。实施中有一处方案变更：原定"计价挪到查询期 + 拆 cost 来源"经核实不必要——增量扫描器每轮返回全量记录，改价触发一轮刷新即可全量重算，故按更便宜的方案落地（见 I-037 Dev Log）。遗留三项已登记：模型维度按 provider 拆分（需 session_facts 加列）、按 provider 的差异价、Pi/DSH 解析期定价的陈旧边界。

> 2026-10-12：按用户决定一次性建 UI 原型落地批次卡 I-026～I-029 / D-003～D-013 / S-002 / Q-002（17 张）。设计基准 `docs/design/ccusage_analytics_prototype.html`（同日自 ~/Downloads 收入仓库）；选型 Semi.Avalonia（依据见 I-026 Background）。本批是 v0.4 主体的全景规划，执行按依赖顺序逐卡进行：I-026 → I-027 → D-003，I-028/I-029 与主题线并行，随后 D-004/D-005 → D-006 → 其余页面 → Q-002。与「看板保持 5–10 张」的常规策略有意偏离，理由同 2026-10-11 适配器批次。

> 2026-10-11：按用户决定一次性建多源覆盖批次卡 I-006～I-022。蓝本为 ../ccusage/rust/adapters/（Rust 移植版，18 源清单见其 README「Supported Sources」）；DSH 不在 ccusage 支持列表（核实过，README 的 "trendshift" 含 "dsh" 是误匹配），I-007 为自研。与「看板保持 5–10 张」的常规策略有意偏离——本批是一次性全景规划，执行仍按优先级逐卡进行。**2026-10-12 追记：I-006～I-022 十七源适配器全部完成**（对账口径见各卡 TC），多源覆盖批次收口；I-024 候选计价接线随九源收口（UnpricedModels 欠账消除，快照补 tiers 段并刷新 kimi-k2.6）；**I-025 同日收口**：快照 7 条目经 CLI 探针核实已逐位等于 npm 20.0.29 锚点（I-024 审查清单对的是 rust HEAD、与锚点漂移，照单改价会炸对账——详见该卡 B-001），不改价，归一化歧义守卫 + 快照 _provenance 溯源落地。**Todo 清空。**

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
| D-026 | Windows 原生标题栏深色适配（DWM）+ 窗口标题缩短 | P2 | 2026-10-28 |
| B-022 | 托盘弹窗首显闪动——Show 先画空态再被回填撑高，启动预热让首帧即最终高度（B-021 修正项 1） | P2 | 2026-10-27 |
| B-021 | 托盘弹窗首显仍偏下——SizeToContent 数据异步回填撑高窗口，位置只在 Show 后算一次（B-016 修正项 1；修正项 1 见 B-022） | P2 | 2026-10-27 |
| D-025 | 第二梯队 es/de/fr 落地——语种决策由「社区认领」改为「即刻全量」（界面语言 10 语种） | P2 | 2026-10-27 |
| D-024 | 界面多语种扩展——zh-Hant/ja/ko/pt-BR/ru 五语种全量翻译 + 回退链固化（es/de/fr 等社区认领） | P2 | 2026-10-27 |
| P-004 | Native AOT 支持——反射 JSON 全量源生成，PublishAot 编译零警告（发布矩阵接线不在本卡） | P3 | 2026-10-27 |
| B-018 | macOS 点 Dock 图标无反应——interop 挂 applicationShouldHandleReopen 补全唤回（真机 TC 移交用户） | P2 | 2026-10-26 |
| B-017 | Windows 托盘左键永远 Open 不 Switch——失焦收起先于 Clicked 到达，ToggleFromTrayClick 失焦守卫（消费制） | P2 | 2026-10-26 |
| I-042 | 无变化轮次短路——零数据变化跳过计价/聚合/落库，扫描器零变更轮复用去重产物 | P1 | 2026-10-25 |
| P-003 | 发布产物 PublishReadyToRun——JIT 代码堆缩小/启动提速（包体积 +30-50% 已登记） | P3 | 2026-10-25 |
| S-007 | 刷新间隔默认 1→5 分钟——后台采集常态空转降频（设置页 1–1440 可调不动） | P2 | 2026-10-25 |
| B-016 | 托盘弹窗首显锚定取陈旧 Bounds 高度——ScreenSize 改 ClientSize，首显即贴托盘（Windows 中间偏右根因；修正项 1 见 B-021） | P2 | 2026-10-25 |
| D-022 | 官网功能页富数据截图套件——stub fixture × zh/en × 明暗 × 7 页 + 托盘（裸窗特写，零 src 改动） | P3 | 2026-10-25 |
| B-015 | 价格输入双文化解析（invariant Float 优先 + 本机兜底）——de-DE 逗号小数可录入；旧实现对 0,5 实为静默千倍错标非拒绝 | P3 | 2026-10-25 |
| B-010 | 日志读取共享写（AgentLogReader）——Windows agent 持写句柄期间不再静默缺数据 | P1 | 2026-10-25 |
| B-011 | Dsh/Copilot 递归跳 reparse point——junction 环不崩、不双计（真 junction 回归锁） | P2 | 2026-10-25 |
| B-012 | projects 文件名 OrdinalIgnoreCase——大小写键入不符不再静默丢源（Claude/Codebuff） | P3 | 2026-10-25 |
| B-013 | 单实例守卫（命名 Mutex + 管道激活）——第二实例唤回主窗后退出，双开不再双托盘/并发写 | P3 | 2026-10-25 |
| B-014 | 托盘常驻态异常 toast 不可见——随 Q-016 删通道失效关卡（异常出口=弹层/头部徽标，与主窗可见性无关） | P3 | 2026-10-25 |
| D-021 | 时间分析页扩充——时间 KPI 四卡 + 星期分布面板（零新查询，同源客户端聚合） | P2 | 2026-10-25 |
| D-020 | 时间范围预设——内置日历档（本月/上月/昨天）+ 用户自定义相对档 + 顶栏范围选择器 Flyout 化 | P2 | 2026-10-25 |
| Q-014 | ComboBox 焦点环网页化——指针焦点不画环、键盘焦点环保留（顶栏筛选选完蓝框常驻） | P2 | 2026-10-25 |
| Q-013 | 自建模型误标「已改/移除覆盖」——覆盖语义锚定导入层，自建条目行走「删除」 | P2 | 2026-10-25 |
| Q-012 | 模型头像（图标选择器四模式）——新增模型也有头像，全应用显示统一 | P2 | 2026-10-24 |
| Q-011 | 托盘弹层 Warn banner 独占——日尺度异常归位今日花费 KPI 徽标，banner 还给实时速率 | P2 | 2026-10-24 |
| Q-010 | 模型目录 Section 英文漏覆盖——29 键回落中文（I-035 欠账清偿 + En↔Zh 键奇偶测试固化） | P2 | 2026-10-24 |
| Q-009 | 未识别模型兜底图标——◇ 文本字形 → circle-help 矢量问号（不冒充品牌标） | P3 | 2026-10-24 |
| Q-008 | 图表悬停浮窗 token 单位随 UI 语言——中文 万/亿（ScottPlot 时代 ASCII 约束残留） | P2 | 2026-10-24 |
| I-041 | 关于面板补版权/许可/仓库/无担保行——AGPL Appropriate Legal Notices 对齐 | P3 | 2026-10-24 |
| Q-007 | 成本分析页补缓存读/写占比卡——Token 构成四项合计满 100%（图标几何随 6e3c8e9 落地） | P2 | 2026-10-24 |
| Q-006 | 页面滚动面贴窗口边缘——PageHost 外边距移入滚动内容（留白自持，page-root 类） | P2 | 2026-10-24 |
| I-040 | 界面图标矢量化——lucide 生成 AppIcon* 替换全部 Unicode 字形图标（顶栏/导航/品牌/KPI chip/弹窗） | P2 | 2026-10-24 |
| Q-005 | 滚动条显示条件收窄——指针活动改为邻近命中区才显示（取代 Q-004 验收第 3 条） | P2 | 2026-10-24 |
| D-019 | 弹层模型用量区——Top3 按今日已知成本（未知沉底 / 诚实占比 / 空态整块隐藏） | P2 | 2026-10-24 |
| I-039 | 发布自动化与版本更新机制——CI 三平台打包 + GitHub Release + 应用内检查/下载 | P1 | 2026-10-24 |
| D-018 | 应用图标——托盘外三平台图标接线（exe / 窗口 / Dock，绿柱 v1 定稿） | P2 | 2026-10-24 |
| B-009 | ScrollBarOverlay headless 测试泵补渲染失效（渲染空闲不推进 DispatcherTimer 时钟） | P3 | 2026-10-23 |
| B-008 | ExpandHomePath Windows 混合分隔符（~/a/b → C:\Users\x\a/b）回归锁 | P2 | 2026-10-23 |
| P-002 | 官方价单一目录——一模型一条目 + 用户条目前缀作用域 | P1 | 2026-10-23 |
| Q-004 | 全局滚动条重主题——overlay 语义（闲置隐藏 / 贴边 / 无箭头） | P1 | 2026-10-20 |
| S-006 | 开机自启动开关（默认关）+ --autostart 静默启动 | P2 | 2026-10-19 |
| D-017 | 右栏撤除 + 顶栏四维全局筛选 + 会话行元数据升级 | P1 | 2026-10-18 |
| D-016 | 状态栏快捷信息窗 · 壳层（macOS 顶部锚定 / Windows flyout，复用 D-015） | P2 | 2026-10-17 |
| D-015 | 状态栏快捷信息窗 · 内容层（共享 UserControl：KPI + 构成 + Top4 Agent + 三态 banner） | P2 | 2026-10-17 |
| D-014 | 状态栏直显今日用量（位图方案真机不可行 B1 → 决策 C 回落模板图标，关卡） | P2 | 2026-10-17 |
| S-005 | 关窗行为开关（关窗=托盘可关）+ 首次关窗托盘一次性提示 | P2 | 2026-10-17 |
| B-007 | 语言切换后图表 tooltip 文案陈旧（Heatmap/DonutChart 缺 CurrentLanguage 重译订阅） | P3 | 2026-10-17 |
| Q-003 | 图表 tooltip 测试对称补齐（DonutChart chart-tip/紧凑文案零覆盖） | P3 | 2026-10-17 |
| B-006 | 图表 hover tooltip 三缺陷（透明无样式气泡 / 配色不随主题 / token 数紧凑单位） | P1 | 2026-10-16 |
| S-004 | 数据与计价页拆分（数据源/口径/目录/别名 ↔ 应用偏好分离，侧栏 8→9 项） | P2 | 2026-10-16 |
| I-035 | 模型目录（ModelId 单一身份）+ 计价接线 | P1 | 2026-10-15 |
| I-036 | provider 三元组（agent × model × provider）落库与查询 | P1 | 2026-10-15 |
| I-037 | 目录驱动的重算（改价格即生效，不做查询期计价） | P1 | 2026-10-15 |
| S-003 | 设置页模型目录（价格 / 厂商 / 图标唯一入口 + 关联） | P1 | 2026-10-15 |
| I-038 | 目录迁移与对账护栏（旧字段兼容 + 信任锚边界） | P2 | 2026-10-15 |
| B-005 | 首启全窗冻结（采集扫描内联 UI 线程）+ 初始化菊花转动不匀 | P1 | 2026-10-16 |
| I-034 | Agent 品牌 logo 接入（ZCode/DSH 等 16 个 Agent 行内官方标 + 未覆盖者散列字形兜底） | P2 | 2026-10-15 |
| I-033 | 模型厂商 logo 接入 lobe-icons（官方彩标行内直放 + 单色标明暗翻转） | P2 | 2026-10-15 |
| I-032 | 趋势图视觉升级（单调平滑曲线 + Web 风 chrome）+ 只读交互（禁用缩放/平移） | P2 | 2026-10-15 |
| I-031 | 首次启动数据初始化（启动即刷可见反馈 + 全窗口初始化遮罩） | P2 | 2026-10-15 |
| I-030 | 模型聚合（别名映射）+ 内置模型图标 | P1 | 2026-10-15 |
| B-004 | flaky：CopilotSessionAdapterTests 间歇多计 LinesSkipped | P2 | 2026-10-13 |
| Q-002 | 旧视图清理与视觉验收打磨 | P2 | 2026-10-13 |
| S-002 | 数据源与设置页重构（原型版） | P2 | 2026-10-13 |
| D-013 | 成本分析页（计费口径透明） | P2 | 2026-10-13 |
| D-012 | 时间分析页（小时分布 + 活跃日历热力图） | P2 | 2026-10-13 |
| D-011 | 项目统计页 | P2 | 2026-10-13 |
| D-010 | 模型统计页（价格缺失显示未知口径样板） | P2 | 2026-10-13 |
| D-009 | Agent 统计页 | P2 | 2026-10-13 |
| D-008 | 会话明细页 + 右栏最近会话联动 | P1 | 2026-10-13 |
| D-007 | 总览页（下半）：模型分布 + Token 构成 + 项目排行 + 成本趋势 | P2 | 2026-10-13 |
| D-006 | 总览页（上半）：KPI 行 + 使用趋势 + Agent 占比环形 | P1 | 2026-10-13 |
| D-005 | 自绘环形图与热力图控件 | P1 | 2026-10-13 |
| D-004 | ScottPlot 趋势图正式集成（堆叠柱 + 折线 / hover helper） | P1 | 2026-10-13 |
| I-029 | UI 聚合查询服务 B：维度聚合 + 会话明细 + 小时分布 | P1 | 2026-10-13 |
| I-028 | UI 聚合查询服务 A：日趋势 + KPI + 区间对比 | P1 | 2026-10-13 |
| D-003 | 三栏应用外壳 + 导航 + 全局筛选状态 | P1 | 2026-10-13 |
| I-027 | 设计令牌与共享样式（原型色板 / 卡片 / 系列色 / mini 进度条 / toast） | P1 | 2026-10-13 |
| I-026 | Semi.Avalonia 主题迁移（含 Avalonia 12.1.x 升级） | P1 | 2026-10-13 |
| I-025 | 定价快照数据版本刷新 | P3 | 2026-10-12 |
| I-023 | Copilot CLI OpenTelemetry 合并与积分对账 | P3 | 2026-10-12 |
| I-024 | 候选计价端到端接线 | P2 | 2026-10-12 |
| I-021 | Antigravity 适配器 | P3 | 2026-10-12 |
| I-020 | OpenClaw 适配器 | P3 | 2026-10-12 |
| I-022 | Grok Build CLI 适配器 | P3 | 2026-10-12 |
| I-019 | pi-agent 适配器 | P3 | 2026-10-12 |
| I-018 | Codebuff 适配器 | P3 | 2026-10-12 |
| I-013 | Kimi Code 适配器 | P3 | 2026-10-12 |
| I-011 | Qwen Code 适配器 | P2 | 2026-10-12 |
| I-015 | Droid (Factory) 适配器 | P3 | 2026-10-12 |
| I-014 | Amp 适配器 | P3 | 2026-10-12 |
| I-010 | Gemini CLI 适配器 | P2 | 2026-10-12 |
| I-017 | Kilo 适配器 | P3 | 2026-10-12 |
| I-016 | Hermes Agent 适配器 | P3 | 2026-10-12 |
| I-012 | Goose 适配器 | P2 | 2026-10-12 |
| I-009 | OpenCode 适配器 | P2 | 2026-10-12 |
| I-008 | GitHub Copilot CLI 适配器 | P2 | 2026-10-11 |
| I-007 | DSH 会话适配器 | P1 | 2026-10-11 |
| I-006 | ZCode SQLite 适配器 | P1 | 2026-10-11 |
| D-002 | ScottPlot.Avalonia 集成 spike | P2 | 2026-10-11 |
| I-005 | 本地采集数据源接线，CLI 降级为对账工具 | P1 | 2026-10-11 |
| Q-001 | 与 ccusage CLI 的对账测试 | P1 | 2026-10-11 |
| I-004 | Codex JSONL 适配器 | P2 | 2026-10-11 |
| I-003 | 自有定价引擎（内置快照 + models.dev + 用户覆盖） | P1 | 2026-10-11 |
| I-002 | SQLite facts 存储 + migration runner | P1 | 2026-10-11 |
| I-001 | Claude Code JSONL 直读适配器 | P1 | 2026-10-11 |
| R-001 | Reports 每日数据表格 | P1 | 2026-06-19 |
| D-001 | Dashboard 卡片布局优化 | P1 | 2026-06-19 |
| P-001 | Windows 打包发布 | P1 | 2026-06-19 |
| R-002 | Reports 排序和筛选 | P2 | 2026-06-19 |
| R-003 | Reports 导出 CSV | P2 | 2026-06-19 |
| S-001 | Settings 基础页面 | P2 | 2026-06-19 |
| R-004 | Model Breakdown 页面 | P2 | 2026-06-19 |
