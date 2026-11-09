namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 中继设备与账号管理端点（relay 计划 Task 7 / 设计 A11、A16）：
/// 设备注册/列表/改名、解绑级联清行、删号全清。
/// </summary>
public sealed class DeviceEndpointsTests
{
    private static readonly DateTimeOffset T1 = new(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, string? jsonBody = null, string? device = null)
    {
        using var message = new HttpRequestMessage(method, uri);
        if (device is not null)
        {
            message.Headers.Add("X-Tokenmetry-Device", device);
        }

        if (jsonBody is not null)
        {
            message.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(message);
    }

    [Fact]
    public async Task RegisterListRename_FlowsThrough()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);

        var registered = await SendAsync(devA, HttpMethod.Post, "/api/devices",
            """{"deviceId":"devA","name":"Work Mac"}""", device: "devA");
        registered.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = JsonSerializer.Deserialize(
            await (await SendAsync(devA, HttpMethod.Get, "/api/devices", device: "devA")).Content.ReadAsStreamAsync(),
            SyncJsonContext.Default.SyncDeviceListResponse)!;
        list.Devices.Should().ContainSingle().Which.Should().Be(new SyncDeviceInfo("devA", "Work Mac", list.Devices[0].LastSeenAt));

        var renamed = await SendAsync(devA, HttpMethod.Patch, "/api/devices/devA",
            """{"name":"Renamed"}""", device: "devA");
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = JsonSerializer.Deserialize(
            await (await SendAsync(devA, HttpMethod.Get, "/api/devices", device: "devA")).Content.ReadAsStreamAsync(),
            SyncJsonContext.Default.SyncDeviceListResponse)!;
        after.Devices.Should().ContainSingle().Which.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task RemoveDevice_CascadesRows()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>
            {
                new() { Device = "devA", SessionId = "s-a", TotalTokens = 100, FirstSeenAt = T1, LastSeenAt = T1, UpdatedAt = T1 },
            }))).EnsureSuccessStatusCode();

        var removed = await SendAsync(devB, HttpMethod.Delete, "/api/devices/devA", device: "devB");
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().BeEmpty("解绑 = 该设备行全清（A16）");
        pulled.SessionFacts.Should().BeEmpty();

        var list = JsonSerializer.Deserialize(
            await (await SendAsync(devB, HttpMethod.Get, "/api/devices", device: "devB")).Content.ReadAsStreamAsync(),
            SyncJsonContext.Default.SyncDeviceListResponse)!;
        list.Devices.Select(device => device.DeviceId).Should().NotContain("devA");
    }

    [Fact]
    public async Task DeleteAccount_RemovesEverything()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var deleted = await devA.DeleteAsync("/api/me");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 旧 token 已随账号行消失：一切 /api 请求 401（A11 删号 = 桶全清）。
        using var after = RelayTestSync.Client(factory);
        (await after.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await after.GetAsync("/api/devices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var health = await after.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK, "health 不受删号影响");
    }
}
