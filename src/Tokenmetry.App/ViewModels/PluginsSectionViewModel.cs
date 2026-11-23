using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 数据与计价页「插件」区块（Task 8，设计文档 §6.2）：插件安装、启用/停用、卸载与状态展示。
/// <para>
/// 数据来源是插件事实的三个面：<see cref="IPluginCatalog.Installed"/>（有效安装）、
/// <see cref="IPluginCatalog.Invalid"/>（扫描到但 manifest 无效的目录）与
/// <see cref="IngestionScanRegistry"/>（按插件显示名登记的扫描快照）。提供一个固定区块，
/// 不新增导航页、不改信息架构。
/// </para>
/// <para>
/// 三条敏感操作流的固定顺序（Task 6 carry-forward）：安装/卸载前先
/// <see cref="IPluginSourceProvider.StopPlugin"/> 停掉常驻宿主（它会锁住插件文件与缓存），再改目录/状态，
/// 再 <see cref="IPluginSourceProvider.Refresh"/> 让采集管线同步，再重建行、最后触发一轮重扫——
/// 否则运行中的进程会一直握着旧插件源直到重启。任何失败都只报错，不伪造成功。
/// </para>
/// </summary>
public sealed partial class PluginsSectionViewModel : ObservableObject
{
    private readonly IPluginCatalog _catalog;
    private readonly IPluginInstaller _installer;
    private readonly IPluginSourceProvider _sourceProvider;
    private readonly IPluginFilePicker _picker;
    private readonly IPluginInstallConfirmation _confirmation;
    private readonly AgentIconService _icons;
    private readonly IngestionScanRegistry _registry;
    private readonly Func<Task> _rescan;

    private readonly bool _isNoOp;

    /// <summary>插件行（catalog.Installed + catalog.Invalid 合并，整表重建）。</summary>
    public ObservableCollection<PluginRowViewModel> Rows { get; } = new();

    /// <summary>插件宿主是否可用（<c>hostPath != null</c>）。false 时安装按钮禁用并展示提示。</summary>
    public bool HostAvailable { get; }

    /// <summary>区块级错误（安装/启停/卸载失败的可读消息）；成功后清空。</summary>
    [ObservableProperty]
    private string? _installError;

    /// <summary>是否有插件行（驱动空态可见性）。</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// 无服务、无副作用的空区块：<see cref="DataPricingViewModel"/> 在测试/预览未注入真实区块时使用
    /// （既有测试构造因此无需改动）。所有命令 no-op，<see cref="HostAvailable"/> 为 false。
    /// </summary>
    public static PluginsSectionViewModel Empty { get; } = new();

    private PluginsSectionViewModel()
    {
        _isNoOp = true;
        _catalog = null!;
        _installer = null!;
        _sourceProvider = null!;
        _picker = null!;
        _confirmation = null!;
        _icons = AgentIconService.BuiltInOnly;
        _registry = null!;
        _rescan = static () => Task.CompletedTask;
        HostAvailable = false;
    }

    public PluginsSectionViewModel(
        IPluginCatalog catalog,
        IPluginInstaller installer,
        IPluginSourceProvider sourceProvider,
        IPluginFilePicker picker,
        IPluginInstallConfirmation confirmation,
        AgentIconService icons,
        IngestionScanRegistry registry,
        Func<Task> rescan,
        string? hostPath)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _sourceProvider = sourceProvider ?? throw new ArgumentNullException(nameof(sourceProvider));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _icons = icons ?? AgentIconService.BuiltInOnly;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rescan = rescan ?? throw new ArgumentNullException(nameof(rescan));
        HostAvailable = hostPath is not null;

        Rows.CollectionChanged += OnRowsChanged;

        // 语言切换重译：状态/版本/作者串随当前语言重建（本 VM 是 DI 单例，直订阅无需退订，
        // 同 DataPricingViewModel 约定）。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;

        // 启动装配后目录已加载（AppServices 启动序列先 Reload + Refresh），这里同步建初始行即可。
        RebuildRows();
    }

    /// <summary>是否处于空区块模式（<see cref="Empty"/>）。</summary>
    public bool IsNoOp => _isNoOp;

    /// <summary>重新读取目录并重建行（安装/启停/卸载之后与语言切换时调用）。</summary>
    public async Task RefreshAsync()
    {
        if (_isNoOp)
        {
            return;
        }

        // 目录实现是同步磁盘扫描；Reload 让行反映安装/卸载后的真实文件状态。
        await _catalog.ReloadAsync(CancellationToken.None);
        RebuildRows();
    }

    private bool CanInstall() => !_isNoOp && HostAvailable;

    /// <summary>
    /// 安装流（设计 §6.1）：选包 → 校验预览（无效包抛 <see cref="PluginInstallException"/>）→
    /// 确认（manifest/警告）→ 安装 → 提供者刷新 → 重建行 → 重扫。取消 = 不动作。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_isNoOp || !HostAvailable)
        {
            return;
        }

        InstallError = null;

        var packagePath = await _picker.PickPackageAsync();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        PluginInstallPreview preview;
        try
        {
            preview = _installer.Preview(packagePath);
        }
        catch (PluginInstallException ex)
        {
            InstallError = DescribePreviewFailure(ex);
            return;
        }

        // 展示名变化会切断历史（设计 D14）：补充本地化警告；宿主不可用等警告原样保留。
        preview = EnrichWarnings(preview);

        if (!await _confirmation.ConfirmAsync(preview))
        {
            return;
        }

        try
        {
            // 升级会替换 plugins/<id>：先停掉常驻宿主（Windows 上它会锁住插件文件）。
            _sourceProvider.StopPlugin(preview.Manifest.Id);
            await _installer.InstallAsync(packagePath, CancellationToken.None);
            _sourceProvider.Refresh();
            await RefreshAsync();
            await _rescan();
        }
        catch (PluginInstallException ex)
        {
            InstallError = string.Format(
                Translations.Instance.PluginInstallFailedFormat, string.Join("; ", ex.Errors));
            // StopPlugin 已注销旧源；失败后目录仍列出该插件，必须重新登记，否则直到重启都不再扫描。
            _sourceProvider.Refresh();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            InstallError = string.Format(Translations.Instance.PluginInstallFailedFormat, ex.Message);
            _sourceProvider.Refresh();
            await RefreshAsync();
        }
    }

    /// <summary>启用/停用：写状态 → 提供者刷新 → 重建行 → 重扫。宿主缺失时仍允许写停用状态
    /// （状态只是 plugins.json 的事实，不依赖宿主可执行文件）。</summary>
    [RelayCommand]
    private async Task ToggleAsync(PluginRowViewModel? row)
    {
        if (_isNoOp || row is null || row.IsInvalid)
        {
            return;
        }

        InstallError = null;
        try
        {
            await _catalog.SetEnabledAsync(row.Id, !row.IsEnabled, CancellationToken.None);
            _sourceProvider.Refresh();
            await RefreshAsync();
            await _rescan();
        }
        catch (Exception ex)
        {
            InstallError = string.Format(Translations.Instance.PluginInstallFailedFormat, ex.Message);
        }
    }

    /// <summary>卸载：确认 → 删除文件/缓存（历史 facts 保留）→ 提供者刷新 → 重建行 → 重扫。</summary>
    [RelayCommand]
    private async Task UninstallAsync(PluginRowViewModel? row)
    {
        if (_isNoOp || row is null || row.IsInvalid)
        {
            return;
        }

        if (!await _confirmation.ConfirmUninstallAsync(row.DisplayName))
        {
            return;
        }

        InstallError = null;
        try
        {
            // 卸载会删除 plugins/<id> 与 plugin-data/<id>：先停宿主，否则 Windows 上删除失败。
            _sourceProvider.StopPlugin(row.Id);
            await _catalog.UninstallAsync(row.Id, CancellationToken.None);
            _sourceProvider.Refresh();
            await RefreshAsync();
            await _rescan();
        }
        catch (Exception ex)
        {
            InstallError = string.Format(Translations.Instance.PluginInstallFailedFormat, ex.Message);
            // StopPlugin 已注销源但删除失败、插件仍在：重新登记，避免它直到重启都不再扫描。
            _sourceProvider.Refresh();
        }
    }

    private void RebuildRows()
    {
        if (_isNoOp)
        {
            return;
        }

        var rows = new List<PluginRowViewModel>();

        foreach (var installation in _catalog.Installed)
        {
            rows.Add(BuildInstalledRow(installation));
        }

        foreach (var error in _catalog.Invalid)
        {
            rows.Add(BuildInvalidRow(error));
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    private PluginRowViewModel BuildInstalledRow(PluginInstallation installation)
    {
        var manifest = installation.Manifest;
        return new PluginRowViewModel(
            manifest.Id,
            manifest.DisplayName,
            manifest.Version,
            manifest.Author,
            installation.Enabled,
            IsInvalid: false,
            StatusText: DescribeScanStatus(manifest.DisplayName, installation.Enabled),
            ErrorText: null,
            Icon: _icons.Resolve(manifest.DisplayName).CustomIcon);
    }

    private PluginRowViewModel BuildInvalidRow(PluginCatalogError error)
    {
        // 无效目录没有可信 manifest：id 取插件目录名（<plugins>/<id>/<version> 的父目录名）。
        var id = Path.GetFileName(Path.GetDirectoryName(error.Directory)) is { Length: > 0 } dirName
            ? dirName
            : error.Directory;
        return new PluginRowViewModel(
            id,
            id,
            Version: string.Empty,
            Author: null,
            IsEnabled: false,
            IsInvalid: true,
            StatusText: Translations.Instance.PluginInvalidPackageFormat,
            ErrorText: error.Message,
            Icon: null);
    }

    /// <summary>
    /// 扫描状态：宿主缺失 → 停用 → 进行中 → 上次快照 → 尚未扫描。状态串与数据源表语义一致
    /// （设计 §5.6 / §6.2），但使用插件区块自己的翻译键。
    /// </summary>
    private string DescribeScanStatus(string displayName, bool enabled)
    {
        var t = Translations.Instance;

        if (!HostAvailable)
        {
            return t.PluginHostMissing;
        }

        if (!enabled)
        {
            return t.PluginStatusUnscanned;
        }

        if (_registry.GetScanProgress(displayName) is not null)
        {
            return t.PluginStatusScanning;
        }

        return _registry.GetLastScan(displayName)?.Status switch
        {
            JsonlScanStatus.Ok => t.PluginStatusOk,
            JsonlScanStatus.Partial => t.PluginStatusPartial,
            JsonlScanStatus.NoData => t.PluginStatusNoData,
            JsonlScanStatus.Error => t.PluginStatusError,
            _ => t.PluginStatusUnscanned,
        };
    }

    /// <summary>预览失败的可读消息：API 版本不受支持单独提示，其余按「安装失败：…」展示。</summary>
    private static string DescribePreviewFailure(PluginInstallException ex)
    {
        if (ex.Errors.Any(error => error.StartsWith("apiVersion", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Format(
                Translations.Instance.PluginUnsupportedApiVersionFormat, PluginApiVersion.Current);
        }

        return string.Format(
            Translations.Instance.PluginInstallFailedFormat, string.Join("; ", ex.Errors));
    }

    /// <summary>
    /// 补充本地化警告：安装器给的 displayName 变化警告是英文的，这里按当前语言重述并替换，
    /// 其余警告原样保留（预览警告本就是给用户看的技术细节）。
    /// </summary>
    private PluginInstallPreview EnrichWarnings(PluginInstallPreview preview)
    {
        var warnings = new List<string>();

        var existing = _catalog.Installed.FirstOrDefault(
            installation => string.Equals(
                installation.Manifest.Id, preview.Manifest.Id, StringComparison.Ordinal));
        if (existing is not null
            && !string.Equals(existing.Manifest.DisplayName, preview.Manifest.DisplayName, StringComparison.Ordinal))
        {
            warnings.Add(string.Format(
                Translations.Instance.PluginDisplayNameChangedWarningFormat,
                existing.Manifest.DisplayName,
                preview.Manifest.DisplayName));
        }

        foreach (var warning in preview.Warnings)
        {
            if (!warning.Contains("displayName is changing", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(warning);
            }
        }

        return preview with { Warnings = warnings };
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasRows));

    /// <summary>语言切换：重建行让状态 / 版本 / 作者串按新语言重译；区块级错误保持出现时语言。</summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            RebuildRows();
        }
    }
}
