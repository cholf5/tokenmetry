namespace Tokenmetry.Infrastructure.Tests.Query;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// 宿主维度查询面测试：facts 按 host 分行后，Agent 维度给出宿主拆分；Host 过滤（含空串 = 直接用 CLI）
/// 在 facts 与会话两侧都精确生效；宿主候选清单按用量权重返回。时区固定 UTC。
/// </summary>
public class HostDimensionQueryTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteUsageStore _store = null!;
    private UsageQueryService _service = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-host-query-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_dbPath);
        await _store.InitializeAsync();
        _service = new UsageQueryService(_store, TimeZoneInfo.Utc);

        await _store.UpsertUsageFactsAsync(
        [
            Fact(host: "Cindy", tokens: 100),
            Fact(host: string.Empty, tokens: 300),
            Fact(host: "Orca Agent", tokens: 50),
        ]);
        await _store.UpsertSessionFactsAsync(
        [
            Session(sessionId: "s-cindy", host: "Cindy", tokens: 100),
            Session(sessionId: "s-cli", host: string.Empty, tokens: 300),
        ]);
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }

                break;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task Agent_breakdown_reports_host_split()
    {
        var rows = await _service.GetAgentBreakdownAsync(new UsageQueryFilter());

        var agent = rows.Should().ContainSingle().Subject;
        agent.TotalTokens.Should().Be(450);
        agent.Hosts.Should().Equal(
            new HostUsageSplit(string.Empty, 300),
            new HostUsageSplit("Cindy", 100),
            new HostUsageSplit("Orca Agent", 50));
    }

    [Fact]
    public async Task Host_filter_is_exact_and_distinguishes_empty_from_all()
    {
        var all = await _service.GetAgentBreakdownAsync(new UsageQueryFilter());
        all.Single().TotalTokens.Should().Be(450, "null = 不过滤");

        var cindy = await _service.GetAgentBreakdownAsync(new UsageQueryFilter { Host = "Cindy" });
        cindy.Single().TotalTokens.Should().Be(100);

        var cli = await _service.GetAgentBreakdownAsync(new UsageQueryFilter { Host = string.Empty });
        cli.Single().TotalTokens.Should().Be(300, "空串是合法过滤值：未识别 / 直接用 CLI");
    }

    [Fact]
    public async Task Session_list_carries_host_and_filters_by_it()
    {
        var cindySessions = await _service.GetSessionsAsync(new SessionListQuery
        {
            Filter = new UsageQueryFilter { Host = "Cindy" },
        });

        var row = cindySessions.Rows.Should().ContainSingle().Subject;
        row.SessionId.Should().Be("s-cindy");
        row.Host.Should().Be("Cindy");

        var cliSessions = await _service.GetSessionsAsync(new SessionListQuery
        {
            Filter = new UsageQueryFilter { Host = string.Empty },
        });
        cliSessions.Rows.Should().ContainSingle().Which.SessionId.Should().Be("s-cli");
    }

    [Fact]
    public async Task Host_options_are_ranked_by_usage_weight()
    {
        var hosts = await _service.GetHostsAsync();

        hosts.Should().Equal(string.Empty, "Cindy", "Orca Agent");
    }

    private static UsageFact Fact(string host, long tokens) => new()
    {
        Day = "2026-11-20",
        Grain = UsageFactGrain.Full,
        Agent = "Claude Code",
        Model = "claude-sonnet-4",
        Host = host,
        Project = "C:/workspace/demo",
        InputTokens = tokens,
        TotalTokens = tokens,
        CostUsd = 0m,
    };

    private static SessionFact Session(string sessionId, string host, long tokens) => new()
    {
        SessionId = sessionId,
        Agent = "Claude Code",
        Host = host,
        Project = "C:/workspace/demo",
        DominantModel = "claude-sonnet-4",
        FirstSeenAt = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero),
        LastSeenAt = new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.Zero),
        InputTokens = tokens,
        TotalTokens = tokens,
        CostUsd = 0m,
    };
}
