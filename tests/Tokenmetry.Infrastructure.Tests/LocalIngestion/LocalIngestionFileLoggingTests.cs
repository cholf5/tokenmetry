namespace Tokenmetry.Infrastructure.Tests.LocalIngestion;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Cindy;
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
using Tokenmetry.Infrastructure.Codebuff;

/// <summary>
/// I-045 管道埋点：默认配置下一轮采集后，日志文件存在且含轮开始/结束、每源开始/结束（耗时/文件数/
/// 记录数）、写库起止；数据源异常分支同时落文件日志（含堆栈）。隐私红线由 IngestionFileLoggerTests
/// 与埋点载荷（只写路径/计数/耗时/异常）共同守住。
/// </summary>
public class LocalIngestionFileLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ccusage-flog-{Guid.NewGuid():N}");

    public LocalIngestionFileLoggingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Round_WritesRoundSourceAndStoreEntries()
    {
        var logDir = Path.Combine(_root, "logs");
        using var logger = new IngestionFileLogger(logDir, enabled: true);
        var claudeRoot = Path.Combine(_root, "claude");
        Directory.CreateDirectory(claudeRoot);
        var session = Path.Combine(claudeRoot, "projects", "p", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllTextAsync(session,
            "{\"sessionId\":\"s\",\"timestamp\":\"2026-05-25T10:00:00.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-test\",\"usage\":{\"input_tokens\":10,\"output_tokens\":20}},\"costUSD\":0.05,\"requestId\":\"r1\"}\n");

        var source = CreateSource(claudeRoot, logger);

        var rows = await source.GetDailyUsageAsync();
        rows.Should().ContainSingle();

        logger.Dispose();
        var text = File.ReadAllText(Path.Combine(logDir, IngestionFileLogger.LogFileName));

        text.Should().Contain("round start");
        text.Should().Contain("source [Claude Code] start").And.Contain("source [Claude Code] done in");
        text.Should().Contain("records: 1");
        text.Should().Contain("store upsert start").And.Contain("store upsert done in");
        text.Should().Contain("round done in");
        // I-042 短路轮标记不该出现在全量轮
        text.Should().NotContain("unchanged shortcut");
    }

    [Fact]
    public async Task UnchangedRound_LogsShortcutFact()
    {
        // I-042 联动（卡内 Related 要求）：短路与卡死在外部必须可区分——短路轮如实记录走了短路。
        var logDir = Path.Combine(_root, "logs");
        using var logger = new IngestionFileLogger(logDir, enabled: true);
        var claudeRoot = Path.Combine(_root, "claude");
        Directory.CreateDirectory(claudeRoot);
        var session = Path.Combine(claudeRoot, "projects", "p", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllTextAsync(session,
            "{\"sessionId\":\"s\",\"timestamp\":\"2026-05-25T10:00:00.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-test\",\"usage\":{\"input_tokens\":10,\"output_tokens\":20}},\"costUSD\":0.05,\"requestId\":\"r1\"}\n");

        var source = CreateSource(claudeRoot, logger);
        await source.GetDailyUsageAsync();
        await source.GetDailyUsageAsync();

        logger.Dispose();
        var text = File.ReadAllText(Path.Combine(logDir, IngestionFileLogger.LogFileName));
        text.Should().Contain("unchanged shortcut", "短路轮如实反映，外部可区分短路与卡死");
    }

    [Fact]
    public async Task ThrowingSource_LogsErrorWithStackTrace()
    {
        var logDir = Path.Combine(_root, "logs");
        using var logger = new IngestionFileLogger(logDir, enabled: true);
        var claudeRoot = Path.Combine(_root, "claude-empty");
        Directory.CreateDirectory(claudeRoot);

        var source = CreateSource(claudeRoot, logger, zcodeAdapter: new ThrowingZCode());
        await source.GetDailyUsageAsync();

        logger.Dispose();
        var text = File.ReadAllText(Path.Combine(logDir, IngestionFileLogger.LogFileName));
        text.Should().Contain("source [ZCode] ERROR");
        text.Should().Contain("InvalidOperationException").And.Contain("at ", "异常含堆栈（A-6 隔离的失败不再静默）");
    }

    [Fact]
    public void DisabledLogger_PipelineStillCompletes()
    {
        // 开关关闭：管道零变化（日志是纯旁路），零文件产生。
        var claudeRoot = Path.Combine(_root, "claude-off");
        Directory.CreateDirectory(claudeRoot);
        using var logger = new IngestionFileLogger(Path.Combine(_root, "logs-off"), enabled: false);

        var source = CreateSource(claudeRoot, logger);

        var act = () => source.GetDailyUsageAsync();
        act.Should().NotThrowAsync();
        Directory.Exists(Path.Combine(_root, "logs-off")).Should().BeFalse("关闭态零写盘");
    }

    private static LocalIngestionDataSource CreateSource(string claudeRoot, IngestionFileLogger logger, IZCodeSqliteAdapter? zcodeAdapter = null)
    {
        var store = new MemStore();
        var missing = Path.Combine(Path.GetTempPath(), $"ccusage-flog-missing-{Guid.NewGuid():N}");
        return new LocalIngestionDataSource(
            new ClaudeCodeJsonlAdapter(configuredRoot: claudeRoot),
            new CodexJsonlAdapter(configuredHome: missing),
            zcodeAdapter ?? new ZCodeStub(),
            new DshSessionAdapter(configuredHome: missing),
            new CopilotSessionAdapter(configuredHome: missing),
            new OpenCodeSqliteAdapter(configuredDataDir: missing),
            new GooseSqliteAdapter(configuredRoot: missing),
            new HermesSqliteAdapter(configuredHome: missing),
            new KiloSqliteAdapter(configuredDataDir: missing),
            new GeminiCliAdapter(configuredDataDir: missing),
            new AmpAdapter(configuredDataDir: missing),
            new DroidAdapter(configuredDataDir: missing),
            new QwenCodeAdapter(configuredDataDir: missing),
            new KimiCodeAdapter(configuredDataDir: missing),
            new CodebuffAdapter(configuredDataDir: missing),
            new PiAgentAdapter(configuredDataDir: missing),
            new CindyPiAdapter(configuredUserDataRoot: missing),
            new GrokAdapter(configuredDataDir: missing),
            new OpenClawAdapter(configuredDataDir: missing),
            new AntigravityAdapter(configuredDataDir: missing),
            new PricingEngine(overrides: new[]
            {
                new ModelPricingOverride { Model = "claude-test", InputPerMillion = 1m, OutputPerMillion = 2m },
            }),
            store,
            scanRegistry: new IngestionScanRegistry(),
            fileLogger: logger);
    }

    private sealed class ThrowingZCode : IZCodeSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom（注入的故障源）");
    }

    private sealed class ZCodeStub : IZCodeSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class MemStore : IUsageStore
    {
        private readonly Dictionary<(string Day, string Grain, string Agent, string Model, string Project), UsageFact> _facts = new();

        public Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            foreach (var fact in facts)
            {
                _facts[(fact.Day, fact.Grain, fact.Agent, fact.Model, fact.Project)] = fact;
            }

            return Task.CompletedTask;
        }

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) =>
            UpsertUsageFactsAsync(facts, ct);

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<UsageFact> facts = _facts.Values.ToList();
            return Task.FromResult(facts);
        }

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<SessionFact> facts = [];
            return Task.FromResult(facts);
        }

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
