using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Cache;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Cindy;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.LocalIngestion;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Pipeline;

/// <summary>
/// 插件数据源接入采集管线的端到端集成：启用插件 → 记录进入 facts 并登记数据源行；
/// 停用 → 数据源注销、宿主进程释放，但历史 facts 保留；Refresh 只增删差异项并复用未变化的源。
/// 全部落在临时目录与临时 SQLite 库，不触碰真实用户 AppData。
/// </summary>
public class PluginPipelineIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteUsageStore _store;

    public PluginPipelineIntegrationTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("tokenmetry-plugin-pipeline-").FullName;
        _store = new SqliteUsageStore(Path.Combine(_tempRoot, "tokenmetry.db"));
        Task.Run(() => _store.InitializeAsync()).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Enabled_plugin_records_reach_facts_and_source_row()
    {
        var host = new RecordingHostClient(OkOutcome(ValidDto()));
        var catalog = new FakePluginCatalog([Installation()]);
        var harness = CreateHarness(host, catalog);

        var summaries = await harness.DataSource.GetDailyUsageAsync();

        Assert.Contains(summaries.SelectMany(summary => summary.Agents), agent => agent.Agent == "Foo");
        Assert.Contains("Foo", harness.Registry.RegisteredSources);
        Assert.Contains(await _store.GetUsageFactsAsync(), fact => fact.Agent == "Foo");
        // 会话命名空间：插件原始 id "s1" 落库为 plugin:com.acme.foo:s1。
        Assert.Contains(await _store.GetSessionFactsAsync(), fact => fact.SessionId == "plugin:com.acme.foo:s1");
    }

    [Fact]
    public async Task Disabled_plugin_source_is_unregistered_but_facts_remain()
    {
        var host = new RecordingHostClient(OkOutcome(ValidDto()));
        var catalog = new FakePluginCatalog([Installation()]);
        var harness = CreateHarness(host, catalog);

        await harness.DataSource.GetDailyUsageAsync();
        Assert.Contains("Foo", harness.Registry.RegisteredSources);
        Assert.Contains(await _store.GetUsageFactsAsync(), fact => fact.Agent == "Foo");

        // 停用插件 → Refresh 注销数据源并释放宿主进程，但不删历史 facts（facts 是 upsert-only）。
        catalog.SetInstalled([Installation(enabled: false)]);
        harness.Provider.Refresh();

        Assert.DoesNotContain("Foo", harness.Registry.RegisteredSources);
        Assert.Contains("com.acme.foo", host.RemovedPluginIds);
        Assert.Contains(await _store.GetUsageFactsAsync(), fact => fact.Agent == "Foo");

        // 再跑一轮：插件源已不在管道里，内置源无数据 → store 原样保留。
        await harness.DataSource.GetDailyUsageAsync();
        Assert.Contains(await _store.GetUsageFactsAsync(), fact => fact.Agent == "Foo");
    }

    [Fact]
    public void Refresh_registers_and_unregisters_sources()
    {
        var host = new RecordingHostClient(OkOutcome(ValidDto()));
        var catalog = new FakePluginCatalog([]);
        var registry = new IngestionScanRegistry();
        var provider = new PluginSourceProvider(catalog, host, registry);

        provider.Refresh();
        Assert.DoesNotContain("Foo", registry.RegisteredSources);

        catalog.SetInstalled([Installation()]);
        provider.Refresh();
        Assert.Contains("Foo", registry.RegisteredSources);

        catalog.SetInstalled([]);
        provider.Refresh();
        Assert.DoesNotContain("Foo", registry.RegisteredSources);
        Assert.Contains("com.acme.foo", host.RemovedPluginIds);
    }

    [Fact]
    public void Refresh_reuses_unchanged_plugin_source_instances()
    {
        var host = new RecordingHostClient(OkOutcome(ValidDto()));
        var catalog = new FakePluginCatalog([Installation()]);
        var provider = new PluginSourceProvider(catalog, host, new IngestionScanRegistry());

        provider.Refresh();
        var first = Assert.Single(provider.GetEnabledSources());

        provider.Refresh();

        // 未变化的插件复用同一实例：其内部 last-known-good 快照不因 Refresh 丢失。
        Assert.Same(first, Assert.Single(provider.GetEnabledSources()));
        Assert.Empty(host.RemovedPluginIds);
    }

    [Fact]
    public void Refresh_rebuilds_changed_installation_and_drops_stale_host()
    {
        var host = new RecordingHostClient(OkOutcome(ValidDto()));
        var catalog = new FakePluginCatalog([Installation()]);
        var provider = new PluginSourceProvider(catalog, host, new IngestionScanRegistry());

        provider.Refresh();
        var first = Assert.Single(provider.GetEnabledSources());

        // 升级（版本目录变化）：旧宿主握着旧 --plugin-dir，必须 Remove 后重建源。
        catalog.SetInstalled([Installation(version: "2.0.0")]);
        provider.Refresh();
        var second = Assert.Single(provider.GetEnabledSources());

        Assert.NotSame(first, second);
        Assert.Contains("com.acme.foo", host.RemovedPluginIds);
    }

    [Fact]
    public void Plugin_session_ids_do_not_collide_across_agents()
    {
        // 两个插件各自有原始会话 id "1"：编码为 plugin:<id>:1 后成为两条独立会话行。
        var recordA = Record("plugin:com.a:1", "A");
        var recordB = Record("plugin:com.b:1", "B");

        var sessions = UsageFactsMapper.ToSessionFacts([recordA, recordB]);

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, fact => fact.SessionId == "plugin:com.a:1");
        Assert.Contains(sessions, fact => fact.SessionId == "plugin:com.b:1");
        // 编码后的 id 不会与内置源 / 其它插件的裸 id 相等。
        Assert.NotEqual("1", PluginSessionId.Encode("com.a", "1"));
        Assert.Equal("plugin:com.a:1", PluginSessionId.Encode("com.a", "1"));
    }

    // ---- helpers ----

    private PipelineHarness CreateHarness(RecordingHostClient host, FakePluginCatalog catalog)
    {
        var registry = new IngestionScanRegistry();
        var provider = new PluginSourceProvider(catalog, host, registry);
        provider.Refresh(); // 构造管道前先登记启用项（真实启动路径同为先刷新后建源）

        var dataSource = CreateDataSource(registry, provider);
        return new PipelineHarness(dataSource, registry, provider);
    }

    private LocalIngestionDataSource CreateDataSource(
        IngestionScanRegistry registry,
        IPluginSourceProvider pluginSources)
    {
        var root = Path.Combine(_tempRoot, "sources");
        Directory.CreateDirectory(root);
        string Missing(string name) => Path.Combine(root, name);

        var pricingEngine = new PricingEngine(overrides:
        [
            new ModelPricingOverride { Model = "foo-large", InputPerMillion = 1m, OutputPerMillion = 2m },
        ]);

        return new LocalIngestionDataSource(
            new ClaudeCodeJsonlAdapter(configuredRoot: Missing("claude")),
            new CodexJsonlAdapter(configuredHome: Missing("codex")),
            new ZCodeSqliteAdapter(configuredHome: Missing("zcode")),
            new DshSessionAdapter(configuredHome: Missing("dsh")),
            new CopilotSessionAdapter(configuredHome: Missing("copilot")),
            new OpenCodeSqliteAdapter(configuredDataDir: Missing("opencode")),
            new GooseSqliteAdapter(configuredRoot: Missing("goose")),
            new HermesSqliteAdapter(configuredHome: Missing("hermes")),
            new KiloSqliteAdapter(configuredDataDir: Missing("kilo")),
            new GeminiCliAdapter(configuredDataDir: Missing("gemini")),
            new AmpAdapter(configuredDataDir: Missing("amp")),
            new DroidAdapter(configuredDataDir: Missing("droid")),
            new QwenCodeAdapter(configuredDataDir: Missing("qwen")),
            new KimiCodeAdapter(configuredDataDir: Missing("kimi")),
            new CodebuffAdapter(configuredDataDir: Missing("codebuff")),
            new PiAgentAdapter(configuredDataDir: Missing("pi")),
            new CindyPiAdapter(configuredUserDataRoot: Missing("cindy")),
            new GrokAdapter(configuredDataDir: Missing("grok")),
            new OpenClawAdapter(configuredDataDir: Missing("openclaw")),
            new AntigravityAdapter(configuredDataDir: Missing("antigravity")),
            pricingEngine,
            _store,
            pricingClient: null,
            timeZone: TimeZoneInfo.Utc,
            scanRegistry: registry,
            fileLogger: null,
            pluginSources: pluginSources);
    }

    private static PluginScanOutcome OkOutcome(params PluginUsageRecord[] records) => new(
        HasChanges: true,
        Records: records,
        Diagnostics: new PluginDiagnostics { Status = PluginScanStatus.Ok });

    private static PluginUsageRecord ValidDto() => new()
    {
        RecordId = "r1",
        Timestamp = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
        SessionId = "s1",
        ProjectPath = "/work/foo",
        ProjectDisplayName = "Foo Workspace",
        Model = "foo-large",
        Provider = "acme",
        InputTokens = 10,
        OutputTokens = 5,
        CostUsd = 0.5m,
    };

    private static UsageRecord Record(string sessionId, string agent) => new()
    {
        Timestamp = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
        Agent = agent,
        SessionId = sessionId,
        ProjectPath = "/work/foo",
        Model = "foo-large",
        InputTokens = 10,
        OutputTokens = 5,
        CostUsd = 0.5m,
    };

    private static PluginInstallation Installation(
        string pluginId = "com.acme.foo",
        string displayName = "Foo",
        bool enabled = true,
        string version = "1.0.0") => new(
        new PluginManifest
        {
            SchemaVersion = 1,
            Id = pluginId,
            AgentId = pluginId,
            DisplayName = displayName,
            Version = version,
            ApiVersion = "1",
            EntryAssembly = "Foo.Plugin.dll",
            EntryType = "Foo.Plugin.FooUsagePlugin",
            Icon = "icon.png",
        },
        Directory: $"/plugin/{pluginId}/{version}",
        IconPath: $"/plugin/{pluginId}/{version}/icon.png",
        Enabled: enabled);

    private sealed record PipelineHarness(
        LocalIngestionDataSource DataSource,
        IngestionScanRegistry Registry,
        PluginSourceProvider Provider);

    /// <summary>可编排目录：测试直接改写 Installed 驱动 Refresh 的增删差异。</summary>
    private sealed class FakePluginCatalog(IReadOnlyList<PluginInstallation> installed) : IPluginCatalog
    {
        private IReadOnlyList<PluginInstallation> _installed = installed;

        public IReadOnlyList<PluginInstallation> Installed => _installed;

        public IReadOnlyList<PluginCatalogError> Invalid => [];

        public void SetInstalled(IReadOnlyList<PluginInstallation> installed) => _installed = installed;

        public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UninstallAsync(string pluginId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>固定返回同一扫描结果，并记录被释放的插件宿主 id。</summary>
    private sealed class RecordingHostClient(PluginScanOutcome outcome) : IPluginHostClient
    {
        public List<string> RemovedPluginIds { get; } = [];

        public Task<PluginScanOutcome> ScanAsync(
            PluginInstallation installation,
            bool forceFull,
            CancellationToken cancellationToken)
            => Task.FromResult(outcome);

        public void Remove(string pluginId) => RemovedPluginIds.Add(pluginId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
