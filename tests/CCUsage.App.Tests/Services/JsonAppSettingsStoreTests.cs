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
        loaded.Theme.Should().Be("system"); // 缺省值往返
        loaded.Language.Should().Be("system");
        File.Exists(Path.Combine(_tempDirectory, "settings.json")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_RoundTripsLanguageAndThemePreferences()
    {
        // 界面语言与主题同为字符串偏好（未知值在解析处回落，不炸整体反序列化）。
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));
        var settings = new AppSettings { Theme = "dark", Language = "en" };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        loaded.Theme.Should().Be("dark");
        loaded.Language.Should().Be("en");
    }

    [Fact]
    public async Task SaveAsync_RoundTripsPricingOverrides()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));
        var settings = new AppSettings();
        settings.PricingOverrides.Add(new ModelPricingOverride
        {
            Model = "claude-sonnet-4-6",
            InputPerMillion = 9m,
            OutputPerMillion = 90m,
            CacheWritePerMillion = 11.25m,
            CacheReadPerMillion = 0.9m
        });
        settings.PricingOverrides.Add(new ModelPricingOverride
        {
            Model = "my-model",
            InputPerMillion = 1m,
            OutputPerMillion = 2m
        });

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        loaded.PricingOverrides.Should().HaveCount(2);
        loaded.PricingOverrides[0].Model.Should().Be("claude-sonnet-4-6");
        loaded.PricingOverrides[0].InputPerMillion.Should().Be(9m);
        loaded.PricingOverrides[0].OutputPerMillion.Should().Be(90m);
        loaded.PricingOverrides[0].CacheWritePerMillion.Should().Be(11.25m);
        loaded.PricingOverrides[0].CacheReadPerMillion.Should().Be(0.9m);
        loaded.PricingOverrides[1].CacheWritePerMillion.Should().BeNull();
        loaded.PricingOverrides[1].CacheReadPerMillion.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenFileCorrupt_SelfHealsToDefaultsAndKeepsEvidence()
    {
        // A-7：settings.json 损坏不再启动即崩——回落默认 + 坏文件改名 .bad-时间戳留证（不静默销毁）。
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "settings.json"), "{ not valid json");

        var settings = await store.LoadAsync();

        settings.CcusageCommand.Should().Be("ccusage");
        settings.RefreshMinutes.Should().Be(15);
        File.Exists(Path.Combine(_tempDirectory, "settings.json")).Should().BeFalse();
        Directory.EnumerateFiles(_tempDirectory, "settings.json.bad-*").Should().ContainSingle();
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFileBehind()
    {
        // A-7 原子保存：tmp + Move(overwrite) 后不残留半截临时文件。
        var store = new JsonAppSettingsStore(new AppPaths(_tempDirectory));
        Directory.CreateDirectory(_tempDirectory);

        await store.SaveAsync(new AppSettings { CcusageCommand = "x" });

        File.Exists(Path.Combine(_tempDirectory, "settings.json")).Should().BeTrue();
        Directory.EnumerateFiles(_tempDirectory, "settings.json.tmp*").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
