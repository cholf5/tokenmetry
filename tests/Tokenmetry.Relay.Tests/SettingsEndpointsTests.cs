namespace Tokenmetry.Relay.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Sync;

/// <summary>
/// 设置同步端点（settings-sync 设计 §4.2）：Bearer 保护、逐 key 往返、服务端时间戳覆盖写、
/// 长度/数量校验与删号级联。
/// </summary>
public sealed class SettingsEndpointsTests
{
    private static async Task<SettingsSyncResponse> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/settings");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStreamAsync(), SyncJsonContext.Default.SettingsSyncResponse)!;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, params SyncedSettingUpdate[] settings)
    {
        var payload = new SettingsSyncPutRequest(settings);
        return client.PutAsync(
            "/api/me/settings",
            new StringContent(
                JsonSerializer.Serialize(payload, SyncJsonContext.Default.SettingsSyncPutRequest),
                Encoding.UTF8,
                "application/json"));
    }

    [Fact]
    public async Task Settings_PutThenGet_Roundtrips()
    {
        using var factory = new RelayFactory(token: "abc");
        var client = RelayTestSync.Client(factory);

        var put = await PutAsync(
            client,
            new SyncedSettingUpdate(SettingsSyncKeysForTest.Theme, "dark"),
            new SyncedSettingUpdate(SettingsSyncKeysForTest.Language, "zh-hant"));
        put.EnsureSuccessStatusCode();

        var get = await GetAsync(client);
        get.Settings.Should().BeEquivalentTo(
        [
            new { Key = "theme", Value = "dark" },
            new { Key = "language", Value = "zh-hant" },
        ], options => options.ExcludingMissingMembers());
        get.Settings.Should().OnlyContain(row => row.UpdatedAt != default, "服务端写入时赋值时间戳");
    }

    [Fact]
    public async Task Settings_PutUpsertsPerKey_WithServerTimestamp()
    {
        using var factory = new RelayFactory(token: "abc");
        var client = RelayTestSync.Client(factory);

        (await PutAsync(client, new SyncedSettingUpdate("theme", "dark"))).EnsureSuccessStatusCode();
        var first = (await GetAsync(client)).Settings.Single();

        (await PutAsync(client, new SyncedSettingUpdate("theme", "light"))).EnsureSuccessStatusCode();
        var second = (await GetAsync(client)).Settings.Single();

        second.Value.Should().Be("light");
        second.UpdatedAt.Should().BeOnOrAfter(first.UpdatedAt);
    }

    [Fact]
    public async Task Settings_RequireBearer()
    {
        using var factory = new RelayFactory(token: "abc");
        var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/me/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settings_InvalidPayload_400()
    {
        using var factory = new RelayFactory(token: "abc");
        var client = RelayTestSync.Client(factory);

        (await PutAsync(client)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "空列表无意义");

        (await PutAsync(client, new SyncedSettingUpdate(string.Empty, "x")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "空 key");

        (await PutAsync(client, new SyncedSettingUpdate("theme", new string('x', 16385))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "value 超长");

        (await PutAsync(client, new SyncedSettingUpdate(new string('k', 65), "x")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "key 超长");
    }

    [Fact]
    public async Task DeleteAccount_InvalidatesSettingsAccess_AndClearsRows()
    {
        // 只断言 401 抓不到孤儿行（账号本身没了，任何 401 都成立）；必须直接看表（审查 #6）。
        var dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-settings-delete-{Guid.NewGuid():N}.db");
        var factory = new RelayFactory(token: "abc", dbPath: dbPath);
        try
        {
            var client = RelayTestSync.Client(factory);

            (await PutAsync(client, new SyncedSettingUpdate("theme", "dark"))).EnsureSuccessStatusCode();

            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            string accountId;
            await using (var before = database.OpenConnection())
            {
                accountId = (await RelayAuth.MapAccountAsync(before, "abc"))!.Id;
                (await CountRowsAsync(before, accountId)).Should().Be(1, "PUT 已落库，删号前有可清的行");
            }

            (await client.DeleteAsync("/api/me")).EnsureSuccessStatusCode();

            (await client.GetAsync("/api/me/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await using var after = database.OpenConnection();
            (await CountRowsAsync(after, accountId)).Should().Be(0, "删号必须级联清空 account_settings");
        }
        finally
        {
            factory.Dispose();
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string accountId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM account_settings WHERE account_id = $accountId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>测试内常量（避免测试工程依赖 App 层的 SettingsSyncKeys）。</summary>
    private static class SettingsSyncKeysForTest
    {
        public const string Theme = "theme";

        public const string Language = "language";
    }
}
