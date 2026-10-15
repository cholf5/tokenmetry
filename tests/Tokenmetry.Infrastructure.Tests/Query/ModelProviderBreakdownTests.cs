namespace Tokenmetry.Infrastructure.Tests.Query;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// 模型维度的 provider 汇总（I-036 三元组的第三元，查询侧契约）：
/// facts 行自带的 provider 在模型维度去重——唯一值即该行的 provider，多个 provider 共用同一模型时
/// 留空 + 计数（展示层出「N 个」），没有任何观测时计数为 0（界面"—"）。
/// facts 已按 (agent × model × provider) 分行出账，故这里断言的是"汇总口径"而不是拆分口径
/// （模型行按 provider 拆分的查询形态是与 session_facts 加列绑定的后续改动，见卡片遗留）。
/// </summary>
public class ModelProviderBreakdownTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteUsageStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ccusage-provider-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_dbPath);
        await _store.InitializeAsync();

        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            Fact("2026-04-10", "ZCode", "glm-4.6", "zai", input: 100),
            Fact("2026-04-10", "ZCode", "glm-4.6", "openrouter", input: 200),
            Fact("2026-04-11", "Claude Code", "claude-sonnet-4", "anthropic", input: 300),
            Fact("2026-04-11", "Claude Code", "bare-model", string.Empty, input: 400),
        });
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_dbPath))
        {
            SqliteConnection.ClearAllPools();
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(_dbPath);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    [Fact]
    public async Task Model_breakdown_summarizes_provider_per_model()
    {
        var service = new UsageQueryService(_store, TimeZoneInfo.Utc);

        var rows = await service.GetModelBreakdownAsync(new UsageQueryFilter());

        var single = rows.Single(row => row.Key == "claude-sonnet-4");
        single.Provider.Should().Be("anthropic");
        single.ProviderCount.Should().Be(1);

        var multi = rows.Single(row => row.Key == "glm-4.6");
        multi.Provider.Should().BeEmpty("两个 provider 共用一个模型时不给单一值，交给展示层出「N 个」");
        multi.ProviderCount.Should().Be(2);
        multi.InputTokens.Should().Be(300, "provider 只影响归属标注，不影响该模型的用量合计");

        var none = rows.Single(row => row.Key == "bare-model");
        none.ProviderCount.Should().Be(0, "没有 provider 观测 → 计数 0（界面显示「—」），不猜");
    }

    [Fact]
    public async Task Other_dimensions_never_report_provider()
    {
        var service = new UsageQueryService(_store, TimeZoneInfo.Utc);

        var agents = await service.GetAgentBreakdownAsync(new UsageQueryFilter());
        var projects = await service.GetProjectBreakdownAsync(new UsageQueryFilter());

        agents.Should().OnlyContain(row => row.ProviderCount == 0 && row.Provider == string.Empty);
        projects.Should().OnlyContain(row => row.ProviderCount == 0 && row.Provider == string.Empty);
    }

    private static UsageFact Fact(string day, string agent, string model, string provider, long input) => new()
    {
        Day = day,
        Grain = UsageFactGrain.Full,
        Agent = agent,
        Model = model,
        Provider = provider,
        Project = "/work/one",
        ProjectDisplayName = "one",
        InputTokens = input,
        TotalTokens = input,
        CostUsd = 1m,
    };
}
