namespace CCUsage.Infrastructure.Tests.LocalIngestion;

using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.DshSession;
using CCUsage.Infrastructure.LocalIngestion;
using CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// I-005: 本地采集数据源管道测试（临时目录 fixtures + 内存 store）：
/// 补价与 LogCost 短路 → facts 写入 → 数据源切换清理 → 读回 roundtrip → 重复刷新幂等。
/// 时间分桶统一用 UTC，测试不依赖本机时区。
/// </summary>
public class LocalIngestionDataSourceTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task GetDailyUsage_PricesMissingCostsWritesFactsAndReturnsDailySummaries()
    {
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20),
            ClaudeLine("msg-2", "claude-test", costUsd: "null", inputTokens: 10, outputTokens: 20));
        WriteCodexSession(codexHome, "sessions/rollout-x.jsonl",
            CodexMetaLine("/Users/dev/codex-proj"),
            CodexTokenCountLine("2026-05-25T16:00:18.744Z", inputTokens: 100, cached: 0, outputTokens: 50));

        var source = CreateSource(store, claudeRoot, codexHome);

        var rows = await source.GetDailyUsageAsync();

        // 读回路径：页面将看到的每日汇总直接来自 facts 存储。
        rows.Should().HaveCount(1);
        var day = rows.Single();
        day.Date.Should().Be("2026-05-25");
        day.TotalCostUsd.Should().Be(0.05m + 0.0002m + 0.00005m); // LogCost + gpt-5 补价 + claude-test 补价
        day.TotalTokens.Should().Be(30 + 30 + 150);

        day.Agents.Select(agent => agent.Agent).Should().BeEquivalentTo("Claude Code", "Codex");
        day.Models.Select(model => model.Model).Should().BeEquivalentTo("claude-test", "gpt-5");
        day.Projects.Select(project => project.DisplayName).Should().BeEquivalentTo("project-a", "codex-proj");

        // facts 只写 full 粒度（旧 CLI 分解行已清理，见 DeleteLegacy 测试）。
        store.Facts.Should().OnlyContain(fact => fact.Grain == UsageFactGrain.Full);
        store.Facts.Should().HaveCount(2); // claude（session-a × claude-test）+ codex（gpt-5）各聚合一行

        // session facts 也写入。
        store.SessionFacts.Should().HaveCount(2);
        var claudeSession = store.SessionFacts.Single(fact => fact.SessionId == "session-a");
        claudeSession.ProjectDisplayName.Should().Be("project-a");
        claudeSession.CostUsd.Should().Be(0.05m + 0.00005m);
        var codexSession = store.SessionFacts.Single(fact => fact.SessionId == "rollout-x");
        codexSession.ProjectDisplayName.Should().Be("codex-proj");
        codexSession.DominantModel.Should().Be("gpt-5");
    }

    [Fact]
    public async Task GetDailyUsage_PreservesLogCost_AndPricesMissingRecords()
    {
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20),
            ClaudeLine("msg-2", "claude-test", costUsd: "null", inputTokens: 10, outputTokens: 20));

        var source = CreateSource(store, claudeRoot, codexHome);

        await source.GetDailyUsageAsync();

        var claudeFacts = store.Facts.Where(fact => fact.Agent == "Claude Code").ToList();
        claudeFacts.Should().HaveCount(1); // 同 day × agent × model × project 聚合为一行
        var fact = claudeFacts.Single();
        fact.CostUsd.Should().Be(0.05m + 0.00005m); // 0.05 来自日志（LogCost 短路），0.00005 来自补价
    }

    [Fact]
    public async Task GetDailyUsage_IsIdempotentOnRepeatedRefresh()
    {
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));
        WriteCodexSession(codexHome, "sessions/rollout-x.jsonl",
            CodexTokenCountLine("2026-05-25T16:00:18.744Z", inputTokens: 100, cached: 0, outputTokens: 50));

        var source = CreateSource(store, claudeRoot, codexHome);

        var first = await source.GetDailyUsageAsync();
        var second = await source.GetDailyUsageAsync();

        second.Should().HaveCount(first.Count);
        second.Single().TotalCostUsd.Should().Be(first.Single().TotalCostUsd);
        second.Single().TotalTokens.Should().Be(first.Single().TotalTokens);
        store.Facts.Should().HaveCount(2); // 不因重复刷新翻倍
        store.SessionFacts.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDailyUsage_DeletesLegacyCliDecompositionRows_AndKeepsFullRows()
    {
        var store = new InMemoryUsageStore();
        // 旧 CLI 源残留：分解粒度行（混源日 2026-05-25 与更早的 2026-05-24）+ 一条旧 full 行。
        store.Seed(new UsageFact { Day = "2026-05-25", Grain = UsageFactGrain.Day, TotalTokens = 999, CostUsd = 9.99m });
        store.Seed(new UsageFact { Day = "2026-05-25", Grain = UsageFactGrain.Agent, Agent = "Claude Code", TotalTokens = 999 });
        store.Seed(new UsageFact { Day = "2026-05-24", Grain = UsageFactGrain.Day, TotalTokens = 888, CostUsd = 8.88m });
        store.Seed(new UsageFact
        {
            Day = "2026-05-20",
            Grain = UsageFactGrain.Full,
            Agent = "Claude Code",
            Model = "old-model",
            Project = "old-project",
            TotalTokens = 100,
            CostUsd = 1m
        });

        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));

        var source = CreateSource(store, claudeRoot, codexHome);

        await source.GetDailyUsageAsync();

        // 分解行全清，full 行（旧的与本次写入的）保留。
        store.Facts.Should().OnlyContain(fact => fact.Grain == UsageFactGrain.Full);
        store.Facts.Should().Contain(fact =>
            fact.Day == "2026-05-20" && fact.Model == "old-model" && fact.TotalTokens == 100);
        store.Facts.Should().Contain(fact => fact.Day == "2026-05-25" && fact.Model == "claude-test");
        store.Facts.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDailyUsage_WithNoRecords_LeavesStoreUntouched()
    {
        var store = new InMemoryUsageStore();
        store.Seed(new UsageFact { Day = "2026-05-24", Grain = UsageFactGrain.Day, TotalTokens = 888, CostUsd = 8.88m });
        var (claudeRoot, codexHome) = CreateLogDirs(); // 空目录：无 Claude 日志、无 Codex 日志

        var source = CreateSource(store, claudeRoot, codexHome);

        var rows = await source.GetDailyUsageAsync();

        rows.Should().BeEmpty();
        // 空扫描不清数据：仅存的历史（含旧 CLI 行）保留，等有真实数据再切换。
        store.Facts.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetDailyUsage_SerializesConcurrentCalls()
    {
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));

        var source = CreateSource(store, claudeRoot, codexHome);

        var results = await Task.WhenAll(
            source.GetDailyUsageAsync(),
            source.GetDailyUsageAsync());

        results.Should().OnlyContain(rows => rows.Count == 1);
        store.Facts.Should().HaveCount(1); // 并发进入被串行化，不产生重复或损坏
    }

    [Fact]
    public void PersistsFacts_IsTrue()
    {
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();

        var source = CreateSource(store, claudeRoot, codexHome);

        ((IUsageDataSource)source).PersistsFacts.Should().BeTrue();
    }

    [Fact]
    public async Task GetDailyUsage_RepricesRecordsWhenOverridesChange()
    {
        // 回归（审查发现）：适配器增量扫描跨刷新复用同一批记录实例，
        // 补价结果不得写回记录——否则二次刷新被 LogCost 短路锁死旧价。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "null", inputTokens: 1_000_000, outputTokens: 1_000_000));

        var pricingEngine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = "claude-test", InputPerMillion = 1m, OutputPerMillion = 2m },
        });
        var source = CreateSource(store, claudeRoot, codexHome, pricingEngine);

        var first = await source.GetDailyUsageAsync();
        first.Single().TotalCostUsd.Should().Be(3m); // 1M × (1 + 2) / 1M

        pricingEngine.UpdateOverrides(new[]
        {
            new ModelPricingOverride { Model = "claude-test", InputPerMillion = 2m, OutputPerMillion = 4m },
        });
        var second = await source.GetDailyUsageAsync();
        second.Single().TotalCostUsd.Should().Be(6m); // 新覆盖价生效，不被锁死

        store.Facts.Single(fact => fact.Model == "claude-test").CostUsd.Should().Be(6m);
    }

    // ---- helpers ----

    private LocalIngestionDataSource CreateSource(InMemoryUsageStore store, string claudeRoot, string codexHome, PricingEngine? pricingEngine = null)
    {
        pricingEngine ??= new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = "claude-test", InputPerMillion = 1m, OutputPerMillion = 2m },
            new ModelPricingOverride { Model = "gpt-5", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        return new LocalIngestionDataSource(
            new ClaudeCodeJsonlAdapter(configuredRoot: claudeRoot),
            new CodexJsonlAdapter(configuredHome: codexHome),
            new ZCodeSqliteAdapter(configuredHome: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "zcode-missing")),
            new DshSessionAdapter(configuredHome: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "dsh-missing")),
            pricingEngine,
            store,
            pricingClient: null,
            timeZone: Utc);
    }

    private (string ClaudeRoot, string CodexHome) CreateLogDirs()
    {
        var root = CreateTempDir();
        var claudeRoot = Path.Combine(root, "claude");
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(codexHome);
        return (claudeRoot, codexHome);
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-ingestion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteClaudeSession(string claudeRoot, string relativePath, params string[] lines)
    {
        var fullPath = Path.Combine(claudeRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
    }

    private static void WriteCodexSession(string codexHome, string relativePath, params string[] lines)
    {
        var fullPath = Path.Combine(codexHome, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
    }

    private static string ClaudeLine(string messageId, string model, string costUsd, long inputTokens, long outputTokens)
    {
        return $"{{\"sessionId\":\"session-a\",\"timestamp\":\"2026-05-25T10:00:00.000Z\",\"message\":{{\"id\":\"{messageId}\",\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{inputTokens},\"output_tokens\":{outputTokens},\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}},\"costUSD\":{costUsd},\"requestId\":\"req-{messageId}\"}}";
    }

    private static string CodexMetaLine(string cwd)
    {
        return $"{{\"timestamp\":\"2026-05-25T15:58:18.943Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"meta\",\"cwd\":\"{cwd.Replace("\\", "\\\\")}\"}}}}";
    }

    private static string CodexTokenCountLine(string timestamp, long inputTokens, long cached, long outputTokens)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"input_tokens\":{inputTokens},\"cached_input_tokens\":{cached},\"output_tokens\":{outputTokens},\"reasoning_output_tokens\":0,\"total_tokens\":{inputTokens + outputTokens}}}}}}}}}";
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>内存 facts 存储：与 SqliteUsageStore 相同的 upsert 幂等 / 整日替换 / 分解行清理语义。</summary>
    private sealed class InMemoryUsageStore : IUsageStore
    {
        private readonly Dictionary<(string Day, string Grain, string Agent, string Model, string Project), UsageFact> _facts = new();
        private readonly Dictionary<string, SessionFact> _sessionFacts = new();

        public IReadOnlyCollection<UsageFact> Facts => _facts.Values;

        public IReadOnlyCollection<SessionFact> SessionFacts => _sessionFacts.Values;

        public void Seed(UsageFact fact) => _facts[Key(fact)] = fact;

        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            foreach (var fact in facts)
            {
                _facts[Key(fact)] = fact;
            }

            return Task.CompletedTask;
        }

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            var days = facts.Select(fact => fact.Day).Distinct().ToHashSet();
            foreach (var key in _facts.Keys.Where(key => days.Contains(key.Day) && key.Grain != UsageFactGrain.Full).ToList())
            {
                _facts.Remove(key);
            }

            return UpsertUsageFactsAsync(facts, ct);
        }

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<UsageFact> facts = _facts.Values.ToList();
            return Task.FromResult(facts);
        }

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
        {
            foreach (var fact in facts)
            {
                _sessionFacts[fact.SessionId] = fact;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<SessionFact> facts = _sessionFacts.Values.ToList();
            return Task.FromResult(facts);
        }

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default)
        {
            foreach (var key in _facts.Keys.Where(key => key.Grain != UsageFactGrain.Full).ToList())
            {
                _facts.Remove(key);
            }

            return Task.CompletedTask;
        }

        private static (string, string, string, string, string) Key(UsageFact fact)
            => (fact.Day, fact.Grain, fact.Agent, fact.Model, fact.Project);
    }
}
