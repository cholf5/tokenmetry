using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Tokenmetry.App.Localization;

/// <summary>
/// 全部 UI 文案的唯一出处（移植 stupid-dict 的方案，不引入 i18n 库）：
/// 简体中文字典为源（<see cref="Zh"/>，key = nameof(属性)，漏 key 时 <see cref="Get"/> 回落显示
/// 属性名——遗漏自暴露），英文字典只放覆盖项（<see cref="En"/>，缺项回落中文）。
/// <para>XAML 经 <c>{Binding 属性, Source={x:Static loc:Translations.Instance}}</c> 消费；
/// <see cref="SetLanguage"/> 重建合并字典后对全部属性一次性 raise PropertyChanged——
/// 绑定不重启实时切换；命令式渲染的订阅方以 PropertyChanged(CurrentLanguage) 作为
/// 「每次切换一次」的信号做重译（同 stupid-dict 的 CurrentLanguage 信号约定）。</para>
/// <para>语言偏好持久化在 AppSettings.Language（"system"/"zh"/"en"，未知值回落 system），
/// 启动时 App 在创建主窗/托盘前 ApplyPreference 一次。瞬态文案（toast、一次性状态行）
/// 保持出现时的语言，不做回溯重译（同 stupid-dict 既定取舍）。</para>
/// </summary>
public sealed class Translations : INotifyPropertyChanged
{
    // 注意：静态字段按文本顺序初始化——Zh/En/AllPropertyNames 必须先于 Instance 声明，
    // 否则单例构造读到的字典还是 null（C# 静态初始化顺序陷阱）。
    private static readonly Dictionary<string, string> Zh = new()
    {
        // ── 导航与页面头（ShellPages.PageDescriptor 按下标取用）──
        [nameof(NavOverview)] = "总览",
        [nameof(NavSessions)] = "会话明细",
        [nameof(NavAgents)] = "Agent 统计",
        [nameof(NavModels)] = "模型统计",
        [nameof(NavProjects)] = "项目统计",
        [nameof(NavTime)] = "时间分析",
        [nameof(NavCost)] = "成本分析",
        [nameof(NavDataPricing)] = "数据与计价",
        [nameof(NavSettings)] = "设置",
        [nameof(OverviewTitle)] = "Token 使用概览",
        [nameof(OverviewSubtitle)] = "统一查看不同 Agent 的使用量、成本与工作负载",
        [nameof(SessionsTitle)] = "会话明细",
        [nameof(SessionsSubtitle)] = "从汇总下钻到单次 Agent 会话，支持搜索与组合筛选",
        [nameof(AgentsTitle)] = "Agent 统计",
        [nameof(AgentsSubtitle)] = "比较不同工具的使用强度、成本与会话规模",
        [nameof(ModelsTitle)] = "模型统计",
        [nameof(ModelsSubtitle)] = "模型维度的使用量与成本，不把价格差异隐藏在总量中",
        [nameof(ProjectsTitle)] = "项目统计",
        [nameof(ProjectsSubtitle)] = "按工作目录聚合，观察 Token 消耗集中在哪些代码库或任务",
        [nameof(TimeTitle)] = "时间分析",
        [nameof(TimeSubtitle)] = "识别使用高峰、工作时段分布与长会话",
        [nameof(CostTitle)] = "成本分析",
        [nameof(CostSubtitle)] = "区分真实计费、模型价格估算与无法估价的数据",
        [nameof(DataPricingTitle)] = "数据与计价",
        [nameof(DataPricingSubtitle)] = "多 Agent 统一统计的关键是来源适配、去重和口径透明",
        [nameof(SettingsTitle)] = "设置",
        [nameof(SettingsSubtitle)] = "主题、语言与刷新偏好；改完即生效并自动保存",

        // ── 顶部栏（MainWindow.axaml + MainWindowViewModel）──
        [nameof(TopbarTimeRange)] = "时间范围",
        [nameof(TopbarAgent)] = "Agent",
        [nameof(StatusStarting)] = "启动中…",
        [nameof(StatusScanning)] = "正在扫描本地数据…",
        [nameof(StatusNoLocalData)] = "未检出本地数据",
        [nameof(StatusSourceError)] = "数据源错误（本轮刷新失败）",
        [nameof(StatusReady)] = "本地数据源就绪",
        [nameof(NotRefreshedYet)] = "尚未刷新",
        [nameof(LastUpdatedFormat)] = "最近更新 {0:HH:mm}",
        [nameof(TooltipRefreshNow)] = "立即刷新本地数据",

        // ── 首次初始化遮罩（MainWindow.axaml + MainWindowViewModel）──
        [nameof(InitializingTitle)] = "正在初始化数据…",
        [nameof(InitializingSubtitle)] = "首次启动正在扫描本机各 Agent 的会话记录并解析计费，完成后自动显示，无需手动刷新",

        // ── 右栏最近会话 + 快速筛选（MainWindow.axaml + SessionsPanelViewModel）──
        [nameof(RecentSessions)] = "最近会话",
        [nameof(ViewAll)] = "查看全部 ↗",
        [nameof(NoRecentSessions)] = "暂无最近会话",
        [nameof(QuickFilter)] = "快速筛选",
        [nameof(ClearFilters)] = "清除",
        [nameof(LabelModels)] = "模型",
        [nameof(LabelProjectDir)] = "项目 / 目录",
        [nameof(ApplyFilters)] = "应用筛选",
        [nameof(RecentErrorFormat)] = "最近会话加载失败：{0}",
        [nameof(ToastFiltersAppliedFormat)] = "筛选已应用：{0} 个 Agent · {1} · {2}",
        [nameof(ToastFiltersCleared)] = "筛选条件已清除",
        [nameof(ToastSessionPlannedFormat)] = "会话 {0}：详情视图规划中",

        // ── 全局筛选选项（FilterStateService）──
        [nameof(TimeRangeToday)] = "今日",
        [nameof(TimeRange7)] = "最近 7 天",
        [nameof(TimeRange30)] = "最近 30 天",
        [nameof(TimeRange90)] = "最近 90 天",
        [nameof(TimeRangeAll)] = "全部时间",
        [nameof(TimeRangeCustom)] = "自定义",
        [nameof(AllAgents)] = "所有 Agent",
        [nameof(AllModels)] = "全部模型",
        [nameof(AllProjects)] = "全部项目",

        // ── 托盘（TrayService）──
        [nameof(TrayTooltip)] = "Tokenmetry — Token 成本监控",
        [nameof(TrayQuickInfo)] = "快捷信息窗",
        [nameof(TrayCostFormat)] = "今日成本：${0:F2}",
        [nameof(TrayCostNoData)] = "今日成本：—（尚无数据）",
        [nameof(TrayOpenWindow)] = "打开主窗",
        [nameof(TrayRefreshNow)] = "立即刷新",
        [nameof(TraySettings)] = "设置",
        [nameof(TrayExit)] = "退出",
        // S-005：首次"关窗=托盘"的一次性提示窗（MainWindow 模态弹窗——in-app toast 在主窗隐藏后无处渲染）
        [nameof(TrayHintTitle)] = "已最小化到系统托盘",
        [nameof(TrayHintBody)] = "Tokenmetry 将继续在系统托盘运行，后台采集刷新不中断。可从托盘菜单重新打开主窗或彻底退出；本提示只显示一次，关闭行为可在「设置」页更改。",
        [nameof(TrayHintOk)] = "知道了",

        // ── 状态栏快捷信息窗（D-015 StatusPopoverView / StatusPopoverViewModel）──
        [nameof(PopoverLiveIdle)] = "实时",
        [nameof(PopoverLiveActive)] = "工作中",
        [nameof(PopoverLiveWarn)] = "注意",
        [nameof(PopoverScanning)] = "扫描中…",
        [nameof(PopoverTodayTokens)] = "今日 Token",
        [nameof(PopoverTodayTokensCap)] = "跨所有 Agent",
        [nameof(PopoverCacheHit)] = "缓存命中",
        [nameof(PopoverCacheCaption)] = "缓存读取",
        [nameof(PopoverSegCache)] = "缓存",
        [nameof(PopoverTopAgents)] = "主要 Agent",
        [nameof(PopoverRecentSessions)] = "最近会话 →",
        [nameof(PopoverOpenStats)] = "打开完整统计 →",
        [nameof(PopoverActiveBannerTitleFormat)] = "{0} 正在工作",
        [nameof(PopoverActiveBannerTitle)] = "正在工作",
        [nameof(PopoverActiveBannerSubtitleFormat)] = "当前速度 {0} · 按刷新增量估算",
        [nameof(PopoverRowRateFormat)] = "当前 {0}",
        [nameof(PopoverRowLastActiveFormat)] = "最近活动 {0:HH:mm}",

        // ── 设置页（SettingsView / SettingsViewModel）──
        [nameof(SourcesPanelTitle)] = "已识别的数据源",
        [nameof(ThSource)] = "数据源",
        [nameof(ThStatus)] = "状态",
        [nameof(ThLastScan)] = "最近扫描",
        [nameof(ThRecords)] = "记录数",
        [nameof(SourceMetaIdle)] = "状态与记录数来自上次采集扫描 · 记录数为增量累计值",
        [nameof(SourceMetaScanning)] = "扫描中…（按源逐个执行，完成后统一刷新）",
        [nameof(SourceMetaScanFailed)] = "上次扫描未完成——下表为最近一次成功快照，可能非本轮数据",
        [nameof(ScanIdle)] = "重新扫描（全部源）",
        [nameof(ScanRunning)] = "扫描中…",
        [nameof(StatusOk)] = "● 已连接",
        [nameof(StatusPartial)] = "● 部分数据",
        [nameof(StatusNoData)] = "● 未检出",
        [nameof(StatusError)] = "● 扫描失败（本轮跳过）",
        [nameof(StatusNotScanned)] = "● 尚未扫描",
        [nameof(CaliberTitle)] = "统计口径",
        [nameof(BadgeAlways)] = "始终生效",
        [nameof(BadgeNotProvided)] = "未提供",
        [nameof(CaliberDedupText)] = "按 Agent + Session ID 去重",
        [nameof(CaliberDedupNote)] = "消息级去重与会话事实按 agent + session_id 幂等 upsert，采集管道内建行为",
        [nameof(CaliberSplitText)] = "区分输入、输出、缓存读取与缓存写入",
        [nameof(CaliberSplitNote)] = "统一记录与 facts 存储固定四向拆分 Token",
        [nameof(CaliberUnknownPriceText)] = "未知模型价格显示为「未知」",
        [nameof(CaliberUnknownPriceNote)] = "缺价模型成本显式标记未知、绝不折算为 0（未知成本纪律）",
        [nameof(CaliberMergeText)] = "将不同 Agent 的疑似同一任务自动合并（谨慎启用）",
        [nameof(CaliberMergeNote)] = "跨 Agent 任务合并尚未实现；原型开关不落地，避免无效果的假开关",
        [nameof(CaliberLocalOnlyText)] = "所有原始记录默认仅在本机处理",
        [nameof(CaliberLocalOnlyNote)] = "适配器直读本机日志与账本；联网仅拉取模型价格表，原始记录不出本机",
        [nameof(AliasPanelTitle)] = "模型聚合（别名映射）",
        [nameof(AliasPanelHint)] = "不同来源对同一模型的命名不同（大小写、厂商前缀、别名，如 GLM-5.3-Flash / glm-5.3-flash / ark/glm-5.3-flash）。把原始模型名（按本地数据原样、精确匹配）映射到统一的规范名后，模型统计与分布按规范名合并为一行；修改后重新进入统计页即生效。",
        [nameof(AliasEmpty)] = "尚无映射规则",
        [nameof(AliasSourceLabel)] = "原始模型名",
        [nameof(AliasTargetLabel)] = "规范名（统计显示名）",
        [nameof(AliasSourcePlaceholder)] = "输入或从候选选择，如 ark/glm-5.3-flash",
        [nameof(AliasTargetPlaceholder)] = "如 glm-5.3-flash",
        [nameof(AliasAdd)] = "＋ 添加映射",
        [nameof(AliasRemove)] = "移除",
        [nameof(AliasErrorRequired)] = "原始模型名与规范名都不能为空",
        [nameof(AliasErrorSame)] = "原始模型名与规范名相同，无需映射",
        [nameof(AliasErrorTargetUnknown)] = "目标模型不在模型目录中——请从候选中选择已存在的模型 ID",
        [nameof(AliasCandidatesHint)] = "候选来自本机数据中出现过的模型名（按用量排序）；也可输入任意字符串，为尚未出现的来源预留映射。",
        [nameof(AppearanceTitle)] = "外观与通用",
        [nameof(LabelTheme)] = "主题",
        [nameof(ThemeLight)] = "日间",
        [nameof(ThemeDark)] = "夜间",
        [nameof(ThemeSystem)] = "跟随系统",
        [nameof(ThemeHint)] = "切换后立即生效并自动保存（重启后保持）。",
        [nameof(LabelLanguage)] = "界面语言",
        [nameof(LanguageSystem)] = "跟随系统",
        [nameof(LanguageZh)] = "简体中文",
        [nameof(LanguageEn)] = "English",
        [nameof(LanguageHint)] = "切换后立即生效并自动保存（重启后保持）。",
        [nameof(LabelRefreshInterval)] = "刷新间隔（分钟）",
        [nameof(RefreshIntervalMustBeNumber)] = "刷新间隔必须是数字",
        [nameof(RefreshIntervalOutOfRange)] = "刷新间隔必须在 1–1440 分钟之间（当前值保持不变）",
        [nameof(RefreshHint)] = "修改即时保存；于下次启动后台刷新服务时生效。",
        [nameof(LabelCcusageCli)] = "ccusage CLI（对账 / 手动导入）",
        [nameof(CcusageHint)] = "应用默认直读本地会话日志，无需安装 ccusage；此命令仅对账 / 手动导入时使用，下次启动生效。",
        [nameof(CheckAnomalyNotifications)] = "启用异常通知（应用内 toast 提示）",
        // S-005：关窗行为开关（消费者 = MainWindow.OnClosing 的关窗分派）
        [nameof(CheckMinimizeToTrayOnClose)] = "关闭窗口时最小化到系统托盘",
        [nameof(CheckMinimizeToTrayOnCloseHint)] = "关闭主窗后应用仍在托盘运行、继续定时刷新；彻底退出用托盘菜单「退出」。关闭此开关后，点关闭按钮将直接退出应用。",
        [nameof(LocalPathsTitle)] = "本地路径",
        [nameof(LabelCacheDb)] = "缓存数据库",
        [nameof(LabelSettingsFile)] = "设置文件",
        [nameof(ToastScanDone)] = "扫描完成",
        [nameof(ToastScanFailed)] = "扫描失败：数据表保持最近一次成功快照",

        // ── 启动错误窗（App.ShowStartupError，A-7）──
        [nameof(StartupErrorTitle)] = "Tokenmetry 启动失败",
        [nameof(StartupErrorExit)] = "退出",
        [nameof(StartupErrorBodyFormat)] = "本地数据初始化失败：{0}\n\n缓存库位置：{1}\n\n可删除该文件后重试——历史用量可由本地会话日志重新扫描采集。",

        // ── 总览页（OverviewView / OverviewViewModel）──
        [nameof(KpiTotalTokens)] = "总 Token 数",
        [nameof(KpiEstimatedCost)] = "估算成本",
        [nameof(KpiSessions)] = "会话数",
        [nameof(KpiCacheShare)] = "缓存读取占比",
        [nameof(KpiFootVsPrevious)] = "vs 上一周期",
        [nameof(KpiFootPriceEstimate)] = "按模型价格估算",
        [nameof(KpiFootAcrossAgents)] = "跨 Agent 汇总",
        [nameof(KpiFootCacheOfInput)] = "缓存读取 / 输入相关 Token",
        [nameof(LegendDaily)] = "按日汇总",
        [nameof(LegendInput)] = "输入",
        [nameof(LegendOutput)] = "输出",
        [nameof(LegendTotal)] = "总量",
        [nameof(PanelTrend)] = "Token 使用趋势",
        [nameof(MetaByTotalTokens)] = "按 Token 总量",
        [nameof(PanelAgentDonut)] = "Agent 使用占比",
        [nameof(ViewDetails)] = "查看详情 ↗",
        [nameof(PanelModelDistribution)] = "模型使用分布",
        [nameof(ThModel)] = "模型",
        [nameof(ThTokenCount)] = "Token 数",
        [nameof(ThShare)] = "占比",
        [nameof(NoData)] = "暂无数据",
        [nameof(MetaNormalizedAcrossSources)] = "跨来源归一化",
        [nameof(PanelTypeDonut)] = "Token 类型构成",
        [nameof(PanelProjectRanking)] = "项目 / 目录排行",
        [nameof(PanelCostTrend)] = "成本趋势",
        [nameof(MetaEstimateUsd)] = "估算值 · USD",
        [nameof(LoadErrorFormat)] = "加载失败：{0}",
        [nameof(Unknown)] = "未知",
        [nameof(OthersSegment)] = "其他",
        [nameof(TokenTypeInput)] = "输入(未缓存)",
        [nameof(TokenTypeOutput)] = "输出",
        [nameof(TokenTypeCache)] = "缓存相关",

        // ── 会话明细页（SessionsView / SessionsViewModel）──
        [nameof(SearchPlaceholder)] = "搜索会话标识、项目、模型或 Agent…",
        [nameof(SortByTokens)] = "按 Token 排序",
        [nameof(SortByCost)] = "按成本排序",
        [nameof(SortByRecent)] = "最近活动",
        [nameof(ThSession)] = "会话",
        [nameof(ThLastActivity)] = "最后活动",
        [nameof(NoMatchingSessions)] = "没有匹配的会话",
        [nameof(LoadMore)] = "加载更多",
        [nameof(CountTextFormat)] = "显示 {0} / {1} 条",
        [nameof(LoadMoreErrorFormat)] = "加载更多失败：{0}",
        [nameof(RelTimeNow)] = "刚刚",
        [nameof(RelTimeMinutesAgo)] = "{0} 分钟前",
        [nameof(RelTimeHoursAgo)] = "{0} 小时前",
        [nameof(RelTimeDaysAgo)] = "{0} 天前",

        // ── Agent 统计页（AgentsView / AgentsViewModel）──
        [nameof(MetaClickableForSessions)] = "可点击查看对应会话",
        [nameof(PanelAgentRanking)] = "Agent 排行",
        [nameof(ThSessionCount)] = "会话数",
        [nameof(ThInputTokens)] = "输入 Token",
        [nameof(ThOutputTokens)] = "输出 Token",
        [nameof(ThTotalTokens)] = "总 Token",
        [nameof(AgentKpiFootFormat)] = "{0} 会话 · {1}",

        // ── 模型统计页（ModelsView / ModelsViewModel）──
        [nameof(MetaMissingPriceUnknown)] = "价格缺失时成本应标记为未知，而非 0",
        [nameof(PanelModelDetails)] = "模型明细",
        [nameof(ThAssociatedAgents)] = "关联 Agent",
        [nameof(ThCacheRead)] = "缓存读取",
        [nameof(ThCost)] = "成本",
        [nameof(TooltipUnknownCostChip)] = "模型价格缺失，成本未知；不计入合计",
        [nameof(AssociatedAgentsMultiFormat)] = "{0} 等 {1} 个",
        [nameof(ProviderSingleFormat)] = "服务商：{0}",
        [nameof(ProviderMultiFormat)] = "服务商：{0} 个",
        [nameof(TotalsModelCountFormat)] = "合计（{0} 个模型）",
        [nameof(TotalsUnknownNoteFormat)] = "{0} 个模型成本未知（缺价）：其成本未计入合计，Token 已计入总量。",

        // ── 模型目录（I-035：价格 / 厂商 / 图标的唯一出处）──
        [nameof(CatalogPanelTitle)] = "模型目录",
        [nameof(CatalogPanelHint)] = "每个模型一个稳定 ID，价格、厂商、图标都挂在它上面；数据里出现过的模型名关联到这里（自动关联不上的在下面手动指定），统计与计价都按同一份目录走。改完即按新价格重算。",
        [nameof(ModelSearchPlaceholder)] = "搜索模型 ID / 显示名 / 厂商",
        [nameof(FilterChipBuiltin)] = "内置",
        [nameof(FilterChipRemote)] = "在线",
        [nameof(FilterChipUser)] = "自定义",
        [nameof(FilterChipOverridden)] = "已覆盖",
        [nameof(CatalogNoMatch)] = "没有匹配的条目",
        [nameof(CatalogEmpty)] = "目录为空（内置价格快照缺失）",
        [nameof(ThModelId)] = "模型 ID",
        [nameof(ThModelVendor)] = "厂商",
        [nameof(ThModelPrice)] = "单价（输入 / 输出）",
        [nameof(ThModelOrigin)] = "来源",
        [nameof(ThModelActions)] = "操作",
        [nameof(ModelPriceMissing)] = "未定价",
        [nameof(ModelOriginBuiltin)] = "内置",
        [nameof(ModelOriginRemote)] = "在线",
        [nameof(ModelOriginUser)] = "自定义",
        [nameof(ModelOverriddenBadge)] = "已改",
        [nameof(ModelFormCreate)] = "新增模型",
        [nameof(ModelFormEditFormat)] = "编辑：{0}",
        [nameof(ModelFormIdLabel)] = "模型 ID（自动转小写连字符）",
        [nameof(ModelFormVendorLabel)] = "厂商",
        [nameof(ModelFormDisplayNameLabel)] = "显示名（可选）",
        [nameof(ModelFormInputLabel)] = "输入价（USD / 百万 tokens）",
        [nameof(ModelFormOutputLabel)] = "输出价（USD / 百万 tokens）",
        [nameof(ModelFormCacheWriteLabel)] = "缓存写单价",
        [nameof(ModelFormCacheReadLabel)] = "缓存读单价",
        [nameof(ModelFormCancel)] = "取消",
        [nameof(ModelFormPriceHint)] = "价格可留空（只登记身份）；填则必须两项都填。缓存写 / 读按输入价 × 1.25 / × 0.1 推导。",
        [nameof(ModelFormSave)] = "保存",        [nameof(ModelFormReset)] = "清空",
        [nameof(ModelFormErrorIdInvalid)] = "模型 ID 无效：请用小写字母与数字，段间用连字符（如 glm-4-6）",
        [nameof(ModelFormErrorPricePair)] = "价格要么都填、要么都留空",
        [nameof(ModelFormErrorPriceNumber)] = "价格必须是非负数字",
        [nameof(ModelEditButton)] = "编辑",
        [nameof(ModelRemoveButton)] = "移除覆盖",
        [nameof(ToastRepricing)] = "已保存，正在按新价格重算…",

        // ── 项目统计页（ProjectsView）──
        [nameof(PanelProjectRankingShort)] = "项目排行",
        [nameof(ThAvgPerSession)] = "平均每会话",

        // ── 时间分析页（TimeAnalysisView / TimeViewModel）──
        [nameof(MetaLocalTimePrecise)] = "本地时间 · 仅含带分钟级精度的会话",
        [nameof(PanelHourlyDistribution)] = "按小时分布",
        [nameof(MetaDailyTokenIntensity)] = "每日 Token 强度",
        [nameof(PanelActivityCalendar)] = "活跃日历",
        [nameof(HeatmapLegend)] = "颜色越浓表示当日 Token 越多；淡格为窗口内无数据日。悬停查看当日数值。",
        [nameof(PanelObservations)] = "时间维度建议观察项",
        [nameof(UnknownNoteFormat)] = "{0} 个会话缺少精确时间，未计入小时分布（占 {1}%）",
        [nameof(UnknownNoteTokensSuffix)] = "，涉及 {0} tokens 用量",
        [nameof(TimeObsPeakTitle)] = "高峰时段",
        [nameof(TimeObsPeakDesc)] = "比较小时级 Token 与成本（小时分布仅含带分钟级时间精度的会话），判断是否有长时间连续运行的 Agent。",
        [nameof(TimeObsSessionTitle)] = "会话长度",
        [nameof(TimeObsSessionDesc)] = "结合会话持续时长与 Token 总量判断效率，避免只用 Token 量。事实存储含会话首次 / 最晚活动时间（时长可推导），但部分来源为采集兜底时间戳，时长仅供参考。",
        [nameof(TimeObsBoundaryTitle)] = "数据边界",
        [nameof(TimeObsBoundaryDesc)] = "缺少精确事件时间的来源显示「未知」并不入小时分布（UTC 午夜 / 本地午夜锚定视为无精度），不伪造小时级分布；占比见小时分布图下标注。",

        // ── 成本分析页（CostAnalysisView / CostViewModel）──
        [nameof(KpiTotalCost)] = "估算总成本",
        [nameof(KpiFootKnownCostOnly)] = "已知成本合计 · 未知不计入总额",
        [nameof(KpiInputCostShare)] = "输入成本占比",
        [nameof(KpiOutputCostShare)] = "输出成本占比",
        [nameof(KpiFootTokenShareBasis)] = "按 Token 构成展示 · 成本按方向拆价口径待扩展（缓存单列）",
        [nameof(KpiUnpriced)] = "待确认价格",
        [nameof(KpiFootUnpriced)] = "缺价模型数 · 成本不应默认为零 · 全局口径（自启动累计），不随筛选变化",
        [nameof(KpiFootUnpricedWhere)] = "在「数据与计价 → 模型目录」里给它定价，或把模型名关联到已有条目",
        [nameof(PanelCostBasis)] = "成本计算口径",
        [nameof(ThItem)] = "项目",
        [nameof(ThSuggestedDisplay)] = "建议展示",
        [nameof(ThCaveats)] = "注意事项",
        [nameof(UnpricedCountFormat)] = "{0} 个",
        [nameof(NoUnpricedModels)] = "无缺价模型",
        [nameof(UnknownCostPartialNoteFormat)] = "估算总成本为已知成本下限（${0}+）：存在未计价模型的成本未知，未计入总额（缺价模型见「待确认价格」）。",
        [nameof(UnknownCostAllNote)] = "当前范围成本全部未知：范围内用量的模型均缺价，成本未计入任何合计（绝不折 0）。",
        [nameof(CostBasisRealItem)] = "实际计费",
        [nameof(CostBasisRealStance)] = "优先",
        [nameof(CostBasisRealNote)] = "日志自带 costUSD 时直接采用（与 ccusage 计价优先级一致，对账证据见 docs/reconciliation）；仅在来源提供可靠计费数据时使用。",
        [nameof(CostBasisEstimateItem)] = "估算成本",
        [nameof(CostBasisEstimateStance)] = "模型价格表",
        [nameof(CostBasisEstimateNote)] = "按价格快照估算：用户覆盖 → models.dev 远程数据 → 内置快照三级回退；缓存输入（5m/1h）单独计价。金额为采集时按当次命中的价格来源计算，页面不另行折算。",
        [nameof(CostBasisUnknownItem)] = "未知成本",
        [nameof(CostBasisUnknownStance)] = "不计入总额或单独标注",
        [nameof(CostBasisUnknownNote)] = "价格缺失的模型保留「未知」，绝不折 0 混入合计；缺价清单见「待确认价格」，逐模型明细见模型统计页。",

        // ── 图表控件（TrendChart / Heatmap / DonutChart）──
        [nameof(NoUsageInRange)] = "该时间范围内没有用量记录",
        [nameof(HeatmapNoDataTooltip)] = "{0} · 无数据",

        // ── 异常通知（DashboardViewModel → ISystemNotification/toast）──
        [nameof(AnomalyToastTitle)] = "AI 成本异常提醒",
        [nameof(AnomalyCostSpikeFormat)] = "今日成本是过去 {0} 日均值的 {1:0.0} 倍",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(NavOverview)] = "Overview",
        [nameof(NavSessions)] = "Sessions",
        [nameof(NavAgents)] = "Agents",
        [nameof(NavModels)] = "Models",
        [nameof(NavProjects)] = "Projects",
        [nameof(NavTime)] = "Time",
        [nameof(NavCost)] = "Cost",
        [nameof(NavDataPricing)] = "Data & pricing",
        [nameof(NavSettings)] = "Settings",
        [nameof(OverviewTitle)] = "Token usage overview",
        [nameof(OverviewSubtitle)] = "Usage, cost and workload across agents, in one place",
        [nameof(SessionsTitle)] = "Session details",
        [nameof(SessionsSubtitle)] = "Drill down to individual agent sessions with search and combined filters",
        [nameof(AgentsTitle)] = "Agent statistics",
        [nameof(AgentsSubtitle)] = "Compare usage intensity, cost and session size across tools",
        [nameof(ModelsTitle)] = "Model statistics",
        [nameof(ModelsSubtitle)] = "Usage and cost per model — price differences stay visible, not hidden in totals",
        [nameof(ProjectsTitle)] = "Project statistics",
        [nameof(ProjectsSubtitle)] = "Aggregated by working directory to see which codebases or tasks consume tokens",
        [nameof(TimeTitle)] = "Time analysis",
        [nameof(TimeSubtitle)] = "Spot peak hours, working-time distribution and long sessions",
        [nameof(CostTitle)] = "Cost analysis",
        [nameof(CostSubtitle)] = "Separate real billing, price-table estimates and unpriced data",
        [nameof(DataPricingTitle)] = "Data & pricing",
        [nameof(DataPricingSubtitle)] = "Source adapters, deduplication and transparent methodology are what make cross-agent stats trustworthy",
        [nameof(SettingsTitle)] = "Settings",
        [nameof(SettingsSubtitle)] = "Theme, language and refresh preferences; every change applies and saves automatically",

        [nameof(TopbarTimeRange)] = "Range",
        [nameof(TopbarAgent)] = "Agent",
        [nameof(StatusStarting)] = "Starting…",
        [nameof(StatusScanning)] = "Scanning local data…",
        [nameof(StatusNoLocalData)] = "No local data found",
        [nameof(StatusSourceError)] = "Data source error (refresh failed this round)",
        [nameof(StatusReady)] = "Local data source ready",
        [nameof(NotRefreshedYet)] = "Not refreshed yet",
        [nameof(LastUpdatedFormat)] = "Updated {0:HH:mm}",
        [nameof(TooltipRefreshNow)] = "Refresh local data now",

        // ── First-run initialization overlay (MainWindow.axaml + MainWindowViewModel) ──
        [nameof(InitializingTitle)] = "Initializing data…",
        [nameof(InitializingSubtitle)] = "Scanning local agent session logs on first launch; data will appear automatically — no manual refresh needed",

        [nameof(RecentSessions)] = "Recent sessions",
        [nameof(ViewAll)] = "View all ↗",
        [nameof(NoRecentSessions)] = "No recent sessions",
        [nameof(QuickFilter)] = "Quick filters",
        [nameof(ClearFilters)] = "Clear",
        [nameof(LabelModels)] = "Models",
        [nameof(LabelProjectDir)] = "Project / directory",
        [nameof(ApplyFilters)] = "Apply filters",
        [nameof(RecentErrorFormat)] = "Failed to load recent sessions: {0}",
        [nameof(ToastFiltersAppliedFormat)] = "Filters applied: {0} agents · {1} · {2}",
        [nameof(ToastFiltersCleared)] = "Filters cleared",
        [nameof(ToastSessionPlannedFormat)] = "Session {0}: detail view is planned",

        [nameof(TimeRangeToday)] = "Today",
        [nameof(TimeRange7)] = "Last 7 days",
        [nameof(TimeRange30)] = "Last 30 days",
        [nameof(TimeRange90)] = "Last 90 days",
        [nameof(TimeRangeAll)] = "All time",
        [nameof(TimeRangeCustom)] = "Custom",
        [nameof(AllAgents)] = "All agents",
        [nameof(AllModels)] = "All models",
        [nameof(AllProjects)] = "All projects",

        [nameof(TrayTooltip)] = "Tokenmetry — token cost monitor",
        [nameof(TrayQuickInfo)] = "Quick info panel",
        [nameof(TrayCostFormat)] = "Today: ${0:F2}",
        [nameof(TrayCostNoData)] = "Today: — (no data yet)",
        [nameof(TrayOpenWindow)] = "Open window",
        [nameof(TrayRefreshNow)] = "Refresh now",
        [nameof(TraySettings)] = "Settings",
        [nameof(TrayExit)] = "Quit",
        [nameof(TrayHintTitle)] = "Minimized to the tray",
        [nameof(TrayHintBody)] = "Tokenmetry keeps running in the system tray and background refresh continues. Reopen the window from the tray menu or quit there; this notice shows once, and the close behavior can be changed in Settings.",
        [nameof(TrayHintOk)] = "Got it",

        // ── 状态栏快捷信息窗（D-015）──
        [nameof(PopoverLiveIdle)] = "Live",
        [nameof(PopoverLiveActive)] = "Working",
        [nameof(PopoverLiveWarn)] = "Alert",
        [nameof(PopoverScanning)] = "Scanning…",
        [nameof(PopoverTodayTokens)] = "Today's tokens",
        [nameof(PopoverTodayTokensCap)] = "Across all agents",
        [nameof(PopoverCacheHit)] = "Cache hit",
        [nameof(PopoverCacheCaption)] = "cache read",
        [nameof(PopoverSegCache)] = "Cache",
        [nameof(PopoverTopAgents)] = "Top agents",
        [nameof(PopoverRecentSessions)] = "Recent sessions →",
        [nameof(PopoverOpenStats)] = "Open full stats →",
        [nameof(PopoverActiveBannerTitleFormat)] = "{0} is working",
        [nameof(PopoverActiveBannerTitle)] = "Working now",
        [nameof(PopoverActiveBannerSubtitleFormat)] = "Current rate {0} · from refresh delta",
        [nameof(PopoverRowRateFormat)] = "Now {0}",
        [nameof(PopoverRowLastActiveFormat)] = "Last activity {0:HH:mm}",

        [nameof(SourcesPanelTitle)] = "Recognized data sources",
        [nameof(ThSource)] = "Source",
        [nameof(ThStatus)] = "Status",
        [nameof(ThLastScan)] = "Last scan",
        [nameof(ThRecords)] = "Records",
        [nameof(SourceMetaIdle)] = "Status and record counts come from the last ingestion scan · counts are cumulative",
        [nameof(SourceMetaScanning)] = "Scanning… (source by source; everything refreshes when done)",
        [nameof(SourceMetaScanFailed)] = "Last scan did not finish — the table below is the most recent successful snapshot and may lag behind",
        [nameof(ScanIdle)] = "Rescan (all sources)",
        [nameof(ScanRunning)] = "Scanning…",
        [nameof(StatusOk)] = "● Connected",
        [nameof(StatusPartial)] = "● Partial data",
        [nameof(StatusNoData)] = "● No data",
        [nameof(StatusError)] = "● Scan failed (skipped this round)",
        [nameof(StatusNotScanned)] = "● Not scanned yet",
        [nameof(CaliberTitle)] = "Methodology",
        [nameof(BadgeAlways)] = "Always on",
        [nameof(BadgeNotProvided)] = "Not offered",
        [nameof(CaliberDedupText)] = "Deduplicate by agent + session ID",
        [nameof(CaliberDedupNote)] = "Message-level dedup and idempotent upsert of session facts by agent + session_id — built into the ingestion pipeline",
        [nameof(CaliberSplitText)] = "Split input, output, cache read and cache write",
        [nameof(CaliberSplitNote)] = "Unified records and the facts store always split tokens into these four directions",
        [nameof(CaliberUnknownPriceText)] = "Unknown model prices show as “unknown”",
        [nameof(CaliberUnknownPriceNote)] = "Models without pricing are explicitly marked unknown and never folded into 0 (unknown-cost discipline)",
        [nameof(CaliberMergeText)] = "Auto-merge suspected same tasks across agents (use with care)",
        [nameof(CaliberMergeNote)] = "Cross-agent task merging is not implemented; the prototype toggle was dropped rather than ship a switch with no effect",
        [nameof(CaliberLocalOnlyText)] = "All raw records are processed locally by default",
        [nameof(CaliberLocalOnlyNote)] = "Adapters read local logs and ledgers directly; only the model price table is fetched online — raw records never leave this machine",
        [nameof(AliasPanelTitle)] = "Model aggregation (aliases)",
        [nameof(AliasPanelHint)] = "Sources name the same model differently (case, vendor prefixes, aliases — GLM-5.3-Flash / glm-5.3-flash / ark/glm-5.3-flash). Mapping a raw model name (exact match, as it appears in local data) to a canonical name merges them into one row in model statistics and distribution; reopen a statistics page to see the new mapping.",
        [nameof(AliasEmpty)] = "No mapping rules yet",
        [nameof(AliasSourceLabel)] = "Raw model name",
        [nameof(AliasTargetLabel)] = "Canonical name (display name)",
        [nameof(AliasSourcePlaceholder)] = "Type freely or pick a candidate, e.g. ark/glm-5.3-flash",
        [nameof(AliasTargetPlaceholder)] = "e.g. glm-5.3-flash",
        [nameof(AliasAdd)] = "＋ Add mapping",
        [nameof(AliasRemove)] = "Remove",
        [nameof(AliasErrorRequired)] = "Both the raw name and the canonical name are required",
        [nameof(AliasErrorSame)] = "Raw name and canonical name are identical — no mapping needed",
        [nameof(AliasErrorTargetUnknown)] = "The target model is not in the catalog — pick an existing model ID from the candidates",
        [nameof(AliasCandidatesHint)] = "Candidates come from model names seen in local data (ordered by usage); you can also type any string to pre-map a name that has not appeared yet.",
        [nameof(AppearanceTitle)] = "Appearance & general",
        [nameof(LabelTheme)] = "Theme",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(ThemeSystem)] = "Follow system",
        [nameof(ThemeHint)] = "Applies and saves automatically (kept across restarts).",
        [nameof(LabelLanguage)] = "Language",
        [nameof(LanguageSystem)] = "Follow system",
        [nameof(LanguageZh)] = "简体中文",
        [nameof(LanguageEn)] = "English",
        [nameof(LanguageHint)] = "Applies and saves automatically (kept across restarts).",
        [nameof(LabelRefreshInterval)] = "Refresh interval (minutes)",
        [nameof(RefreshIntervalMustBeNumber)] = "Refresh interval must be a number",
        [nameof(RefreshIntervalOutOfRange)] = "Refresh interval must be between 1 and 1440 minutes (current value kept)",
        [nameof(RefreshHint)] = "Saved automatically; takes effect the next time the background refresh service starts.",
        [nameof(LabelCcusageCli)] = "ccusage CLI (reconciliation / manual import)",
        [nameof(CcusageHint)] = "The app reads local session logs directly — ccusage is not required; this command is only used for reconciliation / manual import and takes effect on next start.",
        [nameof(CheckAnomalyNotifications)] = "Enable anomaly notifications (in-app toast)",
        [nameof(CheckMinimizeToTrayOnClose)] = "Minimize to the system tray on close",
        [nameof(CheckMinimizeToTrayOnCloseHint)] = "After closing, the app keeps running in the tray with scheduled refresh; quit via the tray menu. When off, the close button exits the app.",
        [nameof(LocalPathsTitle)] = "Local paths",
        [nameof(LabelCacheDb)] = "Cache database",
        [nameof(LabelSettingsFile)] = "Settings file",
        [nameof(ToastScanDone)] = "Scan finished",
        [nameof(ToastScanFailed)] = "Scan failed: the table keeps the most recent successful snapshot",

        [nameof(StartupErrorTitle)] = "Tokenmetry failed to start",
        [nameof(StartupErrorExit)] = "Exit",
        [nameof(StartupErrorBodyFormat)] = "Local data initialization failed: {0}\n\nCache database location: {1}\n\nYou can delete that file and retry — historical usage will be re-scanned from local session logs.",

        [nameof(KpiTotalTokens)] = "Total tokens",
        [nameof(KpiEstimatedCost)] = "Estimated cost",
        [nameof(KpiSessions)] = "Sessions",
        [nameof(KpiCacheShare)] = "Cache-read share",
        [nameof(KpiFootVsPrevious)] = "vs previous period",
        [nameof(KpiFootPriceEstimate)] = "Estimated from model prices",
        [nameof(KpiFootAcrossAgents)] = "Aggregated across agents",
        [nameof(KpiFootCacheOfInput)] = "Cache reads / input-related tokens",
        [nameof(LegendDaily)] = "Daily",
        [nameof(LegendInput)] = "Input",
        [nameof(LegendOutput)] = "Output",
        [nameof(LegendTotal)] = "Total",
        [nameof(PanelTrend)] = "Token usage trend",
        [nameof(MetaByTotalTokens)] = "By total tokens",
        [nameof(PanelAgentDonut)] = "Agent share",
        [nameof(ViewDetails)] = "View details ↗",
        [nameof(PanelModelDistribution)] = "Model distribution",
        [nameof(ThModel)] = "Model",
        [nameof(ThTokenCount)] = "Tokens",
        [nameof(ThShare)] = "Share",
        [nameof(NoData)] = "No data",
        [nameof(MetaNormalizedAcrossSources)] = "Normalized across sources",
        [nameof(PanelTypeDonut)] = "Token type mix",
        [nameof(PanelProjectRanking)] = "Project / directory ranking",
        [nameof(PanelCostTrend)] = "Cost trend",
        [nameof(MetaEstimateUsd)] = "Estimate · USD",
        [nameof(LoadErrorFormat)] = "Failed to load: {0}",
        [nameof(Unknown)] = "Unknown",
        [nameof(OthersSegment)] = "Others",
        [nameof(TokenTypeInput)] = "Input (uncached)",
        [nameof(TokenTypeOutput)] = "Output",
        [nameof(TokenTypeCache)] = "Cache-related",

        [nameof(SearchPlaceholder)] = "Search by session id, project, model or agent…",
        [nameof(SortByTokens)] = "By tokens",
        [nameof(SortByCost)] = "By cost",
        [nameof(SortByRecent)] = "Recent activity",
        [nameof(ThSession)] = "Session",
        [nameof(ThLastActivity)] = "Last activity",
        [nameof(NoMatchingSessions)] = "No matching sessions",
        [nameof(LoadMore)] = "Load more",
        [nameof(CountTextFormat)] = "Showing {0} / {1}",
        [nameof(LoadMoreErrorFormat)] = "Failed to load more: {0}",
        [nameof(RelTimeNow)] = "just now",
        [nameof(RelTimeMinutesAgo)] = "{0} min ago",
        [nameof(RelTimeHoursAgo)] = "{0} h ago",
        [nameof(RelTimeDaysAgo)] = "{0} d ago",

        [nameof(MetaClickableForSessions)] = "Click a row to see its sessions",
        [nameof(PanelAgentRanking)] = "Agent ranking",
        [nameof(ThSessionCount)] = "Sessions",
        [nameof(ThInputTokens)] = "Input tok.",
        [nameof(ThOutputTokens)] = "Output tok.",
        [nameof(ThTotalTokens)] = "Total tok.",
        [nameof(AgentKpiFootFormat)] = "{0} sessions · {1}",

        [nameof(MetaMissingPriceUnknown)] = "Missing prices show cost as unknown, not 0",
        [nameof(PanelModelDetails)] = "Model details",
        [nameof(ThAssociatedAgents)] = "Agents",
        [nameof(ThCacheRead)] = "Cache read",
        [nameof(ThCost)] = "Cost",
        [nameof(TooltipUnknownCostChip)] = "Model price missing — cost unknown; excluded from totals",
        [nameof(AssociatedAgentsMultiFormat)] = "{0} +{1} more",
        [nameof(ProviderSingleFormat)] = "Provider: {0}",
        [nameof(ProviderMultiFormat)] = "Providers: {0}",
        [nameof(TotalsModelCountFormat)] = "Total ({0} models)",
        [nameof(TotalsUnknownNoteFormat)] = "{0} models have unknown (unpriced) cost: excluded from the cost total, included in token totals.",

        [nameof(PanelProjectRankingShort)] = "Project ranking",
        [nameof(ThAvgPerSession)] = "Avg / session",

        [nameof(MetaLocalTimePrecise)] = "Local time · sessions with minute-level precision only",
        [nameof(PanelHourlyDistribution)] = "Hourly distribution",
        [nameof(MetaDailyTokenIntensity)] = "Daily token intensity",
        [nameof(PanelActivityCalendar)] = "Activity calendar",
        [nameof(HeatmapLegend)] = "Darker means more tokens that day; pale cells are days without data in the window. Hover for the value.",
        [nameof(PanelObservations)] = "Suggested time-dimension observations",
        [nameof(UnknownNoteFormat)] = "{0} sessions lack precise time and are excluded from the hourly distribution ({1}%)",
        [nameof(UnknownNoteTokensSuffix)] = ", involving {0} tokens of usage",
        [nameof(TimeObsPeakTitle)] = "Peak hours",
        [nameof(TimeObsPeakDesc)] = "Compare hourly tokens and cost (the hourly distribution only includes sessions with minute-level time precision) to spot agents that run for long stretches.",
        [nameof(TimeObsSessionTitle)] = "Session length",
        [nameof(TimeObsSessionDesc)] = "Judge efficiency by combining session duration with total tokens rather than token count alone. The facts store has first/last activity times (duration is derivable), but some sources fall back to ingestion timestamps — treat duration as approximate.",
        [nameof(TimeObsBoundaryTitle)] = "Data boundaries",
        [nameof(TimeObsBoundaryDesc)] = "Sources without precise event times show as “unknown” and are excluded from the hourly distribution (UTC-midnight / local-midnight anchoring counts as no precision) — no fabricated hourly distribution; see the note under the chart for the share.",

        [nameof(KpiTotalCost)] = "Estimated total cost",
        [nameof(KpiFootKnownCostOnly)] = "Sum of known cost · unknown cost excluded from totals",
        [nameof(KpiInputCostShare)] = "Input cost share",
        [nameof(KpiOutputCostShare)] = "Output cost share",
        [nameof(KpiFootTokenShareBasis)] = "Shown by token mix · per-direction price split is planned (cache listed separately)",
        [nameof(KpiUnpriced)] = "Unpriced models",
        [nameof(KpiFootUnpriced)] = "Models without prices · cost must not default to zero · global scope (accumulated since startup), does not follow filters",
        [nameof(KpiFootUnpricedWhere)] = "Set a price in Data & pricing → Model catalog, or link the model name to an existing entry",
        [nameof(PanelCostBasis)] = "Cost calculation basis",
        [nameof(ThItem)] = "Item",
        [nameof(ThSuggestedDisplay)] = "Suggested display",
        [nameof(ThCaveats)] = "Caveats",
        [nameof(UnpricedCountFormat)] = "{0}",
        [nameof(NoUnpricedModels)] = "No unpriced models",
        [nameof(UnknownCostPartialNoteFormat)] = "Estimated total cost is the known-cost floor (${0}+): some models are unpriced and their cost is unknown — excluded from totals (see “Unpriced models”).",
        [nameof(UnknownCostAllNote)] = "All cost in this range is unknown: every model with usage here lacks prices, so no cost was added to any total (never folded into 0).",
        [nameof(CostBasisRealItem)] = "Billed cost",
        [nameof(CostBasisRealStance)] = "Preferred",
        [nameof(CostBasisRealNote)] = "Used directly when logs carry costUSD (same priority as ccusage; reconciliation evidence in docs/reconciliation); only used when the source provides reliable billing data.",
        [nameof(CostBasisEstimateItem)] = "Estimated cost",
        [nameof(CostBasisEstimateStance)] = "Model price table",
        [nameof(CostBasisEstimateNote)] = "Estimated from the price snapshot with three-level fallback: user overrides → models.dev remote data → built-in snapshot; cache input (5m/1h) priced separately. Amounts are computed at ingestion with the price source hit at the time — the page does not re-convert them.",
        [nameof(CostBasisUnknownItem)] = "Unknown cost",
        [nameof(CostBasisUnknownStance)] = "Excluded or labeled",
        [nameof(CostBasisUnknownNote)] = "Unpriced models keep an “unknown” cost and are never folded into totals as 0; see “Unpriced models” for the list and the models page for per-model details.",

        [nameof(NoUsageInRange)] = "No usage records in this range",
        [nameof(HeatmapNoDataTooltip)] = "{0} · no data",

        [nameof(AnomalyToastTitle)] = "AI cost anomaly alert",
        [nameof(AnomalyCostSpikeFormat)] = "Today's cost is {1:0.0}× the {0}-day average",

        // ── 模型目录（I-035 搜索 / 筛选；既有目录键暂无英文覆盖，zh 兜底）──
        [nameof(ModelSearchPlaceholder)] = "Search model ID / display name / vendor",
        [nameof(FilterChipBuiltin)] = "Built-in",
        [nameof(FilterChipRemote)] = "Remote",
        [nameof(FilterChipUser)] = "Custom",
        [nameof(FilterChipOverridden)] = "Overridden",
        [nameof(CatalogNoMatch)] = "No matching entries",
        [nameof(ModelFormCacheWriteLabel)] = "Cache write price",
        [nameof(ModelFormCacheReadLabel)] = "Cache read price",
        [nameof(ModelFormCancel)] = "Cancel",
    };

    private static readonly string[] AllPropertyNames = typeof(Translations)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .Select(p => p.Name)
        .ToArray();

    public static Translations Instance { get; } = new();

    private Dictionary<string, string> _strings;
    private AppLanguage _currentLanguage;

    private Translations()
    {
        _currentLanguage = Resolve(AppLanguage.System, CultureInfo.CurrentUICulture);
        _strings = StringsFor(_currentLanguage);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前生效语言（system 偏好已解析为具体语言）。SetLanguage 对全部属性 raise 时，
    /// 本属性即为订阅方使用的「每次切换一次」信号（同 stupid-dict 约定）。</summary>
    public AppLanguage CurrentLanguage => _currentLanguage;

    /// <summary>切换语言并重建合并字典（对全部属性一次性 raise PropertyChanged；UI 线程调用约定与其他 UI 服务一致）。
    /// 解析结果与当前一致时跳过——启动路径每次 ApplyPreference 都会走到这里，同语言不广播、
    /// 避免无谓的全量重译（构造器已按初始解析结果建好字典）。</summary>
    public void SetLanguage(AppLanguage language)
    {
        var resolved = Resolve(language, CultureInfo.CurrentUICulture);
        if (resolved == _currentLanguage)
        {
            return;
        }

        _strings = StringsFor(resolved);
        _currentLanguage = resolved;
        foreach (var name in AllPropertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>AppSettings.Language 字符串偏好（"zh"/"en"/"system"）→ SetLanguage；未知/空值如实回落 system。</summary>
    public static void ApplyPreference(string? preference)
    {
        var language = (preference?.Trim().ToLowerInvariant()) switch
        {
            "zh" => AppLanguage.SimplifiedChinese,
            "en" => AppLanguage.English,
            _ => AppLanguage.System,
        };
        Instance.SetLanguage(language);
    }

    internal static AppLanguage Resolve(AppLanguage preference, CultureInfo uiCulture) => preference switch
    {
        AppLanguage.SimplifiedChinese => AppLanguage.SimplifiedChinese,
        AppLanguage.English => AppLanguage.English,
        _ => uiCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English,
    };

    /// <summary>zh 为源、en 为覆盖的合并字典：英文缺 key 时回落中文（可视化遗漏由中文串在英文界面出现暴露）。</summary>
    private static Dictionary<string, string> StringsFor(AppLanguage language)
    {
        var merged = new Dictionary<string, string>(Zh);
        if (language == AppLanguage.English)
        {
            foreach (var (key, value) in En)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>兜底：漏登记的 key 显示属性名本身（自暴露遗漏，同 stupid-dict 的 Get 约定）。</summary>
    private string Get([CallerMemberName] string? key = null) =>
        _strings.TryGetValue(key!, out var value) ? value : key!;

    // ── 导航与页面头 ──
    public string NavOverview => Get();
    public string NavSessions => Get();
    public string NavAgents => Get();
    public string NavModels => Get();
    public string NavProjects => Get();
    public string NavTime => Get();
    public string NavCost => Get();
    public string NavDataPricing => Get();
    public string NavSettings => Get();
    public string OverviewTitle => Get();
    public string OverviewSubtitle => Get();
    public string SessionsTitle => Get();
    public string SessionsSubtitle => Get();
    public string AgentsTitle => Get();
    public string AgentsSubtitle => Get();
    public string ModelsTitle => Get();
    public string ModelsSubtitle => Get();
    public string ProjectsTitle => Get();
    public string ProjectsSubtitle => Get();
    public string TimeTitle => Get();
    public string TimeSubtitle => Get();
    public string CostTitle => Get();
    public string CostSubtitle => Get();
    public string DataPricingTitle => Get();
    public string DataPricingSubtitle => Get();
    public string SettingsTitle => Get();
    public string SettingsSubtitle => Get();

    // ── 顶部栏 ──
    public string TopbarTimeRange => Get();
    public string TopbarAgent => Get();
    public string StatusStarting => Get();
    public string StatusScanning => Get();
    public string StatusNoLocalData => Get();
    public string StatusSourceError => Get();
    public string StatusReady => Get();
    public string NotRefreshedYet => Get();
    public string LastUpdatedFormat => Get();
    public string TooltipRefreshNow => Get();

    // ── 首次初始化遮罩 ──
    public string InitializingTitle => Get();
    public string InitializingSubtitle => Get();

    // ── 右栏 ──
    public string RecentSessions => Get();
    public string ViewAll => Get();
    public string NoRecentSessions => Get();
    public string QuickFilter => Get();
    public string ClearFilters => Get();
    public string LabelModels => Get();
    public string LabelProjectDir => Get();
    public string ApplyFilters => Get();
    public string RecentErrorFormat => Get();
    public string ToastFiltersAppliedFormat => Get();
    public string ToastFiltersCleared => Get();
    public string ToastSessionPlannedFormat => Get();

    // ── 全局筛选选项 ──
    public string TimeRangeToday => Get();
    public string TimeRange7 => Get();
    public string TimeRange30 => Get();
    public string TimeRange90 => Get();
    public string TimeRangeAll => Get();
    public string TimeRangeCustom => Get();
    public string AllAgents => Get();
    public string AllModels => Get();
    public string AllProjects => Get();

    // ── 托盘 ──
    public string TrayTooltip => Get();
    public string TrayQuickInfo => Get();
    public string TrayCostFormat => Get();
    public string TrayCostNoData => Get();
    public string TrayOpenWindow => Get();
    public string TrayRefreshNow => Get();
    public string TraySettings => Get();
    public string TrayExit => Get();
    public string TrayHintTitle => Get();
    public string TrayHintBody => Get();
    public string TrayHintOk => Get();

    // ── 状态栏快捷信息窗（D-015）──
    public string PopoverLiveIdle => Get();
    public string PopoverLiveActive => Get();
    public string PopoverLiveWarn => Get();
    public string PopoverScanning => Get();
    public string PopoverTodayTokens => Get();
    public string PopoverTodayTokensCap => Get();
    public string PopoverCacheHit => Get();
    public string PopoverCacheCaption => Get();
    public string PopoverSegCache => Get();
    public string PopoverTopAgents => Get();
    public string PopoverRecentSessions => Get();
    public string PopoverOpenStats => Get();
    public string PopoverActiveBannerTitleFormat => Get();
    public string PopoverActiveBannerTitle => Get();
    public string PopoverActiveBannerSubtitleFormat => Get();
    public string PopoverRowRateFormat => Get();
    public string PopoverRowLastActiveFormat => Get();

    // ── 设置页 ──
    public string SourcesPanelTitle => Get();
    public string ThSource => Get();
    public string ThStatus => Get();
    public string ThLastScan => Get();
    public string ThRecords => Get();
    public string SourceMetaIdle => Get();
    public string SourceMetaScanning => Get();
    public string SourceMetaScanFailed => Get();
    public string ScanIdle => Get();
    public string ScanRunning => Get();
    public string StatusOk => Get();
    public string StatusPartial => Get();
    public string StatusNoData => Get();
    public string StatusError => Get();
    public string StatusNotScanned => Get();
    public string CaliberTitle => Get();
    public string BadgeAlways => Get();
    public string BadgeNotProvided => Get();
    public string CaliberDedupText => Get();
    public string CaliberDedupNote => Get();
    public string CaliberSplitText => Get();
    public string CaliberSplitNote => Get();
    public string CaliberUnknownPriceText => Get();
    public string CaliberUnknownPriceNote => Get();
    public string CaliberMergeText => Get();
    public string CaliberMergeNote => Get();
    public string CaliberLocalOnlyText => Get();
    public string CaliberLocalOnlyNote => Get();
    public string AliasPanelTitle => Get();
    public string AliasPanelHint => Get();
    public string AliasEmpty => Get();
    public string AliasSourceLabel => Get();
    public string AliasTargetLabel => Get();
    public string AliasSourcePlaceholder => Get();
    public string AliasTargetPlaceholder => Get();
    public string AliasAdd => Get();
    public string AliasRemove => Get();
    public string AliasErrorRequired => Get();
    public string AliasErrorSame => Get();
    public string AliasErrorTargetUnknown => Get();
    public string AliasCandidatesHint => Get();
    public string AppearanceTitle => Get();
    public string LabelTheme => Get();
    public string ThemeLight => Get();
    public string ThemeDark => Get();
    public string ThemeSystem => Get();
    public string ThemeHint => Get();
    public string LabelLanguage => Get();
    public string LanguageSystem => Get();
    public string LanguageZh => Get();
    public string LanguageEn => Get();
    public string LanguageHint => Get();
    public string LabelRefreshInterval => Get();
    public string RefreshIntervalMustBeNumber => Get();
    public string RefreshIntervalOutOfRange => Get();
    public string RefreshHint => Get();
    public string LabelCcusageCli => Get();
    public string CcusageHint => Get();
    public string CheckAnomalyNotifications => Get();
    public string CheckMinimizeToTrayOnClose => Get();
    public string CheckMinimizeToTrayOnCloseHint => Get();
    public string LocalPathsTitle => Get();
    public string LabelCacheDb => Get();
    public string LabelSettingsFile => Get();
    public string ToastScanDone => Get();
    public string ToastScanFailed => Get();

    // ── 启动错误窗 ──
    public string StartupErrorTitle => Get();
    public string StartupErrorExit => Get();
    public string StartupErrorBodyFormat => Get();

    // ── 总览页 ──
    public string KpiTotalTokens => Get();
    public string KpiEstimatedCost => Get();
    public string KpiSessions => Get();
    public string KpiCacheShare => Get();
    public string KpiFootVsPrevious => Get();
    public string KpiFootPriceEstimate => Get();
    public string KpiFootAcrossAgents => Get();
    public string KpiFootCacheOfInput => Get();
    public string LegendDaily => Get();
    public string LegendInput => Get();
    public string LegendOutput => Get();
    public string LegendTotal => Get();
    public string PanelTrend => Get();
    public string MetaByTotalTokens => Get();
    public string PanelAgentDonut => Get();
    public string ViewDetails => Get();
    public string PanelModelDistribution => Get();
    public string ThModel => Get();
    public string ThTokenCount => Get();
    public string ThShare => Get();
    public string NoData => Get();
    public string MetaNormalizedAcrossSources => Get();
    public string PanelTypeDonut => Get();
    public string PanelProjectRanking => Get();
    public string PanelCostTrend => Get();
    public string MetaEstimateUsd => Get();
    public string LoadErrorFormat => Get();
    public string Unknown => Get();
    public string OthersSegment => Get();
    public string TokenTypeInput => Get();
    public string TokenTypeOutput => Get();
    public string TokenTypeCache => Get();

    // ── 会话明细页 ──
    public string SearchPlaceholder => Get();
    public string SortByTokens => Get();
    public string SortByCost => Get();
    public string SortByRecent => Get();
    public string ThSession => Get();
    public string ThLastActivity => Get();
    public string NoMatchingSessions => Get();
    public string LoadMore => Get();
    public string CountTextFormat => Get();
    public string LoadMoreErrorFormat => Get();
    public string RelTimeNow => Get();
    public string RelTimeMinutesAgo => Get();
    public string RelTimeHoursAgo => Get();
    public string RelTimeDaysAgo => Get();

    // ── Agent 统计页 ──
    public string MetaClickableForSessions => Get();
    public string PanelAgentRanking => Get();
    public string ThSessionCount => Get();
    public string ThInputTokens => Get();
    public string ThOutputTokens => Get();
    public string ThTotalTokens => Get();
    public string AgentKpiFootFormat => Get();

    // ── 模型统计页 ──
    public string MetaMissingPriceUnknown => Get();
    public string PanelModelDetails => Get();
    public string ThAssociatedAgents => Get();
    public string ThCacheRead => Get();
    public string ThCost => Get();
    public string TooltipUnknownCostChip => Get();
    public string AssociatedAgentsMultiFormat => Get();
    public string ProviderSingleFormat => Get();
    public string ProviderMultiFormat => Get();
    public string TotalsModelCountFormat => Get();
    public string TotalsUnknownNoteFormat => Get();

    // ── 模型目录（I-035）──
    public string CatalogPanelTitle => Get();
    public string CatalogPanelHint => Get();
    public string CatalogEmpty => Get();
    public string ModelSearchPlaceholder => Get();
    public string FilterChipBuiltin => Get();
    public string FilterChipRemote => Get();
    public string FilterChipUser => Get();
    public string FilterChipOverridden => Get();
    public string CatalogNoMatch => Get();
    public string ThModelId => Get();
    public string ThModelVendor => Get();
    public string ThModelPrice => Get();
    public string ThModelOrigin => Get();
    public string ThModelActions => Get();
    public string ModelPriceMissing => Get();
    public string ModelOriginBuiltin => Get();
    public string ModelOriginRemote => Get();
    public string ModelOriginUser => Get();
    public string ModelOverriddenBadge => Get();
    public string ModelFormCreate => Get();
    public string ModelFormEditFormat => Get();
    public string ModelFormIdLabel => Get();
    public string ModelFormVendorLabel => Get();
    public string ModelFormDisplayNameLabel => Get();
    public string ModelFormInputLabel => Get();
    public string ModelFormOutputLabel => Get();
    public string ModelFormCacheWriteLabel => Get();
    public string ModelFormCacheReadLabel => Get();
    public string ModelFormCancel => Get();
    public string ModelFormPriceHint => Get();
    public string ModelFormSave => Get();
    public string ModelFormReset => Get();
    public string ModelFormErrorIdInvalid => Get();
    public string ModelFormErrorPricePair => Get();
    public string ModelFormErrorPriceNumber => Get();
    public string ModelEditButton => Get();
    public string ModelRemoveButton => Get();
    public string ToastRepricing => Get();

    // ── 项目统计页 ──
    public string PanelProjectRankingShort => Get();
    public string ThAvgPerSession => Get();

    // ── 时间分析页 ──
    public string MetaLocalTimePrecise => Get();
    public string PanelHourlyDistribution => Get();
    public string MetaDailyTokenIntensity => Get();
    public string PanelActivityCalendar => Get();
    public string HeatmapLegend => Get();
    public string PanelObservations => Get();
    public string UnknownNoteFormat => Get();
    public string UnknownNoteTokensSuffix => Get();
    public string TimeObsPeakTitle => Get();
    public string TimeObsPeakDesc => Get();
    public string TimeObsSessionTitle => Get();
    public string TimeObsSessionDesc => Get();
    public string TimeObsBoundaryTitle => Get();
    public string TimeObsBoundaryDesc => Get();

    // ── 成本分析页 ──
    public string KpiTotalCost => Get();
    public string KpiFootKnownCostOnly => Get();
    public string KpiInputCostShare => Get();
    public string KpiOutputCostShare => Get();
    public string KpiFootTokenShareBasis => Get();
    public string KpiUnpriced => Get();
    public string KpiFootUnpriced => Get();
    public string KpiFootUnpricedWhere => Get();
    public string PanelCostBasis => Get();
    public string ThItem => Get();
    public string ThSuggestedDisplay => Get();
    public string ThCaveats => Get();
    public string UnpricedCountFormat => Get();
    public string NoUnpricedModels => Get();
    public string UnknownCostPartialNoteFormat => Get();
    public string UnknownCostAllNote => Get();
    public string CostBasisRealItem => Get();
    public string CostBasisRealStance => Get();
    public string CostBasisRealNote => Get();
    public string CostBasisEstimateItem => Get();
    public string CostBasisEstimateStance => Get();
    public string CostBasisEstimateNote => Get();
    public string CostBasisUnknownItem => Get();
    public string CostBasisUnknownStance => Get();
    public string CostBasisUnknownNote => Get();

    // ── 图表控件 ──
    public string NoUsageInRange => Get();
    public string HeatmapNoDataTooltip => Get();

    // ── 异常通知 ──
    public string AnomalyToastTitle => Get();
    public string AnomalyCostSpikeFormat => Get();
}
