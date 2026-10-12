using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.Antigravity;
using CCUsage.Infrastructure.Amp;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.Codebuff;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.CopilotSession;
using CCUsage.Infrastructure.DshSession;
using CCUsage.Infrastructure.Droid;
using CCUsage.Infrastructure.GeminiCli;
using CCUsage.Infrastructure.Grok;
using CCUsage.Infrastructure.GooseSqlite;
using CCUsage.Infrastructure.HermesSqlite;
using CCUsage.Infrastructure.KiloSqlite;
using CCUsage.Infrastructure.KimiCode;
using CCUsage.Infrastructure.ModelsDev;
using CCUsage.Infrastructure.OpenClaw;
using CCUsage.Infrastructure.OpenCodeSqlite;
using CCUsage.Infrastructure.PiAgent;
using CCUsage.Infrastructure.QwenCode;
using CCUsage.Infrastructure.ZCodeSqlite;

namespace CCUsage.Infrastructure.LocalIngestion;

/// <summary>
/// 本地采集数据源（I-005，I-006 起含 ZCode，I-007 起含 DSH，I-008 起含 Copilot CLI，
/// I-009 起含 OpenCode，I-012 起含 Goose，I-016 起含 Hermes，I-017 起含 Kilo，I-010 起含 Gemini CLI，
/// I-014 起含 Amp，I-015 起含 Droid，I-011 起含 Qwen Code，I-013 起含 Kimi，I-018 起含 Codebuff，
/// I-019 起含 pi-agent，I-022 起含 Grok，I-020 起含 OpenClaw，I-021 起含 Antigravity）：
/// 直读适配器扫描 → 定价 → facts 落库 → 从 facts 读回每日汇总。刷新流程：各适配器扫描
/// （JSONL 增量 / SQLite 账本全量）→
/// <see cref="PricingEngine"/> 为无 costUSD 的记录补价（补价结果进侧表、不写回记录实例——增量扫描
/// 跨刷新复用记录，写回会被 LogCost 短路锁死旧价；日志自带 costUSD 的记录直接采用原值，含
/// ZCode 自定义 provider 行与 DSH 不可计价路由行的 0 成本）→
/// <see cref="UsageFactsMapper.ToUsageFacts"/> / <see cref="UsageFactsMapper.ToSessionFacts"/> 聚合 →
/// <see cref="IUsageStore"/> upsert（幂等，最新值生效）→ 删除旧 CLI 分解行 → 读回。
/// 读取走 facts 存储而非内存聚合：facts 是页面查询的唯一数据面，读回即验证了页面将看到的形状。
/// 适配器实例由 DI 以单例提供；本类用信号量串行化整个管道，
/// 防御手动刷新与后台定时刷新并发进入（JSONL 扫描器非线程安全）。
/// </summary>
public class LocalIngestionDataSource : IUsageDataSource
{
    private readonly IClaudeCodeJsonlAdapter _claudeAdapter;
    private readonly ICodexJsonlAdapter _codexAdapter;
    private readonly IZCodeSqliteAdapter _zcodeAdapter;
    private readonly IDshSessionAdapter _dshAdapter;
    private readonly ICopilotSessionAdapter _copilotAdapter;
    private readonly IOpenCodeSqliteAdapter _openCodeAdapter;
    private readonly IGooseSqliteAdapter _gooseAdapter;
    private readonly IHermesSqliteAdapter _hermesAdapter;
    private readonly IKiloSqliteAdapter _kiloAdapter;
    private readonly IGeminiCliAdapter _geminiAdapter;
    private readonly IAmpAdapter _ampAdapter;
    private readonly IDroidAdapter _droidAdapter;
    private readonly IQwenCodeAdapter _qwenAdapter;
    private readonly IKimiCodeAdapter _kimiAdapter;
    private readonly ICodebuffAdapter _codebuffAdapter;
    private readonly IPiAgentAdapter _piAdapter;
    private readonly IGrokAdapter _grokAdapter;
    private readonly IOpenClawAdapter _openClawAdapter;
    private readonly IAntigravityAdapter _antigravityAdapter;
    private readonly PricingEngine _pricingEngine;
    private readonly ModelsDevPricingClient? _pricingClient;
    private readonly IUsageStore _store;
    private readonly TimeZoneInfo? _timeZone;
    private readonly SemaphoreSlim _ingestionGate = new(initialCount: 1, maxCount: 1);

    /// <inheritdoc />
    public bool PersistsFacts => true;

    public LocalIngestionDataSource(
        IClaudeCodeJsonlAdapter claudeAdapter,
        ICodexJsonlAdapter codexAdapter,
        IZCodeSqliteAdapter zcodeAdapter,
        IDshSessionAdapter dshAdapter,
        ICopilotSessionAdapter copilotAdapter,
        IOpenCodeSqliteAdapter openCodeAdapter,
        IGooseSqliteAdapter gooseAdapter,
        IHermesSqliteAdapter hermesAdapter,
        IKiloSqliteAdapter kiloAdapter,
        IGeminiCliAdapter geminiAdapter,
        IAmpAdapter ampAdapter,
        IDroidAdapter droidAdapter,
        IQwenCodeAdapter qwenAdapter,
        IKimiCodeAdapter kimiAdapter,
        ICodebuffAdapter codebuffAdapter,
        IPiAgentAdapter piAdapter,
        IGrokAdapter grokAdapter,
        IOpenClawAdapter openClawAdapter,
        IAntigravityAdapter antigravityAdapter,
        PricingEngine pricingEngine,
        IUsageStore store,
        ModelsDevPricingClient? pricingClient = null,
        TimeZoneInfo? timeZone = null)
    {
        _claudeAdapter = claudeAdapter ?? throw new ArgumentNullException(nameof(claudeAdapter));
        _codexAdapter = codexAdapter ?? throw new ArgumentNullException(nameof(codexAdapter));
        _zcodeAdapter = zcodeAdapter ?? throw new ArgumentNullException(nameof(zcodeAdapter));
        _dshAdapter = dshAdapter ?? throw new ArgumentNullException(nameof(dshAdapter));
        _copilotAdapter = copilotAdapter ?? throw new ArgumentNullException(nameof(copilotAdapter));
        _openCodeAdapter = openCodeAdapter ?? throw new ArgumentNullException(nameof(openCodeAdapter));
        _gooseAdapter = gooseAdapter ?? throw new ArgumentNullException(nameof(gooseAdapter));
        _hermesAdapter = hermesAdapter ?? throw new ArgumentNullException(nameof(hermesAdapter));
        _kiloAdapter = kiloAdapter ?? throw new ArgumentNullException(nameof(kiloAdapter));
        _geminiAdapter = geminiAdapter ?? throw new ArgumentNullException(nameof(geminiAdapter));
        _ampAdapter = ampAdapter ?? throw new ArgumentNullException(nameof(ampAdapter));
        _droidAdapter = droidAdapter ?? throw new ArgumentNullException(nameof(droidAdapter));
        _qwenAdapter = qwenAdapter ?? throw new ArgumentNullException(nameof(qwenAdapter));
        _kimiAdapter = kimiAdapter ?? throw new ArgumentNullException(nameof(kimiAdapter));
        _codebuffAdapter = codebuffAdapter ?? throw new ArgumentNullException(nameof(codebuffAdapter));
        _piAdapter = piAdapter ?? throw new ArgumentNullException(nameof(piAdapter));
        _grokAdapter = grokAdapter ?? throw new ArgumentNullException(nameof(grokAdapter));
        _openClawAdapter = openClawAdapter ?? throw new ArgumentNullException(nameof(openClawAdapter));
        _antigravityAdapter = antigravityAdapter ?? throw new ArgumentNullException(nameof(antigravityAdapter));
        _pricingEngine = pricingEngine ?? throw new ArgumentNullException(nameof(pricingEngine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pricingClient = pricingClient;
        _timeZone = timeZone;
    }

    public async Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
    {
        await _ingestionGate.WaitAsync(ct);
        try
        {
            // models.dev 定价后台刷新：到期才发请求、单飞、失败静默、绝不阻塞主刷新
            //（fire-and-forget，本次采集先用内置快照 / 上一次成功数据计价，下一次刷新自然校正）。
            if (_pricingClient is { } pricingClient)
            {
                _ = Task.Run(() => pricingClient.RefreshIfDueAsync(ct), ct);
            }

            var claude = await _claudeAdapter.ScanAsync(ct);
            var codex = await _codexAdapter.ScanAsync(ct);
            var zcode = await _zcodeAdapter.ScanAsync(ct);
            var dsh = await _dshAdapter.ScanAsync(ct);
            var copilot = await _copilotAdapter.ScanAsync(ct);
            var openCode = await _openCodeAdapter.ScanAsync(ct);
            var goose = await _gooseAdapter.ScanAsync(ct);
            var hermes = await _hermesAdapter.ScanAsync(ct);
            var kilo = await _kiloAdapter.ScanAsync(ct);
            var gemini = await _geminiAdapter.ScanAsync(ct);
            var amp = await _ampAdapter.ScanAsync(ct);
            var droid = await _droidAdapter.ScanAsync(ct);
            var qwen = await _qwenAdapter.ScanAsync(ct);
            var kimi = await _kimiAdapter.ScanAsync(ct);
            var codebuff = await _codebuffAdapter.ScanAsync(ct);
            var pi = await _piAdapter.ScanAsync(ct);
            var grok = await _grokAdapter.ScanAsync(ct);
            var openclaw = await _openClawAdapter.ScanAsync(ct);
            var antigravity = await _antigravityAdapter.ScanAsync(ct);
            var records = claude.Records.Concat(codex.Records).Concat(zcode.Records).Concat(dsh.Records)
                .Concat(copilot.Records).Concat(openCode.Records).Concat(goose.Records)
                .Concat(hermes.Records).Concat(kilo.Records).Concat(gemini.Records).Concat(amp.Records)
                .Concat(droid.Records).Concat(qwen.Records).Concat(kimi.Records).Concat(codebuff.Records)
                .Concat(pi.Records).Concat(grok.Records).Concat(openclaw.Records).Concat(antigravity.Records)
                .ToList();
            if (records.Count == 0)
            {
                // 无记录（日志目录不存在 / 全空）时不动存储：保留既有数据（含旧 CLI 行），
                // 避免空扫描把用户仅存的历史清掉。
                return new List<DailyUsageSummary>();
            }

            _pricingEngine.ClearUnpricedModels();
            // 补价结果进侧表（按记录引用），不写回记录：适配器增量扫描会跨刷新复用同一批
            // UsageRecord 实例，写回 CostUsd 会让下次刷新被 LogCost 短路锁死旧价，
            // 用户覆盖 / models.dev 更新永不生效。日志自带成本（CostUsd 非空）的记录不进侧表。
            var pricedCosts = new Dictionary<UsageRecord, decimal?>(ReferenceEqualityComparer.Instance);
            foreach (var record in records)
            {
                if (record.CostUsd is null)
                {
                    pricedCosts[record] = _pricingEngine.PriceRecord(record).CostUsd;
                }
            }

            var usageFacts = UsageFactsMapper.ToUsageFacts(records, _timeZone, pricedCosts);
            var sessionFacts = UsageFactsMapper.ToSessionFacts(records, pricedCosts);
            await _store.UpsertUsageFactsAsync(usageFacts, ct);
            await _store.UpsertSessionFactsAsync(sessionFacts, ct);

            // 数据源切换清理（I-002 转记）：本地采集是唯一权威源后，旧 CLI 源的分解粒度行
            // （day / agent / model / project grain）不应再影响读取；full 粒度行不受影响。
            await _store.DeleteDecompositionFactsAsync(ct);

            var facts = await _store.GetUsageFactsAsync(ct);
            return UsageFactsMapper.ToDailySummaries(facts);
        }
        finally
        {
            _ingestionGate.Release();
        }
    }
}
