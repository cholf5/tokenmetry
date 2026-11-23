namespace Tokenmetry.App.Headless.Tests.Settings;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;
using Xunit;

/// <summary>
/// 数据与计价页「插件」区块（Task 8）：区块标题 / 安装按钮 / 插件行随区块 VM 渲染，
/// 安装确认弹窗展示第三方代码警告与「作者（未验证）」。断言语言无关（引用 Translations.Instance
/// 同源属性）。
/// </summary>
public sealed class PluginSectionTests
{
    [AvaloniaFact]
    public async Task Plugin_section_renders_rows_and_install_button()
    {
        var catalog = new FakePluginCatalog();
        catalog.InstalledList.Add(Installation("com.acme.foo", "Foo"));
        var plugins = CreateSection(catalog, hostPath: "/host");
        await plugins.RefreshAsync();

        var viewModel = CreateViewModel(plugins);
        var window = Show(new DataPricingView { DataContext = viewModel });

        var title = FindText(window, Translations.Instance.PluginSectionTitle);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(title), "插件区块标题可见");

        var install = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == Translations.Instance.PluginInstallButton);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(install), "安装按钮可见");
        Assert.True(install.IsEnabled, "宿主可用时安装按钮可点");

        var displayName = FindText(window, "Foo");
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(displayName), "插件行显示名可见");

        var status = FindText(window, Translations.Instance.PluginStatusUnscanned);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(status), "扫描状态可见");

        var uninstall = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == Translations.Instance.PluginUninstallAction);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(uninstall), "卸载按钮可见");
    }

    [AvaloniaFact]
    public void Install_dialog_shows_warning_and_unverified_author()
    {
        var preview = new PluginInstallPreview(
            new PluginManifest
            {
                Id = "com.acme.foo",
                AgentId = "com.acme.foo",
                DisplayName = "Foo",
                Version = "1.2.3",
                ApiVersion = PluginApiVersion.Current.ToString(),
                EntryAssembly = "Foo.dll",
                EntryType = "Foo.Plugin",
                Icon = "icon.png",
                Author = "Acme",
            },
            Warnings: ["Agent name will change"],
            PackagePath: "/tmp/foo.tkmplugin");

        var dialog = new PluginInstallConfirmDialog(preview);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var texts = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();

        Assert.Contains(Translations.Instance.PluginInstallWarningTitle, texts);
        Assert.Contains(Translations.Instance.PluginInstallWarningBody, texts);
        Assert.Contains(
            string.Format(Translations.Instance.PluginAuthorUnverifiedFormat, "Acme"),
            texts);
        Assert.Contains("Foo", texts);
        Assert.Contains("Agent name will change", texts);

        var confirm = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == Translations.Instance.PluginInstallConfirm);
        var cancel = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == Translations.Instance.PluginInstallCancel);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(confirm));
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(cancel));

        dialog.Close();
    }

    [AvaloniaFact]
    public void Uninstall_dialog_confirm_button_uses_uninstall_verb()
    {
        var dialog = AvaloniaPluginInstallConfirmation.BuildUninstallDialog("Foo");
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // 标题、正文、肯定按钮都必须是卸载语义；肯定按钮不得复用安装动词。
        var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault);
        Assert.Equal(Translations.Instance.PluginUninstallAction, confirm.Content as string);
        Assert.NotEqual(Translations.Instance.PluginInstallConfirm, confirm.Content as string);

        var body = FindText(dialog, Translations.Instance.PluginUninstallConfirm);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(body), "卸载确认正文可见");
        Assert.Equal(Translations.Instance.PluginUninstallAction, dialog.Title);

        dialog.Close();
    }

    private static PluginsSectionViewModel CreateSection(IPluginCatalog catalog, string? hostPath) =>
        new(
            catalog,
            new FakePluginInstaller(),
            new FakeSourceProvider(),
            new FakeFilePicker(),
            new FakeConfirmation(),
            AgentIconService.BuiltInOnly,
            new IngestionScanRegistry(),
            rescan: () => Task.CompletedTask,
            hostPath);

    private static DataPricingViewModel CreateViewModel(PluginsSectionViewModel plugins) =>
        new(
            new AppSettings(),
            new FakeSettingsStore(),
            new FakeScanReader(),
            new BackgroundRefreshService(() => Task.FromResult(true)),
            new FakeToastService(),
            plugins: plugins);

    private static Window Show(Control content)
    {
        var window = new Window { Width = 1100, Height = 1200, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static TextBlock FindText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);

    private static PluginInstallation Installation(string id, string displayName) =>
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
            Enabled: true);

    private sealed class FakePluginCatalog : IPluginCatalog
    {
        public List<PluginInstallation> InstalledList { get; } = new();

        public IReadOnlyList<PluginInstallation> Installed => InstalledList;

        public IReadOnlyList<PluginCatalogError> Invalid => [];

        public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UninstallAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakePluginInstaller : IPluginInstaller
    {
        public PluginInstallPreview Preview(string packagePath) =>
            throw new PluginInstallException("not used in this test");

        public Task<PluginInstallation> InstallAsync(string packagePath, CancellationToken cancellationToken) =>
            throw new PluginInstallException("not used in this test");
    }

    private sealed class FakeSourceProvider : IPluginSourceProvider
    {
        public IReadOnlyList<PluginUsageSource> GetEnabledSources() => [];

        public void Refresh()
        {
        }
    }

    private sealed class FakeFilePicker : IPluginFilePicker
    {
        public Task<string?> PickPackageAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeConfirmation : IPluginInstallConfirmation
    {
        public Task<bool> ConfirmAsync(PluginInstallPreview preview) => Task.FromResult(false);

        public Task<bool> ConfirmUninstallAsync(string displayName) => Task.FromResult(false);
    }

    private sealed class FakeScanReader : IIngestionScanReader
    {
        public IReadOnlyList<string> RegisteredSources => [];

        public IngestionSourceScanSnapshot? GetLastScan(string agent) => null;

        public IngestionScanProgress? GetScanProgress(string agent) => null;
    }

    private sealed class FakeToastService : IToastService
    {
        public void Show(string message, Avalonia.Visual? host = null)
        {
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}
