namespace Tokenmetry.Core.Models;

/// <summary>
/// A-11 收口：仅保留有真实消费者（UI 出口或行为读取）的设置字段——OfflinePricing /
/// BudgetNotificationsEnabled / ActiveSessionNotificationsEnabled / DailyBudgetUsd /
/// MonthlyBudgetUsd 此前无任何出口，按"假开关/死配置"红线删除；旧 settings.json 中的
/// 多余字段反序列化时被忽略，不构成迁移问题。
/// </summary>
public class AppSettings
{
    public bool ShowDebugInfo { get; set; } = true;

    /// <summary>
    /// 后台采集刷新间隔（分钟，设置页可调 1–1440）。默认 5（S-007）：采集无变化轮本应轻，
    /// 但分钟级默认会让"每轮管道"以最高档常态空转（见 I-042 短路前的实测）；5 分钟与
    /// agent 日志的实际写入节奏匹配，活跃使用时数据仍近实时（活跃轮由文件变化触发全量）。
    /// </summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>外观模式偏好（画板 × 模式两维中的模式维）：light（日间）/ dark（夜间）/
    /// system（跟随系统）。单档画板（Nord/Dracula 只有夜间）在应用处强制唯一档，本字段保留
    /// 用户原偏好——切回多档画板时原日间/夜间选择仍在。消费者 = ThemeService.Apply 的模式归口。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 外观画板偏好（整套配色皮肤，画板 × 模式两维中的画板维）：default（默认画板，原型蓝）/
    /// nord / dracula / solarized / gruvbox。画板字典在 App 的 Styles/Themes/（由
    /// scripts/generate-theme-palettes.py 生成，逐令牌过对比度纪律），ThemeService 运行期换入；
    /// 未知值在解析处如实回落 default。旧 settings.json 无此键 = 默认画板，零迁移。
    /// 消费者 = ThemeService.Apply（换字典 + 变体归口）与设置页画板选择器。
    /// </summary>
    public string ThemePalette { get; set; } = "default";

    /// <summary>
    /// 界面语言偏好（统一小写）：zh（简体中文）/ zh-hant（繁體中文）/ en（English）/ ja（日本語）/
    /// ko（한국어）/ pt-br（Português (Brasil)，唯一葡语变体）/ ru（Русский）/ es（Español）/
    /// de（Deutsch）/ fr（Français）/ system（跟随系统 UI 语言：zh 系按书写系统分简繁，其余直达，
    /// 未支持语种 → en）；缺省 system，
    /// 未知值在解析处（Localization/Translations.ApplyPreference）如实回落 system。
    /// 存字符串而非枚举：与 Theme 同风格，且未知值不会让 settings.json 整体反序列化失败
    /// （旧值 "zh"/"en" 原样兼容）。消费者=全部 UI 文案（Tokenmetry.App/Localization/Translations，
    /// 启动时与设置页切换即时应用）。
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// 关窗行为（S-005）：true（默认，A-0 托盘常驻语义）= 用户关窗（点 X / Alt+F4 / 程序内 Close()）
    /// 隐藏到托盘、进程常驻（后台刷新不中断）；false = 用户关窗即退出进程（仍走 desktop.Shutdown
    /// 生命周期收尾，同托盘「退出」）。消费者 = MainWindow.OnClosing 的关窗分派；UI 出口 = 设置页。
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// 首次关窗选择弹窗的一次性状态位（S-005，前身为「已最小化到托盘」提示位 CloseToTrayHintShown，
    /// 行为改为让用户选择后更名；旧 settings.json 的旧字段反序列化时被忽略——已见过旧提示的用户
    /// 会在下次关窗再见到一次选择弹窗，属可接受的一次性打扰）：非用户偏好、无 UI 出口——用户在
    /// 「直接退出 / 缩到托盘」弹窗做出选择即置位并落盘，此后不再打扰（弹窗被直接关掉视为未选择，
    /// 不置位、下次关窗再问）。消费者 = MainWindow.OnClosing；与 MinimizeToTrayOnClose 同时写入
    /// 共享的 settings.json（保存为临时文件 + 原子 Move，两侧写同一 AppSettings 单例）。
    /// </summary>
    public bool FirstCloseChoiceMade { get; set; }

    /// <summary>
    /// 开机自启动（S-006，默认关闭）：true 时 OS 层注册「当前可执行文件 + --autostart 参数」随用户
    /// 登录启动（Windows = HKCU Run 键 / macOS = ~/Library/LaunchAgents plist / Linux = XDG autostart，
    /// 见 App/Services/AutoStartManager）。--autostart 启动静默常驻托盘不弹主窗（A-0 托盘常驻语义）。
    /// 消费者 = SettingsViewModel 开关（注册/注销，OS 失败回滚开关）+ App 启动对账（enabled 时重写注册，
    /// 应用更新换可执行路径后自启动不失效）。
    /// </summary>
    public bool AutoStartOnBoot { get; set; }

    /// <summary>
    /// 采集管线文件日志开关（I-045，默认开）：日志落在 <see cref="Tokenmetry.App.Services.AppPaths"/>
    /// 同目录的 logs/（<c>app.log</c>，按大小轮转保留 3 份），内容只含路径/计数/耗时/异常（隐私红线）。
    /// 消费者 = AppServices 装配（IngestionFileLogger 构造参数）；UI 出口 = 设置页开关。
    /// 默认开：issue #1 的教训——应用拿不出任何诊断信息时，用户只能靠自建采样脚本自证。
    /// </summary>
    public bool FileLoggingEnabled { get; set; } = true;

    /// <summary>
    /// 更新检查的上次时间戳（UTC，无 UI 出口——非用户偏好）：启动期静默周检（App 启动序列）按它节流，
    /// 手动「检查更新」不受限但同样刷新本时间戳（成功与失败都记——节流语义是「打过一次卡」，打不通的
    /// 网络每次启动都重试只会白费）。消费者 = UpdateService。
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// 顶部栏时间范围档选中值（D-020 起为 TimeRangeOption 稳定 Id）：内置档沿用旧枚举名
    /// （"Today"/"Last7Days"/"Last30Days"/"Last90Days"/"All"/"Custom"，旧 settings.json 照常解析）
    /// 并新增 "Yesterday"/"ThisMonth"/"LastMonth"；用户自定义预设为 "preset:&lt;guid&gt;"。
    /// 未知值/指向已删除预设的 Id 由 FilterStatePersistence 解析处如实回落默认档。
    /// </summary>
    public string TimeRange { get; set; } = "Last7Days";

    /// <summary>自定义档起始日（含，yyyy-MM-dd invariant；null = 未选，查询映射不设下界）。</summary>
    public string? CustomFromDay { get; set; }

    /// <summary>自定义档结束日（含，yyyy-MM-dd invariant；null = 未选，查询映射不设上界）。</summary>
    public string? CustomToDay { get; set; }

    /// <summary>
    /// 用户自定义相对时间范围预设（D-020，如 本周/上周/最近 14 天）：封闭词表投影
    /// （Kind/Offset/Days，语义解释见 App 层 RelativeRangeSpec），上限 6 个、与内置档去重，
    /// 由 App 的 TimeRangePresetService 单点增删并即存。空清单 = 未自定义（默认）。
    /// </summary>
    public List<RelativeRangePresetSetting> CustomTimePresets { get; set; } = new();

    /// <summary>
    /// 分享海报的默认版式（设置页可选；也是生成对话框的起值，且对话框里改动即回写本字段——
    /// 上次使用即下次默认）："portrait"（竖版 1080 宽，默认）/ "landscape"（横版 1800 宽，
    /// 单日窗口强制竖版）。存字符串而非枚举：与 Theme/Language 同风格，未知值在解析处如实回落
    /// 默认，旧 settings.json 不受影响。
    /// </summary>
    public string SharePosterLayout { get; set; } = "portrait";

    /// <summary>
    /// 分享海报的默认主题（设置页可选；也是生成对话框的起值，且对话框里改动即回写本字段）：
    /// "app"（跟随应用，默认）/ "dark" / "light"。海报主题独立于应用主题——应用在夜间也能导出
    /// 一张日间海报，两套取值都来自同一批令牌（见 Styles/Tokens.axaml）。
    /// </summary>
    public string SharePosterTheme { get; set; } = "app";

    /// <summary>
    /// 分享海报的项目名雾化默认档（设置页可选；也是生成对话框的起值，且对话框里改动即回写本字段）：
    /// "off"（原样显示，默认）/ "partial"（前 4 个字符可见，其余模糊）/ "full"（整名模糊）。
    /// 雾化是文字模糊而不是占位符替换，保留"这里原本有字、大概多长"的形状。
    /// </summary>
    public string SharePosterProjectMask { get; set; } = "off";

    /// <summary>
    /// 分享海报的样式记忆（对话框选项即存，唯一写入方是生成对话框——设置页没有样式的编辑入口）：
    /// "tray" / "overview" / "heatmap" / "rhythm" / "milestone" = 上次在对话框里选的样式，下次打开
    /// 直接接着上次的选择；空串/未知值 = 从未在对话框里选过样式 → 打开时跟随范围（单日 → 信息卡、
    /// 多日 → 总览，见 MainWindowViewModel.CreateSharePosterDialogAsync），与解耦前的首启观感一致。
    /// </summary>
    public string SharePosterStyle { get; set; } = "";

    /// <summary>
    /// 分享海报的页边距记忆（对话框选项即存，唯一写入方是生成对话框——设置页没有编辑入口）：
    /// 海报四周留白的逻辑像素数（上下左右同值，0–80），下次打开接着上次拖的值；旧 settings.json
    /// 无此字段 = 默认 28。越界值在应用处按 <see cref="SharePosterRequest.SafeMargin"/> 收口。
    /// </summary>
    public double SharePosterMargin { get; set; } = SharePosterRequest.DefaultMargin;

    /// <summary>
    /// <b>旧</b>用户价格覆盖规则（I-003 字段形状）：I-035 起用户配置统一走
    /// <see cref="ModelDefinitions"/>，本字段只读不写（装载时转换为模型目录条目，见
    /// <see cref="ModelDefinitionOverride.FromLegacy"/>）。保留是为了不丢老 settings.json 里已配的价，
    /// 待下个版本删除。
    /// </summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();

    /// <summary>
    /// 用户模型目录条目（I-035）：按模型 ID 逐字段覆盖导入层（内置快照 / models.dev）的价格与元数据，
    /// ID 不在导入层时即用户自建的模型。价格、厂商、图标、聚合归一由此单点决定——设置页的模型目录区
    /// 即本列表的编辑器，保存后 <c>ModelCatalog.UpdateUserDefinitions</c> 热更新（无需重启）。
    /// </summary>
    public List<ModelDefinitionOverride> ModelDefinitions { get; set; } = new();

    /// <summary>
    /// 模型别名映射（原始名 → 模型 ID）：键 = 数据里出现的原始模型名（精确匹配，大小写敏感），
    /// 值 = 模型 ID（或用户自定的规范名）。动机：不同来源对同一模型命名不同
    /// （GLM-5.3-Flash / glm-flash-latest / ark/glm-5.3-flash），全靠内置规则无法覆盖，
    /// 交给用户在设置页维护；I-035 起同时被定价引擎消费（关联即对价格生效）。
    /// 空字典 = 不聚合（默认）。
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = new();

    /// <summary>
    /// 跨设备同步开关（relay 设计 §4.6，默认关 = 零出站网络请求，G3 红线）。
    /// 消费者 = SyncStateService（后端装配/定时器停启的归口）与 GlobalFilterMapper
    /// （关时查询强制本机设备 id，A12 单点收口）；UI 出口 = 设置页「跨设备同步」区块。
    /// </summary>
    public bool SyncEnabled { get; set; }

    /// <summary>同步中继根地址（官方实例或自建 http(s)://host:port）。消费者 = SyncStateService 建 RelaySyncBackend。</summary>
    public string SyncEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// 同步账号 token（明文落盘——设计 §4.6 已知取舍：v1 接受，OS keyring 列为远期增强）。
    /// 只随 Authorization 头过线，不进任何日志（隐私红线）。消费者 = SyncStateService。
    /// </summary>
    public string SyncToken { get; set; } = string.Empty;
}
