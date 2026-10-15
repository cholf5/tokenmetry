namespace Tokenmetry.Infrastructure.Tests.Query;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// 模型别名聚合（GetModelBreakdownAsync 经 IModelNameResolver 归一）的 fixture 测试：
/// 同一模型的多个原始名（大小写变体 / 厂商前缀 / 别名）在规范名口径合并为一行——token、成本
/// （未知成本契约保持 null ≠ 0）、会话数、关联 Agent 全部按合并后的口径成立，占比在合并后的
/// 全集上重算。对照组：无解析器（恒等）时三个原始名仍是三行（既有契约逐位不变）。
/// 另覆盖 GetSessionsAsync 的模型筛选（规范名空间比较）与 GetRawModelNamesAsync 的
/// 「绕过映射返回原始名」语义。时区固定 UTC，断言不受本机时区影响。
/// </summary>
public class ModelAliasAggregationTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteUsageStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ccusage-alias-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_dbPath);
        await _store.InitializeAsync();
        await SeedAsync();
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

    private UsageQueryService CreateService(IModelNameResolver? resolver = null) =>
        new(_store, TimeZoneInfo.Utc, resolver);

    [Fact]
    public async Task Model_breakdown_without_resolver_keeps_raw_names_separate()
    {
        var rows = await CreateService().GetModelBreakdownAsync(new UsageQueryFilter());

        rows.Select(row => row.Key).Should().BeEquivalentTo(
            ["GLM-5.3-Flash", "glm-5.3-flash", "ark/glm-5.3-flash", "claude-sonnet-4"]);
    }

    [Fact]
    public async Task Model_breakdown_merges_alias_variants_into_canonical_row()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("GLM-5.3-Flash", "glm-5.3-flash"),
            new("ark/glm-5.3-flash", "glm-5.3-flash"),
            new("glm-flash-latest", "glm-5.3-flash"),
        ]);
        var rows = await CreateService(resolver).GetModelBreakdownAsync(new UsageQueryFilter());

        // 四个原始名（含未列规则的纯大小写变体 GLM-5.3-Flash 之外还有 GLM-5.3-FLASH）
        // 全部并到 glm-5.3-flash；claude 不受影响。
        rows.Select(row => row.Key).Should().BeEquivalentTo(["glm-5.3-flash", "claude-sonnet-4"]);

        var merged = rows.Single(row => row.Key == "glm-5.3-flash");
        merged.InputTokens.Should().Be(1200 + 3400 + 100, "三个原始名的输入 token 合并");
        merged.OutputTokens.Should().Be(800 + 1600);
        merged.TotalTokens.Should().Be(2000 + 5000 + 100);
        merged.Cost.KnownCostUsd.Should().Be(1.10m + 2.75m, "已知成本相加；缺价行（ark/... 100 token）只贡献未知标记");
        merged.Cost.HasUnknownCost.Should().BeTrue("映射不改变未知成本纪律：缺价原始名的成本以未知并入");
        merged.SessionCount.Should().Be(3, "会话按 dominant_model 的归一名归属");
        merged.DominantAgent.Should().Be("ZCode", "合并后 ZCode（2 会话）多于 Claude Code（1 会话）");
        merged.AgentCount.Should().Be(2, "合并行的关联 Agent 取并集（ZCode + Claude Code）");

        // 占比在合并后的全集上重算：glm 7100 / (7100 + 500) = 0.9342...
        merged.TokenShare.Should().Be(Math.Round(7100m / 7600m, 4));
    }

    [Fact]
    public async Task Model_breakdown_merges_only_within_requested_scope()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases([new("ark/glm-5.3-flash", "glm-5.3-flash")]);

        var rows = await CreateService(resolver)
            .GetModelBreakdownAsync(new UsageQueryFilter { FromDay = "2026-04-11", ToDay = "2026-04-11" });

        rows.Select(row => row.Key).Should().BeEquivalentTo(["glm-5.3-flash"]);
        var merged = rows.Single(row => row.Key == "glm-5.3-flash");
        merged.TotalTokens.Should().Be(5000 + 100, "窗口内的 ark/ 行并入 glm-5.3-flash；窗口外的 GLM-5.3-Flash 不参与");
    }

    [Fact]
    public async Task Session_filter_compares_in_canonical_space()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("GLM-5.3-Flash", "glm-5.3-flash"),
            new("ark/glm-5.3-flash", "glm-5.3-flash"),
        ]);

        var result = await CreateService(resolver).GetSessionsAsync(new SessionListQuery
        {
            Model = "glm-5.3-flash",
        });

        result.TotalCount.Should().Be(3, "规范名筛选命中所有归一后等于它的会话");
        result.Rows.Select(row => row.DominantModel).Should().OnlyContain(model =>
            model == "GLM-5.3-Flash" || model == "glm-5.3-flash" || model == "ark/glm-5.3-flash");
    }

    [Fact]
    public async Task Raw_model_names_bypass_alias_resolution_and_rank_by_usage()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("GLM-5.3-Flash", "glm-5.3-flash"),
            new("ark/glm-5.3-flash", "glm-5.3-flash"),
        ]);

        var names = await CreateService(resolver).GetRawModelNamesAsync();

        // 候选清单是映射前的原始名（录入映射的源），按 facts + 会话 token 之和降序：
        // glm-5.3-flash 5000+5000、GLM-5.3-Flash 2000+2000、claude-sonnet-4 500+500、ark/... 100+100。
        names.Should().Equal("glm-5.3-flash", "GLM-5.3-Flash", "claude-sonnet-4", "ark/glm-5.3-flash");
    }

    private async Task SeedAsync()
    {
        var facts = new List<UsageFact>
        {
            Full("2026-04-10", "ZCode", "GLM-5.3-Flash", "/work/one", "one", input: 1200, output: 800, total: 2000, cost: 1.10m),
            Full("2026-04-11", "ZCode", "glm-5.3-flash", "/work/one", "one", input: 3400, output: 1600, total: 5000, cost: 2.75m),
            Full("2026-04-11", "Claude Code", "ark/glm-5.3-flash", "/work/two", "two", input: 100, total: 100, cost: null),
            Full("2026-04-12", "Claude Code", "claude-sonnet-4", "/work/two", "two", input: 400, output: 100, total: 500, cost: 0.25m),
        };

        var sessions = new List<SessionFact>
        {
            Session("a0", "ZCode", "/work/one", "one", "GLM-5.3-Flash", new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero), 2000, 1.10m),
            Session("a1", "ZCode", "/work/one", "one", "glm-5.3-flash", new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero), 5000, 2.75m),
            Session("a2", "Claude Code", "/work/two", "two", "ark/glm-5.3-flash", new DateTimeOffset(2026, 4, 11, 15, 0, 0, TimeSpan.Zero), 100, null),
            Session("a3", "Claude Code", "/work/two", "two", "claude-sonnet-4", new DateTimeOffset(2026, 4, 12, 11, 0, 0, TimeSpan.Zero), 500, 0.25m),
        };

        await _store.UpsertUsageFactsAsync(facts);
        await _store.UpsertSessionFactsAsync(sessions);
    }

    private static UsageFact Full(
        string day,
        string agent,
        string model,
        string project,
        string displayName,
        long input = 0,
        long output = 0,
        long total = 0,
        decimal? cost = null) => new()
    {
        Day = day,
        Grain = UsageFactGrain.Full,
        Agent = agent,
        Model = model,
        Project = project,
        ProjectDisplayName = displayName,
        InputTokens = input,
        OutputTokens = output,
        TotalTokens = total,
        CostUsd = cost,
    };

    private static SessionFact Session(
        string id,
        string agent,
        string project,
        string displayName,
        string model,
        DateTimeOffset firstSeenAt,
        long totalTokens,
        decimal? cost) => new()
    {
        SessionId = id,
        Agent = agent,
        Project = project,
        ProjectDisplayName = displayName,
        DominantModel = model,
        FirstSeenAt = firstSeenAt,
        LastSeenAt = firstSeenAt.AddMinutes(30),
        TotalTokens = totalTokens,
        CostUsd = cost,
    };
}
