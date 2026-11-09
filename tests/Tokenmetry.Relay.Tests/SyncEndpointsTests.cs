namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 中继 push/pull（relay 计划 Task 6）：设备间行同步、服务端 LWW、账号级单调 seq、
/// pull 排除 caller 自身行、响应携带全设备清单；畸形载荷 400 化、解绑粘滞到行摄取、
/// pull 快照一致性（并发 push 不被游标越过）与负向路径。
/// </summary>
public sealed class SyncEndpointsTests
{
    private static readonly DateTimeOffset T1 = new(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PushThenPull_RoundTripsRowsBetweenDevices()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        var push = await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            UsageFacts: new List<UsageFact>
            {
                RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            SessionFacts: new List<SessionFact>
            {
                new() { Device = "devA", SessionId = "s-a", TotalTokens = 100, FirstSeenAt = T1, LastSeenAt = T1, CostUsd = 1.00m, UpdatedAt = T1 },
            }));
        push.EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().HaveCount(2, "devB 收到 devA 的两行");
        pulled.UsageFacts.Select(row => row.Device).Should().OnlyContain(device => device == "devA");
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").TotalTokens.Should().Be(100);
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").CostUsd.Should().Be(1.00m);
        pulled.SessionFacts.Should().ContainSingle().Which.SessionId.Should().Be("s-a");
        pulled.HasMore.Should().BeFalse();

        var ownPull = await RelayTestSync.PullAsync(devA, "devA", since: 0);
        ownPull.UsageFacts.Should().BeEmpty("pull 不含 caller 自身行");
        ownPull.SessionFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Push_LwwSkipsOlderVersion()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 222, 2.22m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 111, 1.11m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        var row = pulled.UsageFacts.Should().ContainSingle().Subject;
        row.TotalTokens.Should().Be(222, "旧批次不得覆盖新批次（服务端 LWW 防御）");
        row.CostUsd.Should().Be(2.22m);
        row.UpdatedAt.Should().Be(T2);
    }

    [Fact]
    public async Task Pull_SeqMonotonicAndResumable()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact>
            {
                RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var first = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        first.UsageFacts.Should().HaveCount(2);

        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-21", 300, 3.00m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var resumed = await RelayTestSync.PullAsync(devB, "devB", since: first.Seq);
        resumed.UsageFacts.Should().ContainSingle("since 之后的增量只含第二批");
        resumed.UsageFacts[0].TotalTokens.Should().Be(300);
        resumed.Seq.Should().BeGreaterThan(first.Seq, "seq 账号级单调");
        resumed.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Pull_UpdatesDeviceList()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        // 设备行经注册端点建立（正式客户端流程）；push 不再自动注册设备（终审 C2 修复：解绑不被 push 复活）。
        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(devB, "devB", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devB", "2026-06-19", 50, 0.50m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devA, "devA", since: 0);
        pulled.Devices.Select(device => device.DeviceId).Should().BeEquivalentTo("devA", "devB");
        pulled.Devices.Select(device => device.Name).Should().OnlyContain(name => !string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task Push_AfterUnbind_RejectedAndNotReIngested()
    {
        // 解绑粘滞（终审 C2 + 行摄取）：解绑设备的后续 push 直接 400——设备行不得复活（否则 A13 对账
        // 永远清不掉它），行也不得回流 usage_rows（否则 opt-in 账号的榜单重算会把已级联清掉的设备数据
        // 重新吸回公开榜单，defeating 解绑触发的 RebuildAllDaysAsync）。重新入网只走 POST /api/devices。
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        (await devB.DeleteAsync("/api/devices/devA")).EnsureSuccessStatusCode();

        var rejected = await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-20", 100, 1.00m, T1) },
            new List<SessionFact>()));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, "未注册设备的 push 必须被拒收（解绑粘滞到行摄取）");

        var list = JsonSerializer.Deserialize(
            await (await RelayTestSync.SendGetAsync(devB, "/api/devices")).Content.ReadAsStreamAsync(),
            SyncJsonContext.Default.SyncDeviceListResponse)!;
        list.Devices.Select(device => device.DeviceId).Should().NotContain("devA", "解绑后 push 不得复活设备行");

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().BeEmpty("解绑级联已清行，被拒收 push 的行不得回流");
    }

    /// <summary>终审 C1：seq 账号级交错于两表，按表分页时续拉游标必须取"首个被跳过行"之前的水位，
    /// 否则窗口外的低 seq 行被永久跳过。limit=2 流量：u1(seq1) → s1(seq2) → u2(seq3)，首拉回 u1,u2。</summary>
    [Fact]
    public async Task Pull_PaginationInterleavedSeqs_DeliversAllRows()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact> { new() { Device = "devA", SessionId = "s-1", TotalTokens = 5, FirstSeenAt = T1, LastSeenAt = T1, UpdatedAt = T1 } }))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var first = await RelayTestSync.PullAsync(devB, "devB", since: 0, limit: 2);
        first.UsageFacts.Should().HaveCount(2);
        first.SessionFacts.Should().BeEmpty();
        first.HasMore.Should().BeTrue();
        first.Seq.Should().BeLessThanOrEqualTo(1, "续拉游标停在首个被跳过行（s1 seq=2）之前");

        var second = await RelayTestSync.PullAsync(devB, "devB", since: first.Seq, limit: 2);
        second.SessionFacts.Should().ContainSingle(row => row.SessionId == "s-1", "被跳过的会话行必须可达");
        // 已交付行被重复返回是允许的（客户端 LWW 幂等落库消化）；关键是 hasMore=false 收敛、全部行可达。
        second.HasMore.Should().BeFalse();
    }

    /// <summary>快照一致性（先读上界再读行）：拉尽游标 = 行读取**之前**读到的账号头部（上界），不是读取时的
    /// "当前头部"。以陈旧上界直接驱动 ReadPullBatchAsync——等价于上界读之后、行读之前有并发 push 落库：
    /// 窗口 (since, 上界] 外的行不得进本批、游标停在陈旧上界；以上界为 since 的续拉必须拾回它们
    /// （pre-fix 拉尽取"当前头部"会越过未交付行 → 后续 pull 永久跳行，静默单侧少数据）。</summary>
    [Fact]
    public async Task Pull_DrainedCursorNeverExceedsPreReadUpperBound()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-relay-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new RelayFactory(token: "abc", dbPath: dbPath);
            using var devA = RelayTestSync.Client(factory);
            using var devB = RelayTestSync.Client(factory);

            await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
            await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");
            // 两批 push 得 seq 1..3；"陈旧上界" = 2（模拟行读前只见到头部 2，随后 seq=3 的行才落库）。
            (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
                new List<UsageFact>
                {
                    RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                    RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T2),
                },
                new List<SessionFact>()))).EnsureSuccessStatusCode();
            (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
                new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-21", 300, 3.00m, T2) },
                new List<SessionFact>()))).EnsureSuccessStatusCode();

            var database = new RelayDatabase(dbPath);
            await using var connection = database.OpenConnection();
            using var accountCmd = connection.CreateCommand();
            accountCmd.CommandText = "SELECT id FROM accounts WHERE kind = 'shared'";
            var accountId = (string)(await accountCmd.ExecuteScalarAsync())!;

            var stale = await SyncEndpoints.ReadPullBatchAsync(
                connection, accountId, "devB", since: 0, seqUpperBound: 2, limit: SyncEndpoints.PullRowLimit, CancellationToken.None);
            stale.HasMore.Should().BeFalse();
            stale.UsageFacts.Should().HaveCount(2, "窗口 (0, 2] 内的行全量交付");
            stale.Seq.Should().Be(2, "拉尽游标 = 行读取前读到的上界，不得取当前头部 3");

            var next = await SyncEndpoints.ReadPullBatchAsync(
                connection, accountId, "devB", since: stale.Seq, seqUpperBound: 3, limit: SyncEndpoints.PullRowLimit, CancellationToken.None);
            next.HasMore.Should().BeFalse();
            next.Seq.Should().Be(3);
            next.UsageFacts.Should().ContainSingle().Which.Day.Should().Be("2026-06-21", "窗口外的行由续拉拾回，不得被跳过");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    // ---- 负向路径：畸形输入与缺凭据一律 4xx，不冒 500（DTO 允许缺省，服务端是唯一闸门）----

    [Fact]
    public async Task Push_MissingDeviceHeader_Returns400()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
        message.Content = new StringContent(
            JsonSerializer.Serialize(new SyncPushRequest(new List<UsageFact>(), new List<SessionFact>()), SyncJsonContext.Default.SyncPushRequest),
            Encoding.UTF8,
            "application/json");
        var response = await client.SendAsync(message);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "X-Tokenmetry-Device 是 caller 标识，缺省即拒");
    }

    [Fact]
    public async Task Push_RowFromOtherDevice_Returns400()
    {
        // 单写者不变量：devA 的连接推 devB 归属的行 → 400（校验在开事务前，无需 devB 注册）。
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        await RelayTestSync.RegisterAsync(client, "devA", "Desktop A");

        var rejected = await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devB", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, "归属错行会污染其他设备的单写者 LWW");
    }

    [Fact]
    public async Task Push_MalformedJson_Returns400()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
        message.Headers.Add("X-Tokenmetry-Device", "devA");
        message.Content = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(message);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Push_NullOrMissingLists_TreatedAsEmptyBatch()
    {
        // {} 与显式 null 列表按空批处理（幂等 no-op 200）——不得 NRE 500；未注册设备仍走 400。
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        await RelayTestSync.RegisterAsync(client, "devA", "Desktop A");

        foreach (var body in new[] { "{}", """{"usageFacts":null,"sessionFacts":null}""" })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
            message.Headers.Add("X-Tokenmetry-Device", "devA");
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(message);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"空批是幂等 no-op，不得 500：{body}");
        }
    }

    [Fact]
    public async Task Push_RowMissingUpdatedAt_Returns400_AndNothingLands()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");

        var row = RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1);
        row.UpdatedAt = null;
        var rejected = await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { row },
            new List<SessionFact>()));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, "缺 UpdatedAt 无法做 LWW，事务中途抛异常的 500 路径已被 400 前置拦截");

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().BeEmpty("被拒收批次不得部分落库");
    }

    [Fact]
    public async Task Push_RowWithMalformedDay_Returns400_AndNothingLands()
    {
        // day 是 usage_rows/rollup 主键成分，榜单月窗口按 yyyy-MM-dd 文本分桶——乱码 day 进库前拒收。
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        await RelayTestSync.RegisterAsync(devA, "devA", "Desktop A");
        await RelayTestSync.RegisterAsync(devB, "devB", "Laptop B");

        foreach (var day in new[] { "2026/06/19", "2026-6-9", "not-a-day", "" })
        {
            var rejected = await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
                new List<UsageFact> { RelayTestSync.UsageRow("devA", day, 100, 1.00m, T1) },
                new List<SessionFact>()));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"非法 day 必须被拒：{day}");
        }

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().BeEmpty("被拒收批次不得部分落库");
    }

    [Fact]
    public async Task Sync_Unauthenticated_Returns401()
    {
        // Bearer 中间件对 /api/sync/* 同样生效（骨架测试只覆盖了 /api/me）。
        await using var factory = new RelayFactory(token: "abc");
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/sync/pull?since=0")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/sync/push", new StringContent("{}", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pull_NonIntegerLimit_Returns400()
    {
        // 非数字 limit 与 since、榜单公开端点同口径 400，不静默落默认值（否则调参错误被吞）。
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        using var message = new HttpRequestMessage(HttpMethod.Get, "/api/sync/pull?since=0&limit=abc");
        message.Headers.Add("X-Tokenmetry-Device", "devA");
        var response = await client.SendAsync(message);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
