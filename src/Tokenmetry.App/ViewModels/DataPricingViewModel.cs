using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 统计口径区单行（S-002 定案）：本版全部为说明项——逐项评估后没有可真实生效的用户开关
/// （要么是管道/计价的始终生效行为，要么尚未实现），落 AppSettings 即假开关（卡内红线），
/// 故以「徽标 + 说明」样式呈现：<see cref="Badge"/> ∈ {始终生效, 未提供}。
/// </summary>
public sealed record CaliberItem(string Text, string Badge, string Note);

/// <summary>
/// 数据源表行 VM（S-002）：数据全部来自采集扫描注册表的快照（真实可得，见卡 Dev Log 来源表），
/// 无行级可变状态——每次进入页面 / 重扫完成后整表重建。状态类布尔驱动 XAML Classes 条件绑定
/// （status-ok / status-partial / status-nodata / status-pending，颜色复用既有强调色令牌）。
/// I-034 补：行首附该数据源的 Agent 品牌图标（只读、从 <see cref="Agent"/> 派生，不参与 record
/// 相等性——同名数据源恒等）。
/// </summary>
public sealed record DataSourceRowViewModel(
    string Agent,
    string StatusText,
    bool IsOk,
    bool IsPartial,
    bool IsNoData,
    bool IsError,
    bool IsPending,
    string LastScanText,
    string RecordCountText)
{
    /// <summary>Agent 图标（与统计页/会话页同一 AgentVisualMap 判定；未覆盖的源回退散列字形）。</summary>
    private AgentVisual Icon => AgentVisualMap.Resolve(Agent);

    /// <summary>品牌 logo 变体名（经 AgentChip.Logo 附加属性 → agent-* 伪类 DynamicResource 取 AppAgentLogo*）。</summary>
    public string LogoVendor => Icon.LogoVendor;

    /// <summary>散列兜底字形（lobe 无对应标的 Agent 才显示）。</summary>
    public string Glyph => Icon.Glyph.ToString();

    public static DataSourceRowViewModel NotScannedYet(string agent) =>
        new(agent, Translations.Instance.StatusNotScanned, IsOk: false, IsPartial: false, IsNoData: false, IsError: false, IsPending: true, "—", "—");
}

/// <summary>
/// 模型聚合规则单行（AliasRules）：原始模型名 → 规范名，展示侧附规范名的头像（聚合后统计里
/// 出现的是规范名，头像跟着规范名走）。不可变小对象，规则增删整表重建。
/// </summary>
public sealed class AliasRuleRowViewModel
{
    public AliasRuleRowViewModel(string alias, string target, ModelAvatarVisual targetAvatar)
    {
        Alias = alias;
        Target = target;
        Avatar = targetAvatar;
    }

    /// <summary>原始模型名（settings.json 字典键原文，删除时按它精确移除）。</summary>
    public string Alias { get; }

    /// <summary>规范名（统计显示与聚合键）。</summary>
    public string Target { get; }

    /// <summary>规范名的头像视觉（恒非 null；未关联名字回落按名推断）。</summary>
    public ModelAvatarVisual Avatar { get; }
}

/// <summary>
/// 模型目录单行（I-035 ModelDefinitions）：ID（稳定主键）/ 显示名 / 厂商 / 单价 / 来源。
/// 不可变小对象，条目增删改后整表重建。来源徽标如实反映三层（内置快照 / 在线 / 用户）；覆盖了
/// 导入层的条目在下次 models.dev 刷新时不会被冲掉，界面必须让人看得出来——但用户自建条目
/// 不是"覆盖"（下面没有导入层），不挂已改徽标、动作是删除整条。
/// </summary>
public sealed class ModelDefinitionRowViewModel
{
    public ModelDefinitionRowViewModel(ModelDefinition definition)
    {
        Id = definition.Id;
        Label = definition.DisplayLabel;
        Vendor = string.IsNullOrWhiteSpace(definition.Vendor) ? "—" : definition.Vendor;
        // 头像：目录条目的 AvatarKind 四模式 + 自动推断统一由 ModelAvatarVisual 收敛
        // （Auto + 旧手写 LogoVendor 的兼容语义也在解析层处理）。
        Avatar = ModelAvatarVisual.FromDefinition(definition, definition.Id);
        PriceText = definition.HasPrice
            ? string.Format(
                CultureInfo.InvariantCulture,
                "${0:0.###} / ${1:0.###}",
                definition.InputPerMillion,
                definition.OutputPerMillion)
            : Translations.Instance.ModelPriceMissing;
        OriginText = definition.Origin switch
        {
            ModelDefinitionOrigin.Remote => Translations.Instance.ModelOriginRemote,
            ModelDefinitionOrigin.User => Translations.Instance.ModelOriginUser,
            _ => Translations.Instance.ModelOriginBuiltin,
        };
        // 「覆盖」只对装在导入层之上的条目成立：自建条目（Origin = User）的价与元数据是
        // 第一手定义，下面没有导入层——已改徽标、已覆盖筛选与「移除覆盖」动作都不该出现在它身上。
        IsOverridden = definition.Origin != ModelDefinitionOrigin.User
            && (definition.IsPriceOverridden || definition.IsMetadataOverridden);
        HasPrice = definition.HasPrice;
        Origin = definition.Origin;
    }

    /// <summary>模型 ID（小写连字符约定；删除 / 编辑按它精确定位）。</summary>
    public string Id { get; }

    /// <summary>显示名（无显示名时回落 ID）。</summary>
    public string Label { get; }

    /// <summary>厂商 / 服务商 id（无来源时 "—"）。</summary>
    public string Vendor { get; }

    /// <summary>头像视觉（恒非 null；四模式 + 自动推断，见 <see cref="ModelAvatarVisual"/>）。</summary>
    public ModelAvatarVisual Avatar { get; }

    /// <summary>单价文案："$5 / $25"（input / output，USD / 百万 tokens）；无价时为「未定价」。</summary>
    public string PriceText { get; }

    /// <summary>来源徽标（内置 / 在线 / 自定义）。</summary>
    public string OriginText { get; }

    /// <summary>是否覆盖了导入层条目（价格或元数据被用户接管，刷新不会冲掉）。
    /// 自建条目恒 false——它的字段不是改在谁的上面，见构造函数注释。</summary>
    public bool IsOverridden { get; }

    /// <summary>是否有价（驱动价格列的"未定价"视觉区分）。</summary>
    public bool HasPrice { get; }

    /// <summary>来源枚举（内置 / 在线 / 用户；UI 按它判定移除按钮可见性）。</summary>
    public ModelDefinitionOrigin Origin { get; }

    /// <summary>是否用户自建条目（ID 不在导入层）：Actions 显示「删除」整条，而非「移除覆盖」。</summary>
    public bool IsUserCreated => Origin == ModelDefinitionOrigin.User;

    /// <summary>是否显示移除按钮：覆盖了导入层（移除后回落导入值）或整条自建（随条目消失）——
    /// 纯导入层条目的移除是静默 no-op，不渲染假按钮。</summary>
    public bool CanRemove => IsOverridden || IsUserCreated;
}

/// <summary>
/// 数据与计价页 VM（S-004，自 S-002 的合并页拆出）：数据从哪来（数据源表 + 重扫）、数字怎么算
/// （统计口径说明）、价格与身份在哪维护（模型目录 + 别名映射）。全部设置项改完即落盘并热更新
/// （全自动保存纪律沿袭 S-002）；应用偏好（主题 / 语言 / 刷新间隔等）在设置页，互不依赖。
/// </summary>
public partial class DataPricingViewModel : ViewModelBase, INavigationAware
{
    /// <summary>
    /// 统计口径区定案（S-002，逐项结论记录于卡 Dev Log）：原型 5 个复选项全部评估为说明项——
    /// 去重 / Token 四向拆分 / 未知成本纪律 / 仅本机处理为采集与计价管道的<b>始终生效</b>行为
    /// （无用户开关可接）；跨 Agent 合并<b>未提供</b>（未实现）。不做假开关。
    /// 文案每次访问经 Translations 现取（语言切换后 CaliberRows 重绑即拿到新语言）。
    /// </summary>
    public static IReadOnlyList<CaliberItem> CaliberItems
    {
        get
        {
            var t = Translations.Instance;
            return new[]
            {
                new CaliberItem(t.CaliberDedupText, t.BadgeAlways, t.CaliberDedupNote),
                new CaliberItem(t.CaliberSplitText, t.BadgeAlways, t.CaliberSplitNote),
                new CaliberItem(t.CaliberUnknownPriceText, t.BadgeAlways, t.CaliberUnknownPriceNote),
                new CaliberItem(t.CaliberMergeText, t.BadgeNotProvided, t.CaliberMergeNote),
                new CaliberItem(t.CaliberLocalOnlyText, t.BadgeAlways, t.CaliberLocalOnlyNote),
            };
        }
    }

    /// <summary>
    /// 模型目录（I-035）：价格 / 厂商 / 图标的唯一出处，也是"关联"的落点。本页改完条目即
    /// 热更新目录（统计页聚合与计价引擎即时生效）并触发一轮刷新——补价在采集管道里执行，
    /// 改价必须重算才在数字上体现（增量扫描器每轮返回全量记录，故一轮刷新即可全量重算，
    /// 无需重扫磁盘）。
    /// </summary>
    private readonly ModelCatalog _catalog;

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IIngestionScanReader _scanReader;
    private readonly BackgroundRefreshService _refreshService;
    private readonly IToastService _toastService;

    /// <summary>查询服务（可空：测试/预览无查询环境时为 null，候选清单保持空集——诚实无候选）。
    /// 供模型目录的"手动关联"卡拉取本地数据出现过的原始模型名作候选项。</summary>
    private readonly IUsageQueryService? _query;

    /// <summary>自动保存串行化 gate：改条目 / 改规则触发的高频保存不并发写同一 settings.json（原子写语义不被破坏）。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>重扫生命周期内部状态码（scanning / scan finished / scan error，供重扫回归锁对账；
    /// 无 UI 出口）。</summary>
    [ObservableProperty]
    private string _status = "ready";

    /// <summary>重新扫描进行中（TC-001：按钮进入扫描态防重复触发；互斥在 BackgroundRefreshService）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    /// <summary>重扫按钮文案（如实语义：全局重扫，不支持 per-source）。</summary>
    public string ScanButtonText => IsScanning ? Translations.Instance.ScanRunning : Translations.Instance.ScanIdle;

    /// <summary>数据源面板 meta 状态（Idle / Scanning / ScanFailed；文案经 Translations 实时取串，语言切换按当前状态重译）。</summary>
    public enum SourceMetaState
    {
        Idle,
        Scanning,
        ScanFailed,
    }

    private SourceMetaState _sourceMetaState = SourceMetaState.Idle;

    /// <summary>数据源面板 meta 文案：扫描中提示 / 静态口径说明 / 扫描失败提示（保持到下次成功扫描）。</summary>
    public string SourceMetaText => _sourceMetaState switch
    {
        SourceMetaState.Scanning => Translations.Instance.SourceMetaScanning,
        SourceMetaState.ScanFailed => Translations.Instance.SourceMetaScanFailed,
        _ => Translations.Instance.SourceMetaIdle,
    };

    private void SetSourceMetaState(SourceMetaState state)
    {
        if (_sourceMetaState == state)
        {
            return;
        }

        _sourceMetaState = state;
        OnPropertyChanged(nameof(SourceMetaText));
    }

    /// <summary>数据源表行（"已识别数据源"来自采集层注册表，不手写清单）。</summary>
    public ObservableCollection<DataSourceRowViewModel> SourceRows { get; } = new();

    /// <summary>模型聚合规则行（AliasRules，原始名 → 规范名；增删整表重建）。</summary>
    public ObservableCollection<AliasRuleRowViewModel> AliasRules { get; } = new();

    /// <summary>无规则空态（规则列表让位提示行）。</summary>
    public bool HasNoAliasRules => AliasRules.Count == 0;

    /// <summary>新增规则的原始模型名（AutoCompleteBox：候选来自本地数据出现过的原始名，也可自由输入）。</summary>
    [ObservableProperty]
    private string _newAliasSource = string.Empty;

    /// <summary>新增规则的规范名（同款候选 + 自由输入；规范名也会进候选供后续规则复用）。</summary>
    [ObservableProperty]
    private string _newAliasTarget = string.Empty;

    /// <summary>新增规则的行内校验提示（非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _aliasError;

    /// <summary>候选模型原始名（GetRawModelNamesAsync，本地数据全集按用量降序；加载失败保留旧值）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _aliasCandidates = [];

    public DataPricingViewModel(
        AppSettings settings,
        IAppSettingsStore store,
        IIngestionScanReader scanReader,
        BackgroundRefreshService refreshService,
        IToastService toastService,
        IUsageQueryService? query = null,
        ModelCatalog? catalog = null)
    {
        _settings = settings;
        _store = store;
        _scanReader = scanReader;
        _refreshService = refreshService;
        _toastService = toastService;
        _query = query;
        // 目录由 DI 以单例注入（与计价引擎 / 查询服务同一实例）；无注入时兜底新建（测试 / 预览）。
        // 用户层必须走共享合并口径（含旧 PricingOverrides 折算）——只传新字段会把 AppServices
        // 装配进去的旧配置从目录里冲掉，用户旧价格覆盖启动即静默失效。
        _catalog = catalog ?? new ModelCatalog();
        _catalog.UpdateAliases(settings.ModelAliases);
        _catalog.UpdateUserDefinitions(ModelCatalog.MergeUserDefinitions(settings));
        RebuildSourceRows();
        RebuildAliasRules();
        RebuildModelRows();
        _ = LoadAliasCandidatesAsync();

        // 语言切换重译订阅：本页 VM 为 DI 单例（与外壳同生命周期），直订阅无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    /// <summary>页面描述符（标题与副标题，与导航项同源）。</summary>
    public PageDescriptor Page { get; } = ShellPages.For(PageId.DataPricing);

    /// <summary>口径区行（静态定案数据，实例属性便于绑定）。</summary>
    public IReadOnlyList<CaliberItem> CaliberRows => CaliberItems;

    /// <summary>
    /// 全自动保存（沿袭 S-002 纪律：改完即落盘）。
    /// 单飞行 gate 串行化；best-effort——磁盘失败不阻断 UI 已生效的更改，打最小诊断
    /// （同外壳 Agent 选项失败的处理），后续任一更改会继续尝试。
    /// </summary>
    private async Task PersistSettingsAsync()
    {
        try
        {
            await _saveGate.WaitAsync();
            try
            {
                await _store.SaveAsync(_settings);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 设置自动保存失败（UI 已生效，待下次更改重试）：{ex.Message}");
        }
    }

    private void PersistSettings() => _ = PersistSettingsAsync();

    /// <summary>
    /// 语言切换重译（应用级单例，进程内存活）：以 PropertyChanged(CurrentLanguage) 作为
    /// 「每次切换一次」的信号（同 stupid-dict 约定）——重算命令式文案、重建数据源表并重绑口径区。
    /// XAML 直绑 Translations 的串由 Translations 的全量 PropertyChanged 自行刷新；
    /// 页头 Page.Title 由 PageDescriptor 自行发通知（叶子层）。
    /// 瞬态 Status（内部状态码）与 toast 保持出现时语言，不回溯重译。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
        {
            return;
        }

        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(SourceMetaText));
        OnPropertyChanged(nameof(CaliberRows));
        RebuildSourceRows();
        RebuildModelRows();
        RefreshAliasErrorTranslation();
        RefreshModelFormTranslation();
    }

    /// <summary>
    /// 单例页重进刷新（INavigationAware）：外壳每次导航进入本页后重建数据源表，
    /// 使后台定时刷新 / 上一页期间的扫描结果在重进时可见（生命周期定案见 S-002 卡 Dev Log）；
    /// 模型候选清单同步重拉（新扫描后数据里可能出现新模型名）。
    /// </summary>
    public void OnNavigatedTo()
    {
        RebuildSourceRows();
        // 目录行随 models.dev 后台刷新变化（来源徽标 / 在线价），重进页面时重读一次。
        RebuildModelRows();
        _ = LoadAliasCandidatesAsync();
    }

    /// <summary>
    /// TC-001 重新扫描（全局）：采集管道不支持 per-source 重扫（单飞行串行全源管道，
    /// 定案见 S-002 卡 Dev Log），按钮语义如实为「重新扫描（全部源）」，经
    /// BackgroundRefreshService.TriggerRefreshAsync 触发——与定时器刷新同一容器实例、同一单飞槽
    /// （防重复触发 + 触发合并，见审查修复轮 P2-2 定案），已有刷新在跑时并入该轮等待其真实结果。
    /// 完成后重建数据源表 + toast；成败经返回值区分（P2-1）——失败时如实报「扫描失败」并在
    /// 数据源面板给出"数据可能非本轮"行级提示，不做假成功。
    /// </summary>
    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        SetSourceMetaState(SourceMetaState.Scanning);
        Status = "scanning";
        try
        {
            // TriggerRefreshAsync 不抛异常（内部吞异常转 bool，定时器/首刷路径保持静默语义）；
            // 本页路径观察返回值区分成败。
            var success = await _refreshService.TriggerRefreshAsync();
            RebuildSourceRows();
            if (success)
            {
                Status = "scan finished";
                SetSourceMetaState(SourceMetaState.Idle);
                _toastService.Show(Translations.Instance.ToastScanDone);
            }
            else
            {
                Status = "scan error";
                SetSourceMetaState(SourceMetaState.ScanFailed);
                _toastService.Show(Translations.Instance.ToastScanFailed);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>从采集扫描注册表重建数据源表（注册序 = 管道扫描序；未扫描的源如实显示"尚未扫描"）。</summary>
    private void RebuildSourceRows()
    {
        var rows = _scanReader.RegisteredSources
            .Select(agent => _scanReader.GetLastScan(agent) is { } snapshot
                ? new DataSourceRowViewModel(
                    agent,
                    DescribeStatus(snapshot.Status),
                    IsOk: snapshot.Status == JsonlScanStatus.Ok,
                    IsPartial: snapshot.Status == JsonlScanStatus.Partial,
                    IsNoData: snapshot.Status == JsonlScanStatus.NoData,
                    IsError: snapshot.Status == JsonlScanStatus.Error,
                    IsPending: false,
                    FormatLastScan(snapshot.ScannedAt),
                    snapshot.RecordCount.ToString("N0", CultureInfo.InvariantCulture))
                : DataSourceRowViewModel.NotScannedYet(agent))
            .ToList();

        SourceRows.Clear();
        foreach (var row in rows)
        {
            SourceRows.Add(row);
        }
    }

    /// <summary>扫描状态 → 展示文案（三态语义与适配器口径对齐：JsonlScanDiagnostics.Status；
    /// 行的状态着色由各 Is* 布尔经 Classes 条件绑定驱动，见 SharedStyles 的 status-* 类）。</summary>
    internal static string DescribeStatus(JsonlScanStatus status) => status switch
    {
        JsonlScanStatus.Ok => Translations.Instance.StatusOk,
        JsonlScanStatus.Partial => Translations.Instance.StatusPartial,
        JsonlScanStatus.NoData => Translations.Instance.StatusNoData,
        JsonlScanStatus.Error => Translations.Instance.StatusError,
        _ => Translations.Instance.StatusNotScanned,
    };

    // ════════════════════ 模型目录（I-035）：条目增删改 + 手动关联 + 保存即重算 ════════════════════

    /// <summary>模型目录行（全部条目：内置快照 + models.dev 在线 + 用户自建 / 覆盖）。</summary>
    public ObservableCollection<ModelDefinitionRowViewModel> ModelRows { get; } = new();

    /// <summary>目录浏览过滤：搜索文本（ID / 显示名 / 厂商，大小写不敏感子串）。</summary>
    [ObservableProperty]
    private string _modelSearchText = string.Empty;

    partial void OnModelSearchTextChanged(string value) => RebuildModelRows();

    // 四个筛选 chip：前三个是来源维度（选中项并集），已覆盖是正交维度；全不选 = 不过滤。
    // 全部走整表重建（既有模式），与别名热更新、语言切换共用 RebuildModelRows 出口。
    [ObservableProperty]
    private bool _filterBuiltin;

    [ObservableProperty]
    private bool _filterRemote;

    [ObservableProperty]
    private bool _filterUser;

    [ObservableProperty]
    private bool _filterOverridden;

    partial void OnFilterBuiltinChanged(bool value) => RebuildModelRows();

    partial void OnFilterRemoteChanged(bool value) => RebuildModelRows();

    partial void OnFilterUserChanged(bool value) => RebuildModelRows();

    partial void OnFilterOverriddenChanged(bool value) => RebuildModelRows();

    /// <summary>目录全量条目数（区分「目录真空」与「无匹配」两个空态）。</summary>
    private int _catalogDefinitionCount;

    /// <summary>目录真空（内置快照缺失才会发生）——与「有目录但过滤后无匹配」区分。</summary>
    public bool HasNoModelRows => _catalogDefinitionCount == 0;

    /// <summary>目录非空但搜索 / 筛选后无匹配。</summary>
    public bool HasNoMatchingRows => _catalogDefinitionCount > 0 && ModelRows.Count == 0;

    /// <summary>编辑中的原条目 ID（null = 新建）。改 ID 视为换条目（原条目保留）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormTitle))]
    private string? _editingModelId;

    /// <summary>表单标题（新建 / 编辑）。</summary>
    public string ModelFormTitle => EditingModelId is null
        ? Translations.Instance.ModelFormCreate
        : string.Format(CultureInfo.InvariantCulture, Translations.Instance.ModelFormEditFormat, EditingModelId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormTitle), nameof(ModelFormAvatarPreview))]
    private string _modelFormId = string.Empty;

    /// <summary>P-001 T4/T5：Provider 前缀作用域（空 = 裸名条目；填如 openrouter/z-ai 则仅对该渠道生效）。</summary>
    [ObservableProperty]
    private string _modelFormScope = string.Empty;

    [ObservableProperty]
    private string _modelFormVendor = string.Empty;

    [ObservableProperty]
    private string _modelFormDisplayName = string.Empty;

    /// <summary>表单头像（图标选择器四模式 + 自动缺省）：载荷与模式同写同清（AvatarKind = Auto
    /// 时其余头像字段一律空），保存时经 <see cref="NormalizeFormAvatar"/> 防御性再收敛。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormAvatarPreview))]
    private ModelAvatarKind _modelFormAvatarKind = ModelAvatarKind.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormAvatarPreview))]
    private string? _modelFormAvatarVendor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormAvatarPreview))]
    private string _modelFormAvatarLetter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormAvatarPreview))]
    private string? _modelFormAvatarColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelFormAvatarPreview))]
    private string? _modelFormAvatarImageData;

    /// <summary>编辑弹层头像芯片的预览视觉：字母 / 色块 / 图片直接呈载荷，Vendor 呈所选标，
    /// 自动模式跟随 ID 输入框实时推断（所见即所存）。</summary>
    public ModelAvatarVisual ModelFormAvatarPreview => ModelFormAvatarKind switch
    {
        ModelAvatarKind.Vendor => ModelAvatarVisual.FromVendor(ModelFormAvatarVendor),
        ModelAvatarKind.Letter => ModelAvatarVisual.FromLetter(ModelFormAvatarLetter),
        ModelAvatarKind.Color => ModelAvatarVisual.FromColorHex(ModelFormAvatarColor) ?? ModelAvatarVisual.Empty,
        ModelAvatarKind.Image => ModelAvatarVisual.FromImageData(ModelFormAvatarImageData) ?? ModelAvatarVisual.Empty,
        _ => ModelAvatarVisual.FromName(string.IsNullOrWhiteSpace(ModelFormId) ? EditingModelId ?? string.Empty : ModelFormId),
    };

    /// <summary>单价输入（USD / 百万 tokens；空 = 不提供价格，只登记身份 / 元数据）。</summary>
    [ObservableProperty]
    private string _modelFormInput = string.Empty;

    [ObservableProperty]
    private string _modelFormOutput = string.Empty;

    /// <summary>缓存写 / 读单价输入（USD / 百万 tokens；各自独立可空——缺省由装订层按 input 推导）。</summary>
    [ObservableProperty]
    private string _modelFormCacheWrite = string.Empty;

    [ObservableProperty]
    private string _modelFormCacheRead = string.Empty;

    /// <summary>表单行内校验提示（非空并显示；合法 / 初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _modelFormError;

    /// <summary>目录条目的可选项——"手动关联"的目标侧候选（模型 ID，用户从此处挑或自由输入）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _modelIdCandidates = [];

    /// <summary>
    /// 保存条目（新建或覆盖）：ID 按 <see cref="ModelId.Normalize"/> 归一到约定（大写 / 点号自动收敛，
    /// 不是错误），价格可空但必须成对（只填一半是录入错误，半截价会伪装成已知价）。
    /// 落 settings.ModelDefinitions → 目录热更新 → 落盘 → 重建表 → 触发一轮重算。
    /// </summary>
    [RelayCommand]
    private void SaveModelDefinition()
    {
        var id = ModelId.Normalize(ModelFormId);
        if (!ModelId.IsValid(id))
        {
            ModelFormError = Translations.Instance.ModelFormErrorIdInvalid;
            return;
        }

        var inputText = ModelFormInput.Trim();
        var outputText = ModelFormOutput.Trim();
        if ((inputText.Length == 0) != (outputText.Length == 0))
        {
            ModelFormError = Translations.Instance.ModelFormErrorPricePair;
            return;
        }

        decimal? input = null;
        decimal? output = null;
        if (inputText.Length > 0)
        {
            if (!TryParsePrice(inputText, out var parsedInput) || !TryParsePrice(outputText, out var parsedOutput))
            {
                ModelFormError = Translations.Instance.ModelFormErrorPriceNumber;
                return;
            }

            input = parsedInput;
            output = parsedOutput;
        }

        // 缓存两价独立于 in/out 的成对校验：各自可空，填了就必须是合法数字（非法即报既有数字错误）。
        decimal? cacheWrite = null;
        decimal? cacheRead = null;
        if (ModelFormCacheWrite.Trim().Length > 0)
        {
            if (!TryParsePrice(ModelFormCacheWrite.Trim(), out var parsedWrite))
            {
                ModelFormError = Translations.Instance.ModelFormErrorPriceNumber;
                return;
            }

            cacheWrite = parsedWrite;
        }

        if (ModelFormCacheRead.Trim().Length > 0)
        {
            if (!TryParsePrice(ModelFormCacheRead.Trim(), out var parsedRead))
            {
                ModelFormError = Translations.Instance.ModelFormErrorPriceNumber;
                return;
            }

            cacheRead = parsedRead;
        }

        ModelFormError = null;

        // 缓存价按输入价体系推导：没有 input（自建无价条目）时缓存价无处锚定，落盘只会得到
        // 被装订层忽略的死数据，录入侧直接劝阻。
        if (input is null && (cacheWrite is not null || cacheRead is not null))
        {
            ModelFormError = Translations.Instance.ModelFormErrorCacheNeedsInputPair;
            return;
        }

        // 只想改显示名 / 厂商时，预填进表单的生效价不应被"保存"成用户价格覆盖——那会把跟随
        // models.dev 的价钉死（刷新不再更新）。仅当该 ID 此前没有用户价（表单里的价来自导入层）
        // 且与生效价一致时，全部留空 = 只覆盖元数据（导入层会原样回供这些值，含显式为 0 的缓存价）。
        // 用户自建条目（价格只存在新字段里，清掉重启即丢）与已覆盖条目（预填的是用户自己的价）
        // 不在此列，价格照写。
        var existingUserEntry = _settings.ModelDefinitions.FirstOrDefault(
            existing => string.Equals(ModelId.Normalize(existing.Id), id, StringComparison.OrdinalIgnoreCase));
        if (input is { } parsedIn && output is { } parsedOut
            && existingUserEntry is not { InputPerMillion: not null, OutputPerMillion: not null }
            && _catalog.FindById(id) is { HasPrice: true } effectivePrice
            && parsedIn == effectivePrice.InputPerMillion
            && parsedOut == effectivePrice.OutputPerMillion)
        {
            // 缓存价有任一改动 ⇒ 用户在做价格覆盖：整组照写（Bind 的价格覆盖成对生效，只落
            // 缓存价会被整组丢弃；且覆盖生效后导入层不再回供缓存价，未改动的另一价也按表单
            // 所见钉住，不做"表单显示 0、落盘变推导 0.75"的静默偷换）。
            var cacheChanged = cacheWrite is { } parsedWrite && parsedWrite != effectivePrice.CacheWritePerMillion
                || cacheRead is { } parsedRead && parsedRead != effectivePrice.CacheReadPerMillion;
            if (!cacheChanged)
            {
                input = null;
                output = null;
                cacheWrite = null;
                cacheRead = null;
            }
        }

        var (avatarKind, avatarLetter, avatarColor, avatarImage) = NormalizeFormAvatar();

        var definition = new ModelDefinitionOverride
        {
            Id = id,
            ProviderScope = Blank(ModelFormScope),
            Vendor = Blank(ModelFormVendor),
            DisplayName = Blank(ModelFormDisplayName),
            // 头像：LogoVendor 只在 Vendor 模式写入选中标，其余模式显式清空——防止旧手写值
            // 在用户改选字母 / 色块 / 图片后再切回自动时"复活"成厂商标（同旧字段清理先例）。
            LogoVendor = avatarKind == ModelAvatarKind.Vendor ? ModelFormAvatarVendor : null,
            AvatarKind = avatarKind,
            AvatarLetter = avatarLetter,
            AvatarColor = avatarColor,
            AvatarImage = avatarImage,
            InputPerMillion = input,
            OutputPerMillion = output,
            CacheWritePerMillion = cacheWrite,
            CacheReadPerMillion = cacheRead,
        };

        // 同 (ID, 作用域) 覆盖（编辑既有条目 = 改写这一条的用户层，导入层数据仍在下面垫着）。
        // P-001 T5：作用域是条目身份的一部分——同 ID 的裸名 / 其它作用域条目是各自的"局部变量"，
        // 保存不得清掉（删整组走列表行的移除按钮）。
        var scope = Blank(ModelFormScope);
        _settings.ModelDefinitions.RemoveAll(
            existing => string.Equals(ModelId.Normalize(existing.Id), id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.ProviderScope?.Trim() ?? string.Empty, scope ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        // 新字段已接管的 ID，旧字段里被遮蔽的同 ID 规则一并清掉：留着会在用户日后删除新条目时
        // 把旧价"复活"（重新成为生效规则）。这是用户显式编辑驱动的删除，不违反旧字段只读桥的
        // 本意（不新增、不迁移，只清已被接管的规则）。
        _settings.PricingOverrides.RemoveAll(
            existing => string.Equals(ModelId.Normalize(existing.Model), id, StringComparison.OrdinalIgnoreCase));

        _settings.ModelDefinitions.Add(definition);
        ApplyModelSettings();
        ResetModelForm();
        IsModelEditOpen = false;
    }

    /// <summary>模型编辑 / 新建弹层的可见性（页内 overlay；保存成功 / 取消 / 遮罩 / Esc 均关闭）。</summary>
    [ObservableProperty]
    private bool _isModelEditOpen;

    /// <summary>新建模型入口：清空表单进新建态并打开弹层（原常驻内联表单的职责接管）。</summary>
    [RelayCommand]
    private void AddModelDefinition()
    {
        ResetModelForm();
        IsModelEditOpen = true;
    }

    /// <summary>取消编辑：清表单并关弹层（遮罩点击 / Esc / 取消按钮同一出口）。</summary>
    [RelayCommand]
    private void CancelModelEdit()
    {
        ResetModelForm();
        IsModelEditOpen = false;
    }

    // ════════════════════ 模型头像图标选择器（编辑弹层之上的一层 overlay） ════════════════════

    /// <summary>图标选择器（草稿编辑器：模式段选 + 载荷 + 预览；确定才写回表单字段）。</summary>
    public AvatarPickerViewModel AvatarPicker { get; } = new();

    /// <summary>图标选择器弹层可见性（叠在模型编辑弹层之上；确定 / 取消 / 遮罩 / Esc 关闭）。</summary>
    [ObservableProperty]
    private bool _isAvatarPickerOpen;

    /// <summary>打开选择器：把表单当前头像（含自动模式按 ID 输入推断的预览）载入草稿。</summary>
    [RelayCommand]
    private void OpenAvatarPicker()
    {
        var inferenceName = string.IsNullOrWhiteSpace(ModelFormId) ? EditingModelId ?? string.Empty : ModelFormId;
        AvatarPicker.Load(
            ModelFormAvatarKind,
            ModelFormAvatarVendor,
            ModelFormAvatarLetter,
            ModelFormAvatarColor,
            ModelFormAvatarImageData,
            ModelAvatarVisual.FromName(inferenceName));
        IsAvatarPickerOpen = true;
    }

    /// <summary>确认选择：按选中模式整组写回表单字段（其余模式载荷清空，不留中间态）。</summary>
    [RelayCommand]
    private void ConfirmAvatarPicker()
    {
        if (!AvatarPicker.CanConfirm)
        {
            return;
        }

        ModelFormAvatarKind = AvatarPicker.Mode;
        ModelFormAvatarVendor = AvatarPicker.Mode == ModelAvatarKind.Vendor ? AvatarPicker.Vendor : null;
        ModelFormAvatarLetter = AvatarPicker.Mode == ModelAvatarKind.Letter ? AvatarPicker.Letter.Trim() : string.Empty;
        ModelFormAvatarColor = AvatarPicker.Mode == ModelAvatarKind.Color ? AvatarPicker.ColorHex : null;
        ModelFormAvatarImageData = AvatarPicker.Mode == ModelAvatarKind.Image ? AvatarPicker.ImageDataUri : null;
        IsAvatarPickerOpen = false;
    }

    /// <summary>取消选择：丢弃草稿关弹层（遮罩点击 / Esc / 取消按钮同一出口）。</summary>
    [RelayCommand]
    private void CancelAvatarPicker() => IsAvatarPickerOpen = false;

    /// <summary>把目录条目载入表单（编辑）：用户层有该 ID 就载入用户值，否则载入生效值（导入层）。</summary>
    [RelayCommand]
    private void EditModelDefinition(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var normalized = ModelId.Normalize(id);
        var userEntry = _settings.ModelDefinitions.FirstOrDefault(
            existing => string.Equals(ModelId.Normalize(existing.Id), normalized, StringComparison.OrdinalIgnoreCase));
        var effective = _catalog.FindById(normalized);

        EditingModelId = normalized;
        ModelFormId = normalized;
        ModelFormScope = userEntry?.ProviderScope?.Trim() ?? string.Empty;
        ModelFormVendor = userEntry?.Vendor ?? effective?.Vendor ?? string.Empty;
        ModelFormDisplayName = userEntry?.DisplayName ?? effective?.DisplayName ?? string.Empty;
        // 头像载入：用户层优先，其次生效值（导入层不带头像，唯旧手写 LogoVendor 经合并落在生效条目上）。
        // 旧 Auto + LogoVendor 语义在表单里显式化为 Vendor 模式——所见即所存，保存不再依赖解析层兼容。
        var (avatarKind, avatarLogo, avatarLetter, avatarColor, avatarImage) = ReadAvatarFields(userEntry, effective);
        if (avatarKind == ModelAvatarKind.Auto && !string.IsNullOrWhiteSpace(avatarLogo))
        {
            avatarKind = ModelAvatarKind.Vendor;
        }

        ModelFormAvatarKind = avatarKind;
        ModelFormAvatarVendor = avatarLogo;
        ModelFormAvatarLetter = avatarLetter ?? string.Empty;
        ModelFormAvatarColor = avatarColor;
        ModelFormAvatarImageData = avatarImage;
        ModelFormInput = userEntry?.InputPerMillion is { } input
            ? input.ToString(CultureInfo.InvariantCulture)
            : effective is { HasPrice: true } ? effective.InputPerMillion.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ModelFormOutput = userEntry?.OutputPerMillion is { } output
            ? output.ToString(CultureInfo.InvariantCulture)
            : effective is { HasPrice: true } ? effective.OutputPerMillion.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ModelFormCacheWrite = userEntry?.CacheWritePerMillion is { } write
            ? write.ToString(CultureInfo.InvariantCulture)
            : effective is { HasPrice: true } ? effective.CacheWritePerMillion.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ModelFormCacheRead = userEntry?.CacheReadPerMillion is { } read
            ? read.ToString(CultureInfo.InvariantCulture)
            : effective is { HasPrice: true } ? effective.CacheReadPerMillion.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ModelFormError = null;
        IsModelEditOpen = true;
    }

    /// <summary>
    /// 移除条目：新字段条目整条删；纯旧字段（PricingOverrides）条目在目录里同样可见，
    /// 用户显式移除时连旧字段规则一并删——否则"移除"对该行静默无效。自建条目随之消失；
    /// 被覆盖的导入条目回落导入层的价与元数据。
    /// </summary>
    [RelayCommand]
    private void RemoveModelDefinition(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var normalized = ModelId.Normalize(id);
        var removed = _settings.ModelDefinitions.RemoveAll(
            existing => string.Equals(ModelId.Normalize(existing.Id), normalized, StringComparison.OrdinalIgnoreCase));
        var removedLegacy = _settings.PricingOverrides.RemoveAll(
            existing => string.Equals(ModelId.Normalize(existing.Model), normalized, StringComparison.OrdinalIgnoreCase));
        if (removed == 0 && removedLegacy == 0)
        {
            return;
        }

        ApplyModelSettings();
        if (string.Equals(EditingModelId, normalized, StringComparison.OrdinalIgnoreCase))
        {
            ResetModelForm();
            IsModelEditOpen = false;
        }
    }

    /// <summary>读条目头像五元组（用户层 Override 与装订后 Definition 的共形字段；两层都空 = 全自动）。</summary>
    private static (ModelAvatarKind Kind, string? Logo, string? Letter, string? Color, string? Image) ReadAvatarFields(
        ModelDefinitionOverride? userEntry, ModelDefinition? effective)
    {
        return userEntry is not null
            ? (userEntry.AvatarKind, userEntry.LogoVendor, userEntry.AvatarLetter, userEntry.AvatarColor, userEntry.AvatarImage)
            : effective is not null
                ? (effective.AvatarKind, effective.LogoVendor, effective.AvatarLetter, effective.AvatarColor, effective.AvatarImage)
                : (ModelAvatarKind.Auto, null, null, null, null);
    }

    /// <summary>清空表单（新建态）。</summary>
    [RelayCommand]
    private void ResetModelForm()
    {
        EditingModelId = null;
        ModelFormId = string.Empty;
        ModelFormScope = string.Empty;
        ModelFormVendor = string.Empty;
        ModelFormDisplayName = string.Empty;
        ModelFormAvatarKind = ModelAvatarKind.Auto;
        ModelFormAvatarVendor = null;
        ModelFormAvatarLetter = string.Empty;
        ModelFormAvatarColor = null;
        ModelFormAvatarImageData = null;
        ModelFormInput = string.Empty;
        ModelFormOutput = string.Empty;
        ModelFormCacheWrite = string.Empty;
        ModelFormCacheRead = string.Empty;
        ModelFormError = null;
        // 编辑弹层关闭路径共用本方法（保存成功 / 取消 / 移除条目时）：选择器草稿随之作废，
        // 不让 picker overlay 在编辑弹层消失后残留在页面上。
        IsAvatarPickerOpen = false;
    }

    /// <summary>
    /// 目录变更的公共出口：落盘（全自动保存）+ 目录热更新 + 重建表 + 触发一轮重算。
    /// 重算的必要性：补价发生在采集管道（facts 的 cost_usd 在写在库时算好），改完价格必须让管道
    /// 重跑一遍才在数字上体现——增量扫描器每轮返回全量记录，故一轮刷新即全量重算（不重扫磁盘）。
    /// </summary>
    private void ApplyModelSettings()
    {
        PersistSettings();
        // 与装配同一份合并口径：旧 PricingOverrides 折算条目在热更新后仍留在目录里。
        _catalog.UpdateUserDefinitions(ModelCatalog.MergeUserDefinitions(_settings));
        RebuildModelRows();
        TriggerReprice();
    }

    /// <summary>从目录重建条目表（应用浏览过滤谓词）+ 关联目标候选（模型 ID 清单，不受过滤影响）。</summary>
    private void RebuildModelRows()
    {
        var definitions = _catalog.Definitions;
        _catalogDefinitionCount = definitions.Count;
        ModelRows.Clear();
        foreach (var definition in definitions)
        {
            var row = new ModelDefinitionRowViewModel(definition);
            if (MatchesCatalogFilter(row))
            {
                ModelRows.Add(row);
            }
        }

        ModelIdCandidates = definitions.Select(definition => definition.Id).ToList();
        OnPropertyChanged(nameof(HasNoModelRows));
        OnPropertyChanged(nameof(HasNoMatchingRows));
    }

    /// <summary>目录浏览谓词：来源 chip 并集 → 已覆盖 chip → 文本三字段子串，依次取交集。</summary>
    private bool MatchesCatalogFilter(ModelDefinitionRowViewModel row)
    {
        var originSelected = FilterBuiltin || FilterRemote || FilterUser;
        if (originSelected
            && !(FilterBuiltin && row.Origin == ModelDefinitionOrigin.Snapshot)
            && !(FilterRemote && row.Origin == ModelDefinitionOrigin.Remote)
            && !(FilterUser && row.Origin == ModelDefinitionOrigin.User))
        {
            return false;
        }

        if (FilterOverridden && !row.IsOverridden)
        {
            return false;
        }

        return ModelSearchText.Length == 0
            || row.Id.Contains(ModelSearchText, StringComparison.OrdinalIgnoreCase)
            || row.Label.Contains(ModelSearchText, StringComparison.OrdinalIgnoreCase)
            || row.Vendor.Contains(ModelSearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 触发一轮重算（fire-and-forget）：与重扫按钮同一容器与单飞槽——正在跑的刷新并入该轮，
    /// 不追加新的采集轮次。失败静默（页面数据保持上一轮；价格已落盘，下轮刷新自然校正）。
    /// </summary>
    private void TriggerReprice()
    {
        _toastService.Show(Translations.Instance.ToastRepricing);
        _ = _refreshService.TriggerRefreshAsync();
    }

    /// <summary>
    /// 价格输入解析：非负十进制，两趟文化口径（B-015 定案：不变文化优先）。空串由调用方先判掉。
    /// 第一趟 InvariantCulture 且不带千分位（Float）——点号串全局同义：回显与落盘序列化恒 invariant
    /// （编辑弹层预填的 "0.047" 必须原样解析回来），models.dev 粘贴形态在 comma 小数宿主上不得被
    /// 本机千分位偷换。第二趟回落 CurrentCulture 全量口径（Number）——de-DE 等 comma 小数文化的
    /// 本机输入（0,5 / 0,047 / 1.234,5）由此命中。
    /// 已知边界：de-DE 宿主上点号千分位串（"1.234"）读作小数 1.234 而非本机千分位 1234——计价
    /// 输入量级（每百万单价 ≤ 数百）不出现真千分位，接受该歧义换取点号串不随宿主文化漂移。
    /// 千分位在第一趟排除还有一层：.NET 解析不校验千分位组大小，带 AllowThousands 的 invariant
    /// 趟会把 "0,5" 静默读成 5（旧实现的实际缺陷是千倍错标而非报错拒绝）。
    /// </summary>
    private static bool TryParsePrice(string text, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value >= 0m;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && value >= 0m;
    }

    /// <summary>data URI 落盘上限（64×64 PNG 的 base64 实际远小于此；防手改 settings.json 的脏大值）。</summary>
    private const int MaxAvatarDataUriChars = 64 * 1024;

    /// <summary>
    /// 表单头像 → 持久化四元组的防御性收敛：字母取 1 字符、颜色规范化 #RRGGBB、图片限定
    /// 本应用产出的 PNG data URI 且限长；载荷不合法的模式静默落 Auto（录入侧由选择器的
    /// 确认门禁拦住，此处只防手改脏值），不做半渲染。
    /// </summary>
    private (ModelAvatarKind Kind, string? Letter, string? Color, string? Image) NormalizeFormAvatar()
    {
        switch (ModelFormAvatarKind)
        {
            case ModelAvatarKind.Vendor:
                return (ModelAvatarKind.Vendor, null, null, null);
            case ModelAvatarKind.Letter when ModelFormAvatarLetter.Trim().Length > 0:
                var letter = ModelFormAvatarLetter.Trim();
                return (ModelAvatarKind.Letter, letter[..1], null, null);
            case ModelAvatarKind.Color when ModelAvatarVisual.TryNormalizeColor(ModelFormAvatarColor, out var hex):
                return (ModelAvatarKind.Color, null, hex, null);
            case ModelAvatarKind.Image when ModelFormAvatarImageData is { } uri
                && uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                && uri.Length <= MaxAvatarDataUriChars:
                return (ModelAvatarKind.Image, null, null, uri);
            default:
                return (ModelAvatarKind.Auto, null, null, null);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>刷新语言切换后的命令式文案（表单标题、校验提示）。</summary>
    private void RefreshModelFormTranslation()
    {
        OnPropertyChanged(nameof(ModelFormTitle));
        if (ModelFormError is not null)
        {
            // 校验提示是即时文案，不跨语言保留：切换后清空，下次保存按当前输入重新校验。
            ModelFormError = null;
        }
    }

    // ════════════════════ 模型聚合（别名映射）：规则增删 + 热更新 + 候选清单 ════════════════════

    /// <summary>
    /// 新增映射规则：校验（两侧非空、不得自映射——解析器按原始名精确匹配，字面相同即无效果、
    /// 目标须为目录里已存在的模型 ID——悬空重定向会把身份解析带偏）
    /// → 写入 <see cref="AppSettings.ModelAliases"/>（同原始名覆盖旧规则，字典语义）→ 重建规则行 +
    /// 落盘 + 热更新解析器（统计页查询即时按新映射聚合）。原始名取输入框原文（trim 后），
    /// 用户可自由键入数据里还没有的名字，为将来出现该名字的来源做准备。
    /// </summary>
    [RelayCommand]
    private void AddAliasRule()
    {
        var source = NewAliasSource.Trim();
        var target = NewAliasTarget.Trim();
        if (source.Length == 0 || target.Length == 0)
        {
            AliasError = Translations.Instance.AliasErrorRequired;
            return;
        }

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            AliasError = Translations.Instance.AliasErrorSame;
            return;
        }

        if (_catalog.FindById(target) is null)
        {
            // 目标必须是目录里已存在的模型 ID：落空的重定向会在身份解析里压掉原始名命中
            // （解析端另有回落兜底，录入侧直接劝阻）。
            AliasError = Translations.Instance.AliasErrorTargetUnknown;
            return;
        }

        AliasError = null;
        _settings.ModelAliases[source] = target;
        ApplyAliasSettings();
        NewAliasSource = string.Empty;
        NewAliasTarget = string.Empty;
    }

    /// <summary>移除映射规则（按行的原始名精确移除）→ 重建 + 落盘 + 热更新。</summary>
    [RelayCommand]
    private void RemoveAliasRule(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !_settings.ModelAliases.Remove(alias))
        {
            return;
        }

        ApplyAliasSettings();
    }

    /// <summary>规则变更的公共出口：字典 → 规则行重建 + settings.json 落盘 + 目录热更新 + 一轮重算。</summary>
    private void ApplyAliasSettings()
    {
        RebuildAliasRules();
        PersistSettings();
        _catalog.UpdateAliases(_settings.ModelAliases);
        RebuildModelRows();
        TriggerReprice();
    }

    /// <summary>从 AppSettings.ModelAliases 重建规则行（插入序 = 字典序，与 settings.json 一致）；
    /// 目标侧头像按规范名查目录条目（用户自定义头像跟着规范名走，未关联名字回落名推断）。</summary>
    private void RebuildAliasRules()
    {
        AliasRules.Clear();
        foreach (var (alias, target) in _settings.ModelAliases)
        {
            AliasRules.Add(new AliasRuleRowViewModel(
                alias, target, ModelAvatarVisual.FromDefinition(_catalog.FindById(target), target)));
        }

        OnPropertyChanged(nameof(HasNoAliasRules));
    }

    /// <summary>
    /// 候选模型原始名（本地数据全集、绕过别名归一）：进入本页 / 构造时拉取。失败静默保留
    /// 旧清单（候选是辅助录入，不值得打断页面；下次进入重试）。internal 供测试对账
    /// （构造路径是 fire-and-forget，测试需要显式等待）。
    /// I-035 排序：<b>目录里关联不上的原始名排前面</b>——目录能自动关联的名字不需要人管，
    /// 待处理的名字才是这一栏的用途（候选仍未过滤掉已关联项：用户可能想覆盖自动关联的结果）。
    /// </summary>
    internal async Task LoadAliasCandidatesAsync()
    {
        if (_query is null)
        {
            return;
        }

        try
        {
            var raw = await _query.GetRawModelNamesAsync();
            AliasCandidates = raw
                .OrderBy(name => _catalog.ResolveIdentity(name).IsLinked ? 1 : 0)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 模型候选清单加载失败（保留旧值）：{ex.Message}");
        }
    }

    /// <summary>校验提示是即时文案（出现时语言），语言切换后重算当前输入的校验结果对齐其余行内提示。</summary>
    private void RefreshAliasErrorTranslation()
    {
        if (AliasError is null)
        {
            return;
        }

        var source = NewAliasSource.Trim();
        var target = NewAliasTarget.Trim();
        AliasError = source.Length == 0 || target.Length == 0
            ? Translations.Instance.AliasErrorRequired
            : string.Equals(source, target, StringComparison.Ordinal)
                ? Translations.Instance.AliasErrorSame
                : _catalog.FindById(target) is null
                    ? Translations.Instance.AliasErrorTargetUnknown
                    : null;
    }

    /// <summary>扫描完成时刻 → 展示文案：当日显示 HH:mm，跨日显示 MM-dd HH:mm（本机时区）。</summary>
    internal static string FormatLastScan(DateTimeOffset scannedAt)
    {
        var local = scannedAt.LocalDateTime;
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
