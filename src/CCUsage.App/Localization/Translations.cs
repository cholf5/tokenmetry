using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CCUsage.App.Localization;

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
        [nameof(NavSettings)] = "数据源与设置",
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
        [nameof(SettingsTitle)] = "数据源与设置",
        [nameof(SettingsSubtitle)] = "多 Agent 统一统计的关键是来源适配、去重和口径透明",

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
        [nameof(TimeRange7)] = "最近 7 天",
        [nameof(TimeRange30)] = "最近 30 天",
        [nameof(TimeRange90)] = "最近 90 天",
        [nameof(TimeRangeAll)] = "全部时间",
        [nameof(AllAgents)] = "所有 Agent",
        [nameof(AllModels)] = "全部模型",
        [nameof(AllProjects)] = "全部项目",

        // ── 托盘（TrayService）──
        [nameof(TrayTooltip)] = "ccusage — Token 成本监控",
        [nameof(TrayCostFormat)] = "今日成本：${0:F2}",
        [nameof(TrayCostNoData)] = "今日成本：—（尚无数据）",
        [nameof(TrayOpenWindow)] = "打开主窗",
        [nameof(TrayRefreshNow)] = "立即刷新",
        [nameof(TraySettings)] = "设置",
        [nameof(TrayExit)] = "退出",

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
        [nameof(AppearanceTitle)] = "外观与通用",
        [nameof(LabelTheme)] = "主题",
        [nameof(ThemeLight)] = "日间",
        [nameof(ThemeDark)] = "夜间",
        [nameof(ThemeSystem)] = "跟随系统",
        [nameof(ThemeHint)] = "切换后立即预览；选择「保存设置」持久化（重启后保持）。",
        [nameof(LabelLanguage)] = "界面语言",
        [nameof(LanguageSystem)] = "跟随系统",
        [nameof(LanguageZh)] = "简体中文",
        [nameof(LanguageEn)] = "English",
        [nameof(LanguageHint)] = "切换后立即生效；选择「保存设置」持久化（重启后保持）。",
        [nameof(LabelRefreshInterval)] = "刷新间隔（分钟）",
        [nameof(RefreshHint)] = "保存后于下次启动后台刷新服务时生效。",
        [nameof(LabelCcusageCli)] = "ccusage CLI（对账 / 手动导入）",
        [nameof(CcusageHint)] = "应用默认直读本地会话日志，无需安装 ccusage；此命令仅对账 / 手动导入时使用，保存后下次启动生效。",
        [nameof(CheckAnomalyNotifications)] = "启用异常通知（应用内 toast 提示）",
        [nameof(LocalPathsTitle)] = "本地路径",
        [nameof(LabelCacheDb)] = "缓存数据库",
        [nameof(LabelSettingsFile)] = "设置文件",
        [nameof(SaveSettings)] = "保存设置",
        [nameof(ToastScanDone)] = "扫描完成",
        [nameof(ToastScanFailed)] = "扫描失败：数据表保持最近一次成功快照",
        [nameof(ToastSaved)] = "设置已保存",
        [nameof(ToastSaveFailedFormat)] = "保存失败：{0}",

        // ── 启动错误窗（App.ShowStartupError，A-7）──
        [nameof(StartupErrorTitle)] = "ccusage 启动失败",
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
        [nameof(TotalsModelCountFormat)] = "合计（{0} 个模型）",
        [nameof(TotalsUnknownNoteFormat)] = "{0} 个模型成本未知（缺价）：其成本未计入合计，Token 已计入总量。",

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
        [nameof(NavSettings)] = "Data & settings",
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
        [nameof(SettingsTitle)] = "Data & settings",
        [nameof(SettingsSubtitle)] = "Source adapters, deduplication and transparent methodology are what make cross-agent stats trustworthy",

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

        [nameof(TimeRange7)] = "Last 7 days",
        [nameof(TimeRange30)] = "Last 30 days",
        [nameof(TimeRange90)] = "Last 90 days",
        [nameof(TimeRangeAll)] = "All time",
        [nameof(AllAgents)] = "All agents",
        [nameof(AllModels)] = "All models",
        [nameof(AllProjects)] = "All projects",

        [nameof(TrayTooltip)] = "ccusage — token cost monitor",
        [nameof(TrayCostFormat)] = "Today: ${0:F2}",
        [nameof(TrayCostNoData)] = "Today: — (no data yet)",
        [nameof(TrayOpenWindow)] = "Open window",
        [nameof(TrayRefreshNow)] = "Refresh now",
        [nameof(TraySettings)] = "Settings",
        [nameof(TrayExit)] = "Quit",

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
        [nameof(AppearanceTitle)] = "Appearance & general",
        [nameof(LabelTheme)] = "Theme",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(ThemeSystem)] = "Follow system",
        [nameof(ThemeHint)] = "Preview applies immediately; choose “Save settings” to persist (kept across restarts).",
        [nameof(LabelLanguage)] = "Language",
        [nameof(LanguageSystem)] = "Follow system",
        [nameof(LanguageZh)] = "简体中文",
        [nameof(LanguageEn)] = "English",
        [nameof(LanguageHint)] = "Applies immediately; choose “Save settings” to persist (kept across restarts).",
        [nameof(LabelRefreshInterval)] = "Refresh interval (minutes)",
        [nameof(RefreshHint)] = "Takes effect the next time the background refresh service starts.",
        [nameof(LabelCcusageCli)] = "ccusage CLI (reconciliation / manual import)",
        [nameof(CcusageHint)] = "The app reads local session logs directly — ccusage is not required; this command is only used for reconciliation / manual import and takes effect after restart.",
        [nameof(CheckAnomalyNotifications)] = "Enable anomaly notifications (in-app toast)",
        [nameof(LocalPathsTitle)] = "Local paths",
        [nameof(LabelCacheDb)] = "Cache database",
        [nameof(LabelSettingsFile)] = "Settings file",
        [nameof(SaveSettings)] = "Save settings",
        [nameof(ToastScanDone)] = "Scan finished",
        [nameof(ToastScanFailed)] = "Scan failed: the table keeps the most recent successful snapshot",
        [nameof(ToastSaved)] = "Settings saved",
        [nameof(ToastSaveFailedFormat)] = "Failed to save: {0}",

        [nameof(StartupErrorTitle)] = "ccusage failed to start",
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
    public string TimeRange7 => Get();
    public string TimeRange30 => Get();
    public string TimeRange90 => Get();
    public string TimeRangeAll => Get();
    public string AllAgents => Get();
    public string AllModels => Get();
    public string AllProjects => Get();

    // ── 托盘 ──
    public string TrayTooltip => Get();
    public string TrayCostFormat => Get();
    public string TrayCostNoData => Get();
    public string TrayOpenWindow => Get();
    public string TrayRefreshNow => Get();
    public string TraySettings => Get();
    public string TrayExit => Get();

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
    public string RefreshHint => Get();
    public string LabelCcusageCli => Get();
    public string CcusageHint => Get();
    public string CheckAnomalyNotifications => Get();
    public string LocalPathsTitle => Get();
    public string LabelCacheDb => Get();
    public string LabelSettingsFile => Get();
    public string SaveSettings => Get();
    public string ToastScanDone => Get();
    public string ToastScanFailed => Get();
    public string ToastSaved => Get();
    public string ToastSaveFailedFormat => Get();

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
    public string TotalsModelCountFormat => Get();
    public string TotalsUnknownNoteFormat => Get();

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
