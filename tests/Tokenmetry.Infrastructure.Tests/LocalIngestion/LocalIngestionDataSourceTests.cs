namespace Tokenmetry.Infrastructure.Tests.LocalIngestion;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Cindy;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.LocalIngestion;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;

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
    public async Task GetDailyUsage_AllUnknownCostAgent_SurfacesNullFactsAndUnpricedModels()
    {
        // A-0b 回归锁（跨源）：某 agent 的记录全部缺价（DSH 无 request/header 路由 → unknown/unknown）
        // 且 token > 0 时，采集侧不得把未知折成"已知 0"——facts 行成本必须为 null（未知），
        // 权威未知清单（PricingEngine.GetUnpricedModels）必须登记该模型。修复前 DSH 不可计价记 0m，
        // 被 CostUsd is null 侧表门槛短路，既不补价也进不了未知清单，总览永远显示 $0.00。
        var store = new InMemoryUsageStore();
        var pricingEngine = new PricingEngine(overrides: []);
        var (claudeRoot, codexHome) = CreateLogDirs();
        var dshHome = Path.Combine(Path.GetDirectoryName(claudeRoot)!, "dsh");
        Directory.CreateDirectory(Path.Combine(dshHome, "sessions", "--tmp-proj--", "session-dsh-unknown"));
        File.WriteAllText(
            Path.Combine(dshHome, "sessions", "--tmp-proj--", "session-dsh-unknown", "session.jsonl"),
            string.Join("\n",
                "{\"type\":\"session\",\"version\":4,\"id\":\"session-dsh-unknown\",\"createdAt\":1787536700000,\"cwd\":\"/tmp/proj\"}",
                // 无 request/header 重归属：unknown/unknown 路由（DSH 目录不可计价 → null）。
                "{\"type\":\"assistant/chunk\",\"seq\":1,\"time\":1787536800000,\"data\":{\"turn\":1,\"step\":1," +
                "\"chunk\":{\"type\":\"usage\",\"usage\":{\"inputTokens\":40,\"outputTokens\":40,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}}}}") + "\n");

        var source = CreateSource(store, claudeRoot, codexHome, pricingEngine, dshHome: dshHome);

        var rows = await source.GetDailyUsageAsync();

        var day = rows.Single(row => row.Date == "2026-08-24");
        day.TotalTokens.Should().BeGreaterThan(0);

        var dshFacts = store.Facts.Where(fact => fact.Agent == "DSH").ToList();
        dshFacts.Should().NotBeNull();
        dshFacts.Should().OnlyContain(fact => fact.CostUsd == null);
        pricingEngine.GetUnpricedModels().Should().Contain(info => info.Model == "unknown/unknown");
    }

    private sealed class ThrowingZCodeAdapter : IZCodeSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom（注入的故障源）");
    }

    [Fact]
    public async Task GetDailyUsage_ThrowingSourceIsIsolatedAndReportedAsError()
    {
        // A-6 回归锁：任一源扫描抛异常只跳过该源（注册表如实登记 Error），其余源正常出数；
        // 修复前无隔离——一源抛出即整轮 19 源失败。
        var store = new InMemoryUsageStore();
        var registry = new IngestionScanRegistry();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));

        var source = CreateSource(store, claudeRoot, codexHome, registry: registry,
            zcodeAdapter: new ThrowingZCodeAdapter());

        var rows = await source.GetDailyUsageAsync();

        rows.Should().ContainSingle("其余源不受故障源影响，正常出数");
        rows[0].TotalCostUsd.Should().BeGreaterThan(0);
        rows[0].TotalTokens.Should().BeGreaterThan(0);

        registry.GetLastScan("ZCode").Should().NotBeNull();
        registry.GetLastScan("ZCode")!.Status.Should().Be(JsonlScanStatus.Error, "故障源如实登记失败态（设置页红色行）");
        registry.GetLastScan("ZCode")!.RecordCount.Should().Be(0);
        registry.GetLastScan("Claude Code")!.Status.Should().Be(JsonlScanStatus.Ok);
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
        // I-037 契约（I-042 语义迁移）：适配器增量扫描跨刷新复用同一批记录实例，补价结果不得
        // 写回记录；改覆盖价后必须经 IAggregationInvalidator 显式失效才在下一轮生效——无变化轮
        // 已被短路跳过（见 UnchangedSecondRound_SkipsRepricingUntilInvalidated）。
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
        source.InvalidateAggregation();
        var second = await source.GetDailyUsageAsync();
        second.Single().TotalCostUsd.Should().Be(6m); // 新覆盖价生效，不被锁死

        store.Facts.Single(fact => fact.Model == "claude-test").CostUsd.Should().Be(6m);
    }

    [Fact]
    public async Task GetDailyUsage_UnchangedSecondRound_SkipsRepricingUntilInvalidated()
    {
        // I-042 回归锁：无变化轮短路跳过计价/聚合/落库——改覆盖价后不失效 → facts 维持旧价
        // （旧实现"每轮全量重算"会把新价无差别抹上）；显式失效（生产接线 = 设置页改价路径）
        // 后一轮全量重算，新价生效。
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
        first.Single().TotalCostUsd.Should().Be(3m);

        pricingEngine.UpdateOverrides(new[]
        {
            new ModelPricingOverride { Model = "claude-test", InputPerMillion = 2m, OutputPerMillion = 4m },
        });
        var second = await source.GetDailyUsageAsync();
        second.Single().TotalCostUsd.Should().Be(3m, "无变化轮短路：计价不重跑，facts 维持旧价");
        store.Facts.Single(fact => fact.Model == "claude-test").CostUsd.Should().Be(3m);

        source.InvalidateAggregation();
        var third = await source.GetDailyUsageAsync();
        third.Single().TotalCostUsd.Should().Be(6m, "失效后强制全量：I-037 改价格即生效");
        store.Facts.Single(fact => fact.Model == "claude-test").CostUsd.Should().Be(6m);
    }

    [Fact]
    public async Task GetDailyUsage_UnchangedSecondRound_RebuildsFactsWhenStoreCleared()
    {
        // 自愈路径：无变化轮读回 facts 为空（库被外部清空 / 迁移异常）时落穿全量管道重建，
        // 不让"短路 + 空库"卡死成永久无数据。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));

        var source = CreateSource(store, claudeRoot, codexHome);
        var first = await source.GetDailyUsageAsync();
        first.Should().ContainSingle();
        store.Facts.Should().NotBeEmpty();

        store.ClearFacts();
        var second = await source.GetDailyUsageAsync();

        second.Should().ContainSingle("facts 为空 → 落穿全量管道重建");
        store.Facts.Should().Contain(fact => fact.Model == "claude-test");
    }

    [Fact]
    public async Task GetDailyUsage_UnchangedSecondRound_StillRefreshesRegistrySnapshots()
    {
        // 短路轮逐源登记照常：设置页数据源表的"最近扫描"时间戳与累计计数继续跳动
        // （累计数来自扫描器缓存的真实记录集，不是 0）。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));

        var registry = new IngestionScanRegistry();
        var source = CreateSource(store, claudeRoot, codexHome, registry: registry);
        await source.GetDailyUsageAsync();
        var firstAt = registry.GetLastScan(ClaudeCodeJsonlLineParser.AgentName)!.ScannedAt;

        await Task.Delay(30);
        await source.GetDailyUsageAsync();

        var second = registry.GetLastScan(ClaudeCodeJsonlLineParser.AgentName)!;
        second.ScannedAt.Should().BeOnOrAfter(firstAt.AddMilliseconds(25), "短路轮照常刷新登记时间戳");
        second.RecordCount.Should().Be(1, "累计记录数来自缓存去重产物");
        second.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task GetDailyUsage_ReportsPerSourceScanSnapshotsToRegistry()
    {
        // S-002：管道逐源扫描后向注册表登记真实快照（状态 / 累计记录数 / 完成时刻），
        // 注册清单 = 管道组合（19 个适配器），设置页数据源表的数据面。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        WriteClaudeSession(claudeRoot, "projects/project-a/session-a.jsonl",
            ClaudeLine("msg-1", "claude-test", costUsd: "0.05", inputTokens: 10, outputTokens: 20));
        // codexHome 无会话文件 → NoData；其余适配器指向不存在目录 → NoData

        var registry = new IngestionScanRegistry();
        var source = CreateSource(store, claudeRoot, codexHome, registry: registry);
        await source.GetDailyUsageAsync();

        registry.RegisteredSources.Should().HaveCount(20);

        var claude = registry.GetLastScan(ClaudeCodeJsonlLineParser.AgentName);
        claude.Should().NotBeNull();
        claude!.Status.Should().Be(JsonlScanStatus.Ok);
        claude.RecordCount.Should().Be(1);
        claude.ScannedAt.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddMinutes(-1));

        var codex = registry.GetLastScan(CodexJsonlFileParser.AgentName);
        codex.Should().NotBeNull();
        codex!.Status.Should().Be(JsonlScanStatus.NoData);
        codex.RecordCount.Should().Be(0);

        // 管道尾源与其余全部登记（键 = 各适配器声明的源名；pi-agent 无单一出账名，取适配器名）
        registry.GetLastScan("pi-agent").Should().NotBeNull();
        registry.GetLastScan("Antigravity").Should().NotBeNull();
        registry.GetLastScan("Copilot CLI").Should().NotBeNull();
        registry.RegisteredSources.Should().Contain(new[]
        {
            "Claude Code", "Codex", "ZCode", "DSH", "Copilot CLI", "OpenCode", "Goose", "Hermes",
            "Kilo", "Gemini CLI", "Amp", "Droid", "Qwen Code", "Kimi", "Codebuff", "pi-agent",
            "Grok", "OpenClaw", "Antigravity",
        });
    }

    [Fact]
    public async Task GetDailyUsage_WithRegistryNull_DoesNotThrow()
    {
        // 既有构造点（未传注册表）保持可用：报告为可选副作用
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        var source = CreateSource(store, claudeRoot, codexHome);

        var act = () => source.GetDailyUsageAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetDailyUsage_RunsPipeline_OffTheCallerSynchronizationContext()
    {
        // 首启冻结回归锁（线程防护见 GetDailyUsageAsync 入口注释）：调用线程挂着同步上下文
        // （真实应用里是 UI 线程的 AvaloniaSynchronizationContext）发起采集时，管道体必须先经
        // Task.Run 移出调用线程——否则 19 源扫描从同步前缀起就内联在调用线程上，续体逐段排回
        // 调用方上下文，首启全量扫描把 UI 线程冻到扫描结束。观测点用 Claude 桩（管道第一个源，
        // 其入口必然落在同步前缀内），记录实际执行线程与当时的同步上下文。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();

        var callerThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource<(int ThreadId, SynchronizationContext? Context)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = CreateSource(
            store, claudeRoot, codexHome,
            claudeAdapter: new RecordingClaudeAdapter(started));

        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        Task<List<DailyUsageSummary>> task;
        try
        {
            task = source.GetDailyUsageAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        var observed = await started.Task;
        await task;

        observed.ThreadId.Should().NotBe(callerThread, "管道体必须经 Task.Run 移出调用线程执行");
        observed.Context.Should().BeNull("线程池上没有同步上下文；观察到调用线程的上下文即管道内联在调用方执行");
    }

    [Fact]
    public async Task Cindy_sessions_are_not_double_counted_when_pi_source_sees_same_files()
    {
        // 用户把 PI_AGENT_DIR 指到 <Cindy>/pi-agent-home/sessions（本轮改动之前的建议做法）时，pi 适配器
        // 与 Cindy 适配器会读到同一批文件。facts 主键含 agent：不去重就是两行、总额翻倍，而会话表按
        // session_id 主键反而看不出来——所以归属仲裁必须在拼接处做。
        var store = new InMemoryUsageStore();
        var (claudeRoot, codexHome) = CreateLogDirs();
        var userData = CreateTempDir();
        var sessions = Path.Combine(userData, "pi-agent-home", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(
            Path.Combine(sessions, "2026-01-02T10-00-00-000Z_session-a.jsonl"),
            string.Join("\n",
                """{"type":"session","id":"session-a","timestamp":"2026-01-02T10:00:00.000Z","cwd":"E:/dev/demo"}""",
                """{"type":"message","timestamp":"2026-01-02T10:00:05.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":100,"output":10,"cacheRead":20,"cacheWrite":3,"totalTokens":133}}}""") + "\n");

        var source = CreateSource(store, claudeRoot, codexHome, piDir: sessions, cindyUserDataRoot: userData);

        var daily = await source.GetDailyUsageAsync();

        var day = daily.Should().ContainSingle().Subject;
        day.TotalTokens.Should().Be(133, "同一份会话文件只出一份账");
        day.Agents.Should().ContainSingle().Which.Agent.Should().Be("Cindy", "文件归属 Cindy，pi 源的重复记录被丢弃");
    }

    // ---- helpers ----

    private LocalIngestionDataSource CreateSource(InMemoryUsageStore store, string claudeRoot, string codexHome, PricingEngine? pricingEngine = null, IngestionScanRegistry? registry = null, string? dshHome = null, IZCodeSqliteAdapter? zcodeAdapter = null, IClaudeCodeJsonlAdapter? claudeAdapter = null, string? piDir = null, string? cindyUserDataRoot = null)
    {
        pricingEngine ??= new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = "claude-test", InputPerMillion = 1m, OutputPerMillion = 2m },
            new ModelPricingOverride { Model = "gpt-5", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        return new LocalIngestionDataSource(
            claudeAdapter ?? new ClaudeCodeJsonlAdapter(configuredRoot: claudeRoot),
            new CodexJsonlAdapter(configuredHome: codexHome),
            zcodeAdapter ?? new ZCodeSqliteAdapter(configuredHome: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "zcode-missing")),
            new DshSessionAdapter(configuredHome: dshHome ?? Path.Combine(Path.GetDirectoryName(claudeRoot)!, "dsh-missing")),
            new CopilotSessionAdapter(configuredHome: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "copilot-missing")),
            new OpenCodeSqliteAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "opencode-missing")),
            new GooseSqliteAdapter(configuredRoot: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "goose-missing")),
            new HermesSqliteAdapter(configuredHome: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "hermes-missing")),
            new KiloSqliteAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "kilo-missing")),
            new GeminiCliAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "gemini-missing")),
            new AmpAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "amp-missing")),
            new DroidAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "droid-missing")),
            new QwenCodeAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "qwen-missing")),
            new KimiCodeAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "kimi-missing")),
            new CodebuffAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "codebuff-missing")),
            new PiAgentAdapter(configuredDataDir: piDir ?? Path.Combine(Path.GetDirectoryName(claudeRoot)!, "pi-missing")),
            new CindyPiAdapter(configuredUserDataRoot: cindyUserDataRoot ?? Path.Combine(Path.GetDirectoryName(claudeRoot)!, "cindy-missing")),
            new GrokAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "grok-missing")),
            new OpenClawAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "openclaw-missing")),
            new AntigravityAdapter(configuredDataDir: Path.Combine(Path.GetDirectoryName(claudeRoot)!, "antigravity-missing")),
            pricingEngine,
            store,
            pricingClient: null,
            timeZone: Utc,
            scanRegistry: registry);
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

        /// <summary>模拟库被外部清空（I-042 自愈测试用）。</summary>
        public void ClearFacts() => _facts.Clear();

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

    /// <summary>记录管道执行线程与同步上下文的 Claude 桩（管道第一个源，入口必然落在
    /// GetDailyUsageAsync 的同步前缀内——正是旧实现内联执行调用线程的位置）。</summary>
    private sealed class RecordingClaudeAdapter : IClaudeCodeJsonlAdapter
    {
        private readonly TaskCompletionSource<(int ThreadId, SynchronizationContext? Context)> _started;

        public RecordingClaudeAdapter(TaskCompletionSource<(int ThreadId, SynchronizationContext? Context)> started)
        {
            _started = started;
        }

        public Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct = default)
        {
            _started.TrySetResult((Environment.CurrentManagedThreadId, SynchronizationContext.Current));
            return Task.FromResult(new ClaudeCodeScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
        }
    }
}
