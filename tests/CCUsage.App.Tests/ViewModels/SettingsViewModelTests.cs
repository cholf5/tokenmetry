using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.Core.Models;

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

        var viewModel = new SettingsViewModel(settings, paths, new FakeSettingsStore());

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
        var viewModel = new SettingsViewModel(settings, new AppPaths("C:\\ccusage"), store)
        {
            CcusageCommand = "  ccusage.cmd  ",
            RefreshMinutesText = "45",
            AnomalyNotificationsEnabled = false,
            ShowDebugInfo = false
        };

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
        var viewModel = new SettingsViewModel(settings, new AppPaths("C:\\ccusage"), new FakeSettingsStore())
        {
            CcusageCommand = "   ",
            RefreshMinutesText = "15"
        };

        await viewModel.SaveAsync();

        settings.CcusageCommand.Should().Be("ccusage");
        viewModel.CcusageCommand.Should().Be("ccusage");
    }

    [Fact]
    public async Task SaveAsync_WithInvalidRefreshInterval_DoesNotSave()
    {
        var settings = new AppSettings { RefreshMinutes = 15 };
        var store = new FakeSettingsStore();
        var viewModel = new SettingsViewModel(settings, new AppPaths("C:\\ccusage"), store)
        {
            RefreshMinutesText = "0"
        };

        await viewModel.SaveAsync();

        store.SaveCalls.Should().Be(0);
        settings.RefreshMinutes.Should().Be(15);
        viewModel.Status.Should().Be("refresh interval must be between 1 and 1440 minutes");
    }

    [Fact]
    public async Task SaveAsync_WhenStoreFails_ShowsError()
    {
        var store = new FakeSettingsStore { SaveException = new InvalidOperationException("cannot write") };
        var viewModel = new SettingsViewModel(new AppSettings(), new AppPaths("C:\\ccusage"), store);

        await viewModel.SaveAsync();

        viewModel.Status.Should().Be("error: cannot write");
        viewModel.IsSaving.Should().BeFalse();
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
