using CCUsage.App.Services;
using CCUsage.Core.Models;

namespace CCUsage.App.Tests.Services;

public class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonAppSettingsStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ccusage-settings-tests-" + Guid.NewGuid());
    }

    [Fact]
    public async Task LoadAsync_WhenFileMissing_ReturnsDefaults()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));

        var settings = await store.LoadAsync();

        settings.CcusageCommand.Should().Be("ccusage");
        settings.RefreshMinutes.Should().Be(15);
        settings.AnomalyNotificationsEnabled.Should().BeTrue();
        settings.ShowDebugInfo.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryAndLoadAsync_RoundTripsSettings()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));
        var settings = new AppSettings
        {
            CcusageCommand = "C:\\Tools\\ccusage.cmd",
            RefreshMinutes = 30,
            AnomalyNotificationsEnabled = false,
            ShowDebugInfo = false
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        loaded.CcusageCommand.Should().Be("C:\\Tools\\ccusage.cmd");
        loaded.RefreshMinutes.Should().Be(30);
        loaded.AnomalyNotificationsEnabled.Should().BeFalse();
        loaded.ShowDebugInfo.Should().BeFalse();
        File.Exists(Path.Combine(_tempDirectory, "settings.json")).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
