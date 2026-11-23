namespace Tokenmetry.Infrastructure.Tests.Cache;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Cache;

/// <summary>设置同步账面（settings-sync 设计 §5.1，迁移 4）：upsert / 读回 / 清空。</summary>
public sealed class SqliteUsageStoreSettingsSyncStateTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteUsageStore _store;

    public SqliteUsageStoreSettingsSyncStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-settings-sync-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _store = new SqliteUsageStore(Path.Combine(_directory, "cache.db"));
    }

    [Fact]
    public async Task Upsert_RoundTrips_AndOverwrites()
    {
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        await _store.UpsertSettingsSyncStateAsync(
        [
            new SyncedSettingState("theme", "dark", at),
            new SyncedSettingState("language", "zh", at),
        ]);
        await _store.UpsertSettingsSyncStateAsync(
        [
            new SyncedSettingState("theme", "light", at.AddMinutes(1)),
        ]);

        var rows = await _store.GetSettingsSyncStateAsync();

        rows.Should().HaveCount(2);
        rows.Single(row => row.Key == "theme").Should().Be(new SyncedSettingState("theme", "light", at.AddMinutes(1)));
        rows.Single(row => row.Key == "language").Value.Should().Be("zh");
    }

    [Fact]
    public async Task Clear_RemovesAllRows()
    {
        await _store.UpsertSettingsSyncStateAsync([new SyncedSettingState("theme", "dark", DateTimeOffset.UtcNow)]);

        await _store.ClearSettingsSyncStateAsync();

        (await _store.GetSettingsSyncStateAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
