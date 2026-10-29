using System;
using System.Linq;
using System.Threading.Tasks;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// 设置页 VM 测试（S-004 收缩为应用偏好）：主题 / 语言 / 刷新间隔 / 关窗行为 / 开机自启的
/// 全自动保存语义。数据源表、重扫、模型目录与别名映射在 DataPricingViewModelTests。
/// </summary>
public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesFromSettingsAndPaths()
    {
        var settings = new AppSettings
        {
            RefreshMinutes = 20
        };
        var paths = new AppPaths("C:\\Users\\tester\\AppData\\Local\\Tokenmetry");

        var viewModel = CreateViewModel(settings, paths, new FakeSettingsStore());

        viewModel.RefreshMinutesText.Should().Be("20");
        viewModel.CacheDbPath.Should().Be(System.IO.Path.Combine(paths.AppDataDirectory, "tokenmetry.db"));
        viewModel.SettingsFilePath.Should().Be(System.IO.Path.Combine(paths.AppDataDirectory, "settings.json"));
    }

    // ---- I-041：关于区法律信息行的单一出处接线（版权 = 程序集属性 / URL = UpdateAssets）----
    // 断言语言无关（不断言中文/英文文案）；<Copyright> 被从 Directory.Build.props 删掉时这里抓到空串。

    [Fact]
    public void AboutLegalInfo_UsesAssemblyAndUpdateAssetsAsSingleSource()
    {
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.AboutCopyright.Should().NotBeNullOrWhiteSpace().And.Contain("cholf5");
        viewModel.AboutRepositoryUrl.Should().Be($"https://github.com/{UpdateAssets.Repository}");
        viewModel.AboutLicenseUrl.Should().Be($"https://github.com/{UpdateAssets.Repository}/blob/main/LICENSE");
    }

    // ---- 全自动保存（无保存按钮：各设置项改完即生效并落盘）----

    [Fact]
    public void ThemeChange_AppliesAndPersistsImmediately()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var themeService = new FakeThemeService();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store, themeService);

        viewModel.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Value == "dark");

        themeService.LastApplied.Should().Be("dark");
        settings.Theme.Should().Be("dark");
        store.SaveCalls.Should().Be(1);
    }

    [Fact]
    public void RefreshMinutesTextChange_WithValidValue_PersistsAndClearsError()
    {
        var settings = new AppSettings { RefreshMinutes = 15 };
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store);

        viewModel.RefreshMinutesText = "45";

        settings.RefreshMinutes.Should().Be(45);
        store.SaveCalls.Should().Be(1);
        viewModel.RefreshMinutesError.Should().BeNull();
    }

    [Fact]
    public void RefreshMinutesTextChange_WithInvalidValue_ShowsInlineErrorAndDoesNotPersist()
    {
        var settings = new AppSettings { RefreshMinutes = 15 };
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store);

        viewModel.RefreshMinutesText = "0";

        settings.RefreshMinutes.Should().Be(15); // 非法不落盘，保留上一个合法值
        store.SaveCalls.Should().Be(0);
        viewModel.RefreshMinutesError.Should().Be(Translations.Instance.RefreshIntervalOutOfRange);

        viewModel.RefreshMinutesText = "abc";
        viewModel.RefreshMinutesError.Should().Be(Translations.Instance.RefreshIntervalMustBeNumber);
        store.SaveCalls.Should().Be(0);

        viewModel.RefreshMinutesText = "30"; // 回到合法 → 落盘 + 提示清除
        settings.RefreshMinutes.Should().Be(30);
        store.SaveCalls.Should().Be(1);
        viewModel.RefreshMinutesError.Should().BeNull();
    }

    [Fact]
    public void MinimizeToTrayOnCloseToggle_PersistsImmediately()
    {
        // S-005：关窗行为开关全自动保存；消费者 = MainWindow.OnClosing 的关窗分派。
        var settings = new AppSettings { MinimizeToTrayOnClose = true };
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store);

        viewModel.MinimizeToTrayOnClose = false;

        settings.MinimizeToTrayOnClose.Should().BeFalse();
        store.SaveCalls.Should().Be(1);

        viewModel.MinimizeToTrayOnClose = true;

        settings.MinimizeToTrayOnClose.Should().BeTrue();
        store.SaveCalls.Should().Be(2);
    }

    // ---- 开机自启动（S-006：OS 注册 = IAutoStartManager，注册失败回滚开关且不落盘）----

    [Fact]
    public void Constructor_SeedsAutoStartOnBootFromSettings()
    {
        var settings = new AppSettings { AutoStartOnBoot = true };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.AutoStartOnBoot.Should().BeTrue();
    }

    [Fact]
    public void AutoStartOnBootToggle_RegistersOsAndPersistsImmediately()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var manager = new FakeAutoStartManager();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store, autoStartManager: manager);

        viewModel.AutoStartOnBoot = true;

        manager.Calls.Should().Equal(true);
        settings.AutoStartOnBoot.Should().BeTrue();
        store.SaveCalls.Should().Be(1);

        viewModel.AutoStartOnBoot = false;

        manager.Calls.Should().Equal(true, false);
        settings.AutoStartOnBoot.Should().BeFalse();
        store.SaveCalls.Should().Be(2);
    }

    [Fact]
    public void AutoStartOnBootToggle_WhenOsRegistrationFails_RevertsToggleAndDoesNotPersist()
    {
        // OS 未生效不落盘：settings 与 OS 状态保持一致；开关回滚显示（字段直写，不重入回调）。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var manager = new FakeAutoStartManager { SetEnabledException = new InvalidOperationException("registry denied") };
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store, autoStartManager: manager);

        viewModel.AutoStartOnBoot = true;

        manager.Calls.Should().HaveCount(1);        // 尝试过注册
        viewModel.AutoStartOnBoot.Should().BeFalse(); // 开关回滚显示
        settings.AutoStartOnBoot.Should().BeFalse();  // settings 保持旧值
        store.SaveCalls.Should().Be(0);               // 不落盘
    }

    [Fact]
    public void SettingsChange_WhenStoreFails_KeepsUiEffectiveAndDoesNotThrow()
    {
        // best-effort 自动保存：磁盘失败不阻断 UI 已生效的更改（诊断日志，下次更改重试）。
        var settings = new AppSettings();
        var store = new FakeSettingsStore { SaveException = new InvalidOperationException("cannot write") };
        var themeService = new FakeThemeService();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store, themeService);

        viewModel.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Value == "dark");

        themeService.LastApplied.Should().Be("dark"); // UI 侧生效
        settings.Theme.Should().Be("dark");           // 内存设置生效
        store.SavedSettings.Should().BeNull();        // 落盘失败（待下次更改重试）
    }

    [Fact]
    public void Constructor_ResolvesThemePreferenceFromSettings()
    {
        var settings = new AppSettings { Theme = "dark" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.SelectedTheme.Should().Be(SettingsViewModel.ThemeOptions.Single(o => o.Value == "dark"));
    }

    [Fact]
    public void Constructor_WithUnknownThemePreference_FallsBackToSystem()
    {
        var settings = new AppSettings { Theme = "sepia" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.SelectedTheme.Value.Should().Be("system");
    }

    // ---- 界面语言（system / zh / zh-hant / en / ja / ko / pt-br / ru，机制与 Theme 全链路同构）----

    [Fact]
    public void Constructor_ResolvesLanguagePreferenceFromSettings()
    {
        var settings = new AppSettings { Language = "en" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.SelectedLanguage.Should().Be(SettingsViewModel.LanguageOptions.Single(o => o.Value == "en"));
    }

    [Theory]
    [InlineData("zh-hant")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("pt-br")]
    [InlineData("ru")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void Constructor_ResolvesNewLanguagePreferencesFromSettings(string code)
    {
        // D-024 新语种：持久化的新语言代码要能对回稳定选项实例（未知值才回落 system）。
        var settings = new AppSettings { Language = code };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.SelectedLanguage.Should().Be(SettingsViewModel.LanguageOptions.Single(o => o.Value == code));
    }

    [Fact]
    public void Constructor_WithUnknownLanguagePreference_FallsBackToSystem()
    {
        var settings = new AppSettings { Language = "klingon" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.SelectedLanguage.Value.Should().Be("system");
    }

    [Fact]
    public void SelectedLanguageChange_PersistsImmediately_WithoutWaitingForSaveButton()
    {
        // 对齐 stupid-dict 的语言切换语义：改完即落盘（真机反馈：不即时持久化重启会回退）。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), store);
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            viewModel.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");

            store.SaveCalls.Should().Be(1); // 切换即保存，不等「保存设置」
            settings.Language.Should().Be("en");
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线避免污染其他用例
        }
    }

    [Fact]
    public void SelectedLanguageChange_RetranslatesOptionLabels()
    {
        // 选项 Label 实时刷新（稳定实例 + RefreshLabel，规避 ComboBox 快照坑）。
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

            viewModel.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");

            Translations.Instance.CurrentLanguage.Should().Be(AppLanguage.English);
            SettingsViewModel.ThemeOptions.Single(o => o.Value == "light").Label.Should().Be("Light");
            SettingsViewModel.LanguageOptions.Single(o => o.Value == "zh").Label.Should().Be("简体中文");
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage);
        }
    }

    // ---- 页面描述符（S-004 拆分后设置页 = 应用偏好）----

    [Fact]
    public void Page_DescriptorMatchesSettingsNavigationEntry()
    {
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore());

        viewModel.Page.Id.Should().Be(PageId.Settings);
        viewModel.Page.Title.Should().Be("设置");
        viewModel.Page.Subtitle.Should().Be("主题、语言与刷新偏好；改完即生效并自动保存");
    }

    // ---- 更新检查（I-039：状态行/按钮随检查结果切换；文案断言语言无关，只碰状态布尔与版本数字）----

    [Fact]
    public async Task UpdateSection_constructor_syncs_known_available_state_from_service()
    {        // 托盘里查过「发现新版本」再打开设置页：构造即显示结论而非空白。
        var (service, _) = await Tokenmetry.App.Tests.Update.UpdateServiceTestsHelper.CreateChecked(
            latestTag: "v0.3.0", currentVersion: "v0.2.0");
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore(), updateService: service);

        viewModel.IsUpdateAvailable.Should().BeTrue();
        viewModel.UpdateStatusText.Should().Contain("0.3.0");
    }

    [Fact]
    public async Task CheckUpdate_command_reports_up_to_date_without_download_buttons()
    {
        var settings = new AppSettings();
        var service = Tokenmetry.App.Tests.Update.UpdateServiceTestsHelper.CreateService(
            latestTag: "v0.2.0", currentVersion: "v0.2.0");
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\tokenmetry"), new FakeSettingsStore(), updateService: service);

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        viewModel.IsUpdateAvailable.Should().BeFalse();
        viewModel.UpdateStatusText.Should().Contain("0.2.0");
    }

    [Fact]
    public async Task CheckUpdate_command_reports_available_and_shows_download_buttons()
    {
        var service = Tokenmetry.App.Tests.Update.UpdateServiceTestsHelper.CreateService(
            latestTag: "v0.3.0", currentVersion: "v0.2.0");
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore(), updateService: service);

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        viewModel.IsUpdateAvailable.Should().BeTrue();
        viewModel.UpdateStatusText.Should().Contain("0.3.0");
    }

    [Fact]
    public async Task CheckUpdate_command_failure_lands_in_status_text_not_exception()
    {
        var service = Tokenmetry.App.Tests.Update.UpdateServiceTestsHelper.CreateFailing();
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\tokenmetry"), new FakeSettingsStore(), updateService: service);

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        viewModel.IsUpdateAvailable.Should().BeFalse();
        viewModel.UpdateStatusText.Should().NotBeNullOrEmpty();
    }

    // ---- helpers ----

    private static SettingsViewModel CreateViewModel(
        AppSettings settings,
        AppPaths paths,
        FakeSettingsStore store,
        IThemeService? themeService = null,
        FakeAutoStartManager? autoStartManager = null,
        Tokenmetry.App.Update.UpdateService? updateService = null)
    {
        return new SettingsViewModel(
            settings,
            paths,
            store,
            themeService ?? new FakeThemeService(),
            autoStartManager ?? new FakeAutoStartManager(),
            updateService);
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string? LastApplied { get; private set; }

        public void Apply(string? themePreference)
        {
            LastApplied = themePreference;
        }
    }

    private sealed class FakeAutoStartManager : IAutoStartManager
    {
        public List<bool> Calls { get; } = new();

        public Exception? SetEnabledException { get; init; }

        public void SetEnabled(bool enabled)
        {
            Calls.Add(enabled);
            if (SetEnabledException != null)
            {
                throw SetEnabledException;
            }
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public int SaveCalls { get; private set; }
        public AppSettings? SavedSettings { get; private set; }
        public Exception? SaveException { get; init; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new AppSettings());
        }

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCalls++;
            if (SaveException != null)
            {
                throw SaveException;
            }

            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }
}
