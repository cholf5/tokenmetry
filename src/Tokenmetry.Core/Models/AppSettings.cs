namespace Tokenmetry.Core.Models;

/// <summary>
/// A-11 收口：仅保留有真实消费者（UI 出口或行为读取）的设置字段——OfflinePricing /
/// BudgetNotificationsEnabled / ActiveSessionNotificationsEnabled / DailyBudgetUsd /
/// MonthlyBudgetUsd 此前无任何出口，按"假开关/死配置"红线删除；旧 settings.json 中的
/// 多余字段反序列化时被忽略，不构成迁移问题。
/// </summary>
public class AppSettings
{
    public string CcusageCommand { get; set; } = "ccusage";
    public bool ShowDebugInfo { get; set; } = true;

    /// <summary>
    /// 后台采集刷新间隔（分钟，设置页可调 1–1440）。默认 5（S-007）：采集无变化轮本应轻，
    /// 但分钟级默认会让"每轮管道"以最高档常态空转（见 I-042 短路前的实测）；5 分钟与
    /// agent 日志的实际写入节奏匹配，活跃使用时数据仍近实时（活跃轮由文件变化触发全量）。
    /// </summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>外观主题偏好：light（日间）/ dark（夜间）/ system（跟随系统）。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 界面语言偏好：zh（简体中文）/ en（English）/ system（跟随系统 UI 语言）；缺省 system，
    /// 未知值在解析处（Localization/Translations.ApplyPreference）如实回落 system。
    /// 存字符串而非枚举：与 Theme 同风格，且未知值不会让 settings.json 整体反序列化失败。
    /// 消费者=全部 UI 文案（Tokenmetry.App/Localization/Translations，启动时与设置页切换即时应用）。
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// 关窗行为（S-005）：true（默认，A-0 托盘常驻语义）= 用户关窗（点 X / Alt+F4 / 程序内 Close()）
    /// 隐藏到托盘、进程常驻（后台刷新不中断）；false = 用户关窗即退出进程（仍走 desktop.Shutdown
    /// 生命周期收尾，同托盘「退出」）。消费者 = MainWindow.OnClosing 的关窗分派；UI 出口 = 设置页。
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// 「已最小化到托盘」首次关窗提示的一次性状态位（S-005）：非用户偏好、无 UI 出口——提示展示过
    /// 即置位并落盘，此后不再打扰。消费者 = MainWindow.OnClosing；与 MinimizeToTrayOnClose 同时写入
    /// 共享的 settings.json（保存为临时文件 + 原子 Move，两侧写同一 AppSettings 单例）。
    /// </summary>
    public bool CloseToTrayHintShown { get; set; }

    /// <summary>
    /// 开机自启动（S-006，默认关闭）：true 时 OS 层注册「当前可执行文件 + --autostart 参数」随用户
    /// 登录启动（Windows = HKCU Run 键 / macOS = ~/Library/LaunchAgents plist / Linux = XDG autostart，
    /// 见 App/Services/AutoStartManager）。--autostart 启动静默常驻托盘不弹主窗（A-0 托盘常驻语义）。
    /// 消费者 = SettingsViewModel 开关（注册/注销，OS 失败回滚开关）+ App 启动对账（enabled 时重写注册，
    /// 应用更新换可执行路径后自启动不失效）。
    /// </summary>
    public bool AutoStartOnBoot { get; set; }

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
}
