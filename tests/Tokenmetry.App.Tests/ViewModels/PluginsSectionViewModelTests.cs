using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;
using Xunit;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// 数据与计价页「插件」区块 VM（Task 8）：安装确认流、取消不动作、启停/卸载后的
/// 提供者刷新 + 重扫、无效目录行与宿主缺失态。断言语言无关（引用 Translations.Instance 同源）。
/// </summary>
public sealed class PluginsSectionViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Install_command_installs_after_confirmation()
    {
        var installer = new FakePluginInstaller();
        var sourceProvider = new FakeSourceProvider();
        var rescanCalls = 0;
        var vm = CreateVm(
            installer: installer,
            sourceProvider: sourceProvider,
            picker: new FakeFilePicker("/tmp/foo.tkmplugin"),
            confirmation: new FakeConfirmation(confirm: true),
            rescan: () => { rescanCalls++; return Task.CompletedTask; });

        var stoppedBeforeInstall = false;
        installer.OnInstallStarting = () => stoppedBeforeInstall = sourceProvider.StoppedIds.Count > 0;

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Installed.Should().BeTrue("确认后执行安装");
        installer.InstalledPath.Should().Be("/tmp/foo.tkmplugin");
        sourceProvider.StoppedIds.Should().ContainSingle()
            .Which.Should().Be("com.acme.foo", "升级前先停掉预览 manifest 对应的插件宿主");
        stoppedBeforeInstall.Should().BeTrue("停止宿主必须发生在替换插件文件之前");
        sourceProvider.RefreshCount.Should().Be(1, "安装后刷新插件源提供者");
        rescanCalls.Should().Be(1, "安装后触发一轮重扫");
        vm.InstallError.Should().BeNull();
    }

    [Fact]
    public async Task Install_command_does_nothing_when_confirmation_is_false()
    {
        var installer = new FakePluginInstaller();
        var sourceProvider = new FakeSourceProvider();
        var vm = CreateVm(
            installer: installer,
            sourceProvider: sourceProvider,
            picker: new FakeFilePicker("/tmp/foo.tkmplugin"),
            confirmation: new FakeConfirmation(confirm: false));

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Installed.Should().BeFalse("取消 = 不动作");
        sourceProvider.RefreshCount.Should().Be(0);
        vm.InstallError.Should().BeNull();
    }

    [Fact]
    public async Task Install_command_does_nothing_when_picker_is_cancelled()
    {
        var installer = new FakePluginInstaller();
        var confirmation = new FakeConfirmation(confirm: true);
        var vm = CreateVm(
            installer: installer,
            picker: new FakeFilePicker(null),
            confirmation: confirmation);

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Installed.Should().BeFalse();
        confirmation.ConfirmCalls.Should().Be(0, "没选文件就不进入确认");
    }

    [Fact]
    public async Task Install_preview_failure_surfaces_error_and_never_installs()
    {
        var installer = new FakePluginInstaller
        {
            PreviewException = new PluginInstallException(["plugin.json is missing"]),
        };
        var sourceProvider = new FakeSourceProvider();
        var vm = CreateVm(installer: installer, sourceProvider: sourceProvider);

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Installed.Should().BeFalse();
        vm.InstallError.Should().NotBeNullOrEmpty("校验失败如实报错，不伪造成功");
        sourceProvider.RefreshCount.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_catalog_entry_is_rendered_as_error_row()
    {
        var catalog = new FakePluginCatalog();
        catalog.InvalidList.Add(new PluginCatalogError("/plugins/broken/1.0.0", "plugin.json is missing"));
        var vm = CreateVm(catalog: catalog);

        await vm.RefreshAsync();

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.IsInvalid.Should().BeTrue();
        row.StatusText.Should().Be(Translations.Instance.PluginInvalidPackageFormat);
        row.ErrorText.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Host_missing_disables_install_and_reports_status()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo", enabled: true));
        var vm = CreateVm(catalog: catalog, hostPath: null);

        await vm.RefreshAsync();

        vm.HostAvailable.Should().BeFalse();
        vm.InstallCommand.CanExecute(null).Should().BeFalse("宿主缺失时安装不可用");
        vm.Rows.Should().ContainSingle().Subject.StatusText
            .Should().Be(Translations.Instance.PluginHostMissing);
    }

    [Fact]
    public async Task Installed_row_reports_scan_status_from_registry()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo", enabled: true));
        var registry = new IngestionScanRegistry();
        registry.ReportScan("Foo", JsonlScanStatus.Ok, 12, Now);
        var vm = CreateVm(catalog: catalog, registry: registry);

        await vm.RefreshAsync();

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.IsEnabled.Should().BeTrue();
        row.IsInvalid.Should().BeFalse();
        row.StatusText.Should().Be(Translations.Instance.PluginStatusOk);
    }

    [Fact]
    public async Task Toggle_command_sets_enabled_refreshes_provider_and_rescans()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo", enabled: true));
        var sourceProvider = new FakeSourceProvider();
        var rescanCalls = 0;
        var vm = CreateVm(
            catalog: catalog,
            sourceProvider: sourceProvider,
            rescan: () => { rescanCalls++; return Task.CompletedTask; });

        await vm.RefreshAsync();
        await vm.ToggleCommand.ExecuteAsync(vm.Rows.Single());

        catalog.LastEnabled.Should().Be(false, "启用中的插件被停用");
        sourceProvider.RefreshCount.Should().Be(1);
        rescanCalls.Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_command_removes_after_confirmation()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo", enabled: true));
        var confirmation = new FakeConfirmation(confirm: true);
        var sourceProvider = new FakeSourceProvider();
        var rescanCalls = 0;
        var vm = CreateVm(
            catalog: catalog,
            sourceProvider: sourceProvider,
            confirmation: confirmation,
            rescan: () => { rescanCalls++; return Task.CompletedTask; });

        await vm.RefreshAsync();

        var stoppedBeforeUninstall = false;
        catalog.OnUninstallStarting = () => stoppedBeforeUninstall = sourceProvider.StoppedIds.Count > 0;

        await vm.UninstallCommand.ExecuteAsync(vm.Rows.Single());

        confirmation.UninstallConfirmCalls.Should().Be(1);
        catalog.Uninstalled.Should().BeTrue();
        sourceProvider.StoppedIds.Should().ContainSingle()
            .Which.Should().Be("com.acme.foo", "卸载前先停掉宿主以释放文件");
        stoppedBeforeUninstall.Should().BeTrue("停止宿主必须发生在删除插件文件之前");
        sourceProvider.RefreshCount.Should().Be(1);
        rescanCalls.Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_command_does_nothing_when_confirmation_is_false()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo", enabled: true));
        var confirmation = new FakeConfirmation(confirm: false);
        var vm = CreateVm(catalog: catalog, confirmation: confirmation);

        await vm.RefreshAsync();
        await vm.UninstallCommand.ExecuteAsync(vm.Rows.Single());

        catalog.Uninstalled.Should().BeFalse("取消卸载 = 不动作");
    }

    [Fact]
    public async Task Empty_section_is_inert()
    {
        var empty = PluginsSectionViewModel.Empty;

        empty.HostAvailable.Should().BeFalse();
        empty.Rows.Should().BeEmpty();
        empty.InstallCommand.CanExecute(null).Should().BeFalse();
        await empty.RefreshAsync();
        await empty.InstallCommand.ExecuteAsync(null);
        await empty.ToggleCommand.ExecuteAsync(null);
        await empty.UninstallCommand.ExecuteAsync(null);
    }

    private static PluginsSectionViewModel CreateVm(
        FakePluginCatalog? catalog = null,
        FakePluginInstaller? installer = null,
        FakeSourceProvider? sourceProvider = null,
        IPluginFilePicker? picker = null,
        FakeConfirmation? confirmation = null,
        Func<Task>? rescan = null,
        string? hostPath = "/host",
        IngestionScanRegistry? registry = null)
    {
        return new PluginsSectionViewModel(
            catalog ?? new FakePluginCatalog(),
            installer ?? new FakePluginInstaller(),
            sourceProvider ?? new FakeSourceProvider(),
            picker ?? new FakeFilePicker("/tmp/foo.tkmplugin"),
            confirmation ?? new FakeConfirmation(confirm: true),
            AgentIconService.BuiltInOnly,
            registry ?? new IngestionScanRegistry(),
            rescan ?? (() => Task.CompletedTask),
            hostPath);
    }

    private static PluginInstallation Installation(string id, string displayName, bool enabled) =>
        new(
            new PluginManifest
            {
                Id = id,
                AgentId = id,
                DisplayName = displayName,
                Version = "1.0.0",
                ApiVersion = PluginApiVersion.Current.ToString(),
                EntryAssembly = "Plugin.dll",
                EntryType = "Plugin.Entry",
                Icon = "icon.png",
            },
            Directory: "/plugins/" + id,
            IconPath: "/plugins/" + id + "/icon.png",
            Enabled: enabled);

    private sealed class FakePluginCatalog : IPluginCatalog
    {
        public List<PluginInstallation> InstalledList { get; } = new();

        public List<PluginCatalogError> InvalidList { get; } = new();

        public IReadOnlyList<PluginInstallation> Installed => InstalledList;

        public IReadOnlyList<PluginCatalogError> Invalid => InvalidList;

        public bool? LastEnabled { get; private set; }

        public bool Uninstalled { get; private set; }

        public Action? OnUninstallStarting { get; set; }

        public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken)
        {
            LastEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task UninstallAsync(string pluginId, CancellationToken cancellationToken)
        {
            OnUninstallStarting?.Invoke();
            Uninstalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePluginInstaller : IPluginInstaller
    {
        public PluginInstallException? PreviewException { get; init; }

        public bool Installed { get; private set; }

        public string? InstalledPath { get; private set; }

        public Action? OnInstallStarting { get; set; }

        public PluginInstallPreview Preview(string packagePath)
        {
            if (PreviewException is not null)
            {
                throw PreviewException;
            }

            return new PluginInstallPreview(Manifest(), [], packagePath);
        }

        public Task<PluginInstallation> InstallAsync(string packagePath, CancellationToken cancellationToken)
        {
            OnInstallStarting?.Invoke();
            Installed = true;
            InstalledPath = packagePath;
            return Task.FromResult(new PluginInstallation(Manifest(), "/plugins/foo", "/plugins/foo/icon.png", true));
        }

        private static PluginManifest Manifest() => new()
        {
            Id = "com.acme.foo",
            AgentId = "com.acme.foo",
            DisplayName = "Foo",
            Version = "1.0.0",
            ApiVersion = PluginApiVersion.Current.ToString(),
            EntryAssembly = "Foo.dll",
            EntryType = "Foo.Plugin",
            Icon = "icon.png",
            Author = "Acme",
        };
    }

    private sealed class FakeSourceProvider : IPluginSourceProvider
    {
        public int RefreshCount { get; private set; }

        public List<string> StoppedIds { get; } = [];

        public IReadOnlyList<PluginUsageSource> GetEnabledSources() => [];

        public void Refresh() => RefreshCount++;

        public void StopPlugin(string pluginId) => StoppedIds.Add(pluginId);
    }

    private sealed class FakeFilePicker(string? path) : IPluginFilePicker
    {
        public Task<string?> PickPackageAsync() => Task.FromResult(path);
    }

    private sealed class FakeConfirmation(bool confirm) : IPluginInstallConfirmation
    {
        public int ConfirmCalls { get; private set; }

        public int UninstallConfirmCalls { get; private set; }

        public Task<bool> ConfirmAsync(PluginInstallPreview preview)
        {
            ConfirmCalls++;
            return Task.FromResult(confirm);
        }

        public Task<bool> ConfirmUninstallAsync(string displayName)
        {
            UninstallConfirmCalls++;
            return Task.FromResult(confirm);
        }
    }
}
