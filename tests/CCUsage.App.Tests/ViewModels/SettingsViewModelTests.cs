using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CCUsage.App.Localization;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.Tests.ViewModels;

public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesFromSettingsAndPaths()
    {
        var settings = new AppSettings
        {
            CcusageCommand = "ccusage.cmd",
            RefreshMinutes = 20,
            AnomalyNotificationsEnabled = false,
            ShowDebugInfo = false
        };
        var paths = new AppPaths("C:\\Users\\tester\\AppData\\Local\\CCUsage");

        var viewModel = CreateViewModel(settings, paths, new FakeSettingsStore(), reader: new FakeScanReader());

        viewModel.CcusageCommand.Should().Be("ccusage.cmd");
        viewModel.RefreshMinutesText.Should().Be("20");
        viewModel.AnomalyNotificationsEnabled.Should().BeFalse();
        viewModel.ShowDebugInfo.Should().BeFalse();
        viewModel.CacheDbPath.Should().Be(Path.Combine(paths.AppDataDirectory, "ccusage.db"));
        viewModel.SettingsFilePath.Should().Be(Path.Combine(paths.AppDataDirectory, "settings.json"));
    }

    [Fact]
    public async Task SaveAsync_UpdatesSettingsAndPersists()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), store);
        viewModel.CcusageCommand = "  ccusage.cmd  ";
        viewModel.RefreshMinutesText = "45";
        viewModel.AnomalyNotificationsEnabled = false;
        viewModel.ShowDebugInfo = false;

        await viewModel.SaveAsync();

        settings.CcusageCommand.Should().Be("ccusage.cmd");
        settings.RefreshMinutes.Should().Be(45);
        settings.AnomalyNotificationsEnabled.Should().BeFalse();
        settings.ShowDebugInfo.Should().BeFalse();
        store.SaveCalls.Should().Be(1);
        store.SavedSettings.Should().BeSameAs(settings);
        viewModel.Status.Should().Be("settings saved");
        viewModel.IsSaving.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_WithBlankCommand_UsesDefaultCommand()
    {
        var settings = new AppSettings { CcusageCommand = "custom" };
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore());
        viewModel.CcusageCommand = "   ";
        viewModel.RefreshMinutesText = "15";

        await viewModel.SaveAsync();

        settings.CcusageCommand.Should().Be("ccusage");
        viewModel.CcusageCommand.Should().Be("ccusage");
    }

    [Fact]
    public async Task SaveAsync_WithInvalidRefreshInterval_DoesNotSave()
    {
        var settings = new AppSettings { RefreshMinutes = 15 };
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), store);
        viewModel.RefreshMinutesText = "0";

        await viewModel.SaveAsync();

        store.SaveCalls.Should().Be(0);
        settings.RefreshMinutes.Should().Be(15);
        viewModel.Status.Should().Be("refresh interval must be between 1 and 1440 minutes");
    }

    [Fact]
    public async Task SaveAsync_WhenStoreFails_ShowsError()
    {
        var store = new FakeSettingsStore { SaveException = new InvalidOperationException("cannot write") };
        var toast = new FakeToastService();
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), store, toast: toast);

        await viewModel.SaveAsync();

        viewModel.Status.Should().Be("error: cannot write");
        viewModel.IsSaving.Should().BeFalse();
        toast.Messages.Should().Contain(string.Format(Translations.Instance.ToastSaveFailedFormat, "cannot write"));
    }

    [Fact]
    public void Constructor_ResolvesThemePreferenceFromSettings()
    {
        var settings = new AppSettings { Theme = "dark" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore());

        viewModel.SelectedTheme.Should().Be(SettingsViewModel.ThemeOptions.Single(o => o.Value == "dark"));
    }

    [Fact]
    public void Constructor_WithUnknownThemePreference_FallsBackToSystem()
    {
        var settings = new AppSettings { Theme = "sepia" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore());

        viewModel.SelectedTheme.Value.Should().Be("system");
    }

    [Fact]
    public async Task SaveAsync_PersistsThemePreferenceAndAppliesIt()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var themeService = new FakeThemeService();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), store, themeService: themeService);
        viewModel.RefreshMinutesText = "15";
        viewModel.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Value == "dark");

        await viewModel.SaveAsync();

        settings.Theme.Should().Be("dark");
        themeService.LastApplied.Should().Be("dark");
        store.SaveCalls.Should().Be(1);
    }

    // ---- 界面语言（zh / en / system，机制与 Theme 全链路同构）----

    [Fact]
    public void Constructor_ResolvesLanguagePreferenceFromSettings()
    {
        var settings = new AppSettings { Language = "en" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore());

        viewModel.SelectedLanguage.Should().Be(SettingsViewModel.LanguageOptions.Single(o => o.Value == "en"));
    }

    [Fact]
    public void Constructor_WithUnknownLanguagePreference_FallsBackToSystem()
    {
        var settings = new AppSettings { Language = "klingon" };

        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore());

        viewModel.SelectedLanguage.Value.Should().Be("system");
    }

    [Fact]
    public void SelectedLanguageChange_PersistsImmediately_WithoutWaitingForSaveButton()
    {
        // 对齐 stupid-dict 的语言切换语义：改完即落盘（真机反馈：不即时持久化重启会回退）。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), store);
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
    public async Task SaveAsync_PersistsLanguagePreferenceAndAppliesIt()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, new AppPaths("C:\\ccusage"), store);
        viewModel.RefreshMinutesText = "15";
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            viewModel.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");

            await viewModel.SaveAsync();

            settings.Language.Should().Be("en");
            Translations.Instance.CurrentLanguage.Should().Be(AppLanguage.English); // 切换即时生效
            store.SaveCalls.Should().Be(2); // 切换时即时保存 1 次 + 「保存设置」整体保存 1 次
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线避免污染其他用例
        }
    }

    [Fact]
    public void SelectedLanguageChange_RetranslatesOptionsAndImperativeTexts()
    {
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore());

            viewModel.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");

            Translations.Instance.CurrentLanguage.Should().Be(AppLanguage.English);
            // 选项 Label 实时刷新（稳定实例 + RefreshLabel，规避 ComboBox 快照坑）
            SettingsViewModel.ThemeOptions.Single(o => o.Value == "light").Label.Should().Be("Light");
            SettingsViewModel.LanguageOptions.Single(o => o.Value == "zh").Label.Should().Be("简体中文");
            // 命令式文案经 CurrentLanguage 信号重译
            viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanIdle);
            Translations.Instance.ScanIdle.Should().Be("Rescan (all sources)");
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage);
        }
    }

    // ---- S-002 数据源表 ----

    [Fact]
    public void Constructor_BuildsSourceRowsFromScanRegistry()
    {
        var reader = new FakeScanReader();
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 2184, Now));
        reader.Register("Copilot CLI", new IngestionSourceScanSnapshot("Copilot CLI", JsonlScanStatus.Partial, 128, Now));
        reader.Register("Gemini CLI", new IngestionSourceScanSnapshot("Gemini CLI", JsonlScanStatus.NoData, 0, Now));
        reader.Registered.Add("ZCode"); // 已注册但本轮尚未扫描

        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore(), reader: reader);

        var rows = viewModel.SourceRows;
        rows.Select(r => r.Agent).Should().Equal("Claude Code", "Copilot CLI", "Gemini CLI", "ZCode");

        var ok = rows[0];
        ok.StatusText.Should().Be("● 已连接");
        ok.IsOk.Should().BeTrue();
        ok.RecordCountText.Should().Be("2,184");
        ok.LastScanText.Should().NotBe("—");

        rows[1].StatusText.Should().Be("● 部分数据");
        rows[1].IsPartial.Should().BeTrue();

        rows[2].StatusText.Should().Be("● 未检出");
        rows[2].IsNoData.Should().BeTrue();
        rows[2].RecordCountText.Should().Be("0");

        rows[3].StatusText.Should().Be("● 尚未扫描");
        rows[3].IsPending.Should().BeTrue();
        rows[3].LastScanText.Should().Be("—");
        rows[3].RecordCountText.Should().Be("—");
    }

    [Fact]
    public void OnNavigatedTo_RebuildsSourceRowsFromLatestSnapshot()
    {
        // 单例页重进刷新（INavigationAware）：进入页面按最新快照重建
        var reader = new FakeScanReader();
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore(), reader: reader);

        viewModel.SourceRows.Should().BeEmpty();

        reader.Registered.Add("Claude Code");
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, Now));
        viewModel.OnNavigatedTo();

        viewModel.SourceRows.Should().HaveCount(1);
        viewModel.SourceRows[0].IsOk.Should().BeTrue();
        viewModel.SourceRows[0].RecordCountText.Should().Be("7");

        // 再次进入时快照已更新（如后台刷新发生）→ 行刷新
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Partial, 9, Now));
        viewModel.OnNavigatedTo();

        viewModel.SourceRows[0].IsPartial.Should().BeTrue();
        viewModel.SourceRows[0].RecordCountText.Should().Be("9");
    }

    [Fact]
    public async Task RescanAsync_CompletesReloadsRowsAndShowsToast()
    {
        // TC-001（全局重扫定案）：按钮扫描态 → 完成后行重建 + toast（成功路径）
        var reader = new FakeScanReader();
        reader.Registered.Add("Claude Code");
        var toast = new FakeToastService();
        var gate = new TaskCompletionSource();
        var refresh = new BackgroundRefreshService(async () =>
        {
            await gate.Task; // 模拟扫描进行中
            reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 42, DateTimeOffset.Now));
            return true;
        });
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore(),
            reader: reader, toast: toast, refreshService: refresh);

        var run = viewModel.RescanAsync();
        viewModel.IsScanning.Should().BeTrue();
        viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanRunning);
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaScanning);

        gate.SetResult();
        await run;

        viewModel.IsScanning.Should().BeFalse();
        viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanIdle);
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaIdle);
        viewModel.SourceRows.Single().RecordCountText.Should().Be("42");
        toast.Messages.Should().Contain(Translations.Instance.ToastScanDone);
        viewModel.Status.Should().Be("scan finished");
    }

    [Fact]
    public async Task RescanAsync_WhenRefreshFails_ShowsFailureInsteadOfFakeSuccess()
    {
        // P2-1：刷新路径可报告成败——失败如实「扫描失败」+ 数据表面板行级提示，不做假成功
        var reader = new FakeScanReader();
        reader.Registered.Add("Claude Code");
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, DateTimeOffset.Now));
        var toast = new FakeToastService();
        var refresh = new BackgroundRefreshService(() => Task.FromResult(false)); // 采集线报错（如适配器异常）
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore(),
            reader: reader, toast: toast, refreshService: refresh);

        await viewModel.RescanAsync();

        viewModel.Status.Should().Be("scan error");
        viewModel.IsScanning.Should().BeFalse();
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaScanFailed);
        toast.Messages.Should().Contain(Translations.Instance.ToastScanFailed);
        toast.Messages.Should().NotContain(Translations.Instance.ToastScanDone);
        // 行仍为最近一次成功快照（如实旧数据 + 面板提示"可能非本轮"）
        viewModel.SourceRows.Single().RecordCountText.Should().Be("7");
    }

    [Fact]
    public async Task RescanAsync_WhenServiceUnwired_FailsHonestlyInsteadOfFakeSuccess()
    {
        // P0 语义：未接线（Headless 测试宿主等无 App 启动序列的环境）重扫 → 接线层抛错 →
        // 服务层转失败结果 → VM 如实「扫描失败」，绝不空转假成功
        var toast = new FakeToastService();
        var refresh = new BackgroundRefreshService(new BackgroundRefreshWiring().RunRefreshAsync);
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"),
            new FakeSettingsStore(), toast: toast, refreshService: refresh);

        await viewModel.RescanAsync();

        viewModel.Status.Should().Be("scan error");
        toast.Messages.Should().Contain(Translations.Instance.ToastScanFailed);
    }

    [Fact]
    public async Task RescanAsync_MergesIntoInFlightRefresh_WithoutExtraRun()
    {
        // 与定时器刷新共用同一单飞槽（审查修复轮 P2-2 合并语义）：已有刷新在跑时，
        // 重扫并入该轮等待其真实结果，不追加第二轮
        var ticks = 0;
        var gate = new TaskCompletionSource();
        var refresh = new BackgroundRefreshService(async () =>
        {
            Interlocked.Increment(ref ticks);
            await gate.Task;
            return true;
        });

        var first = refresh.TriggerRefreshAsync(); // 占住单飞槽（模拟定时器刷新在跑）
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"),
            new FakeSettingsStore(), refreshService: refresh);

        var rescan = viewModel.RescanAsync(); // 并入在跑的那轮
        await Task.Delay(50);
        ticks.Should().Be(1, "合并语义下重扫并入在跑的一轮，不得追加第二轮");

        gate.SetResult();
        (await first).Should().BeTrue();
        await rescan;

        ticks.Should().Be(1, "重扫应并入在跑的一轮，而不是再跑一遍");
        viewModel.IsScanning.Should().BeFalse();
        viewModel.Status.Should().Be("scan finished");
    }

    // ---- S-002 统计口径区定案 ----

    [Fact]
    public void CaliberItems_AreAllExplanatoryRowsWithoutFakeSwitches()
    {
        // 原型 5 个复选项逐项定案：4 项始终生效（管道内建行为）+ 1 项未提供（未实现），
        // 全部为说明样式（CaliberItem 记录，非布尔开关），不落假开关进 AppSettings。
        var items = SettingsViewModel.CaliberItems;

        items.Should().HaveCount(5);
        items.Should().OnlyContain(i => i.Badge == "始终生效" || i.Badge == "未提供");
        items.Count(i => i.Badge == "始终生效").Should().Be(4);
        items.Count(i => i.Badge == "未提供").Should().Be(1);
        items.Should().Contain(i => i.Text.Contains("按 Agent + Session ID 去重"));
        items.Should().Contain(i => i.Text.Contains("区分输入、输出、缓存读取与缓存写入"));
        items.Should().Contain(i => i.Text.Contains("未知模型价格显示为「未知」"));
        items.Should().Contain(i => i.Text.Contains("自动合并") && i.Badge == "未提供");
        items.Should().Contain(i => i.Text.Contains("仅在本机处理"));
    }

    [Fact]
    public void Page_DescriptorMatchesSettingsNavigationEntry()
    {
        var viewModel = CreateViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), new FakeSettingsStore());

        viewModel.Page.Id.Should().Be(PageId.Settings);
        viewModel.Page.Title.Should().Be("数据源与设置");
        viewModel.Page.Subtitle.Should().Be("多 Agent 统一统计的关键是来源适配、去重和口径透明");    }

    // ---- helpers ----

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 30, 0, TimeSpan.Zero);

    private static SettingsViewModel CreateViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService? themeService = null,
        FakeScanReader? reader = null,
        FakeToastService? toast = null,
        BackgroundRefreshService? refreshService = null)
    {
        return new SettingsViewModel(
            settings,
            paths,
            store,
            themeService ?? new FakeThemeService(),
            reader ?? new FakeScanReader(),
            refreshService ?? new BackgroundRefreshService(() => Task.FromResult(true)),
            toast ?? new FakeToastService());
    }

    /// <summary>扫描注册表读取面桩：Registered/快照可变，供重进刷新与重扫完成断言。
    /// Register 与真实注册表一致（登记扫描即视为该源已注册）。</summary>
    private sealed class FakeScanReader : IIngestionScanReader
    {
        public List<string> Registered { get; } = new();
        private readonly Dictionary<string, IngestionSourceScanSnapshot> _scans = new();

        public IReadOnlyList<string> RegisteredSources => Registered;

        public IngestionSourceScanSnapshot? GetLastScan(string agent) =>
            _scans.GetValueOrDefault(agent);

        public void Register(string agent, IngestionSourceScanSnapshot snapshot)
        {
            if (!Registered.Contains(agent))
            {
                Registered.Add(agent);
            }

            _scans[agent] = snapshot;
        }
    }

    private sealed class FakeToastService : IToastService
    {
        public List<string> Messages { get; } = new();

        public void Show(string message, Avalonia.Visual? host = null)
        {
            Messages.Add(message);
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string? LastApplied { get; private set; }

        public void Apply(string? themePreference)
        {
            LastApplied = themePreference;
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
