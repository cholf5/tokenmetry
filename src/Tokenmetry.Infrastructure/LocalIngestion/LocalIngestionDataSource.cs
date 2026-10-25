using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
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
using Tokenmetry.Infrastructure.ModelsDev;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;

namespace Tokenmetry.Infrastructure.LocalIngestion;

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
/// <para>
/// I-042 无变化轮短路：全部源报告"本轮账面无变化"（显式 FilesChanged=0，或未追踪变更且本轮
/// 发现即空——发现每轮现跑，空即上轮也空）时，跳过补价/聚合/落库/旧分解行清理，直接读 facts
/// 返回；facts 读回为空则落穿全量管道自愈重建（库被外部清空 / 迁移异常）。依赖"每轮全量重算"
/// 的语义变更（I-037 改价格即生效、别名热更新）经 <see cref="IAggregationInvalidator"/>
/// 置一次性强全量旗标——旗标仅在全量管道成功完成后清除，中途失败保留到下轮重试。
/// </para>
/// </summary>
public class LocalIngestionDataSource : IUsageDataSource, IAggregationInvalidator
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
    private readonly IngestionScanRegistry? _scanRegistry;
    private readonly SemaphoreSlim _ingestionGate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// I-042 一次性强全量旗标（<see cref="IAggregationInvalidator"/>）：价格/别名热更新后下一轮
    /// 无视无变化判定。volatile 读写在 UI 线程（设置页 VM）与管道线程（信号量内）之间传递；
    /// 消费点在信号量内，实际不构成竞态，volatile 仅防执行序意外。
    /// </summary>
    private volatile bool _forceFullNextRound;

    /// <inheritdoc />
    public void InvalidateAggregation() => _forceFullNextRound = true;

    /// <summary>无变化判定（I-042）：显式 FilesChanged=0；或适配器不追踪变更（null）且本轮
    /// 发现即空、无记录——文件发现是每轮现跑的，"本轮为空"蕴含"上轮也为空"，复用 facts 安全。</summary>
    internal static bool IsScanUnchanged(JsonlScanResult result) =>
        result.Diagnostics.FilesChanged == 0
        || (result.Diagnostics.FilesChanged is null
            && result.Diagnostics.FilesDiscovered == 0
            && result.Records.Count == 0);

    /// <summary>
    /// 管道扫描清单（S-002 数据源表的"已识别数据源"即此清单，经 <see cref="IngestionScanRegistry"/>
    /// 注册，UI 不手写）：(源名, 扫描委托) 对，顺序即 <see cref="GetDailyUsageAsync"/> 的扫描与
    /// 记录拼接顺序。源名 = 各适配器解析器声明的 <c>AgentName</c>（= facts agent 维度出账名）；
    /// pi-agent 的记录按存储作用域出账（默认 "pi"，命名存储独立 agent），无单一出账名，源名取
    /// 适配器名 "pi-agent"；Antigravity 适配器无 AgentName 常量，取其出账名同值字面量。
    /// ClaudeCodeJsonlAdapter 返回 <see cref="ClaudeCodeScanResult"/>（<see cref="JsonlScanResult"/> 子类），
    /// 经 async await 归一为共享扫描模型。
    /// </summary>
    private (string Agent, Func<CancellationToken, Task<JsonlScanResult>> Scan)[] BuildScanPipeline() =>
    [
        (ClaudeCodeJsonlLineParser.AgentName, async ct => await _claudeAdapter.ScanAsync(ct)),
        (CodexJsonlFileParser.AgentName, async ct => await _codexAdapter.ScanAsync(ct)),
        (ZCodeRecordMapper.AgentName, async ct => await _zcodeAdapter.ScanAsync(ct)),
        (DshSessionLogParser.AgentName, async ct => await _dshAdapter.ScanAsync(ct)),
        (CopilotSessionParser.AgentName, async ct => await _copilotAdapter.ScanAsync(ct)),
        (OpenCodeMessageParser.AgentName, async ct => await _openCodeAdapter.ScanAsync(ct)),
        (GooseSessionMapper.AgentName, async ct => await _gooseAdapter.ScanAsync(ct)),
        (HermesSessionParser.AgentName, async ct => await _hermesAdapter.ScanAsync(ct)),
        (KiloMessageParser.AgentName, async ct => await _kiloAdapter.ScanAsync(ct)),
        (GeminiLogParser.AgentName, async ct => await _geminiAdapter.ScanAsync(ct)),
        (AmpThreadParser.AgentName, async ct => await _ampAdapter.ScanAsync(ct)),
        (DroidSettingsParser.AgentName, async ct => await _droidAdapter.ScanAsync(ct)),
        (QwenLogParser.AgentName, async ct => await _qwenAdapter.ScanAsync(ct)),
        (KimiLogParser.AgentName, async ct => await _kimiAdapter.ScanAsync(ct)),
        (CodebuffChatParser.AgentName, async ct => await _codebuffAdapter.ScanAsync(ct)),
        ("pi-agent", async ct => await _piAdapter.ScanAsync(ct)),
        (GrokLogParser.AgentName, async ct => await _grokAdapter.ScanAsync(ct)),
        (OpenClawLogParser.AgentName, async ct => await _openClawAdapter.ScanAsync(ct)),
        ("Antigravity", async ct => await _antigravityAdapter.ScanAsync(ct)),
    ];

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
        TimeZoneInfo? timeZone = null,
        IngestionScanRegistry? scanRegistry = null)
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
        _scanRegistry = scanRegistry;
        // S-002：管道组成即"已识别数据源"清单，构造时注册（含未扫描态——UI 显示"尚未扫描"）
        if (_scanRegistry is { } registry)
        {
            foreach (var (agent, _) in BuildScanPipeline())
            {
                registry.RegisterSource(agent);
            }
        }
    }

    public Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default) =>
        Task.Run(() => GetDailyUsageCoreAsync(ct));

    /// <summary>
    /// 管道主体。公共入口经 <c>Task.Run</c> 整体移到线程池（线程模型同
    /// <see cref="Tokenmetry.Core.Services.CacheService"/> 的公共入口约定：调用方可能在 UI 线程，
    /// 而 <c>Microsoft.Data.Sqlite</c> 的 *Async API 同步完成、JSONL 读取多为缓冲同步完成——不隔离
    /// 的话整条 19 源管道的续体会逐段排回调用方上下文，首启全量扫描曾在 UI 线程上内联执行
    /// （DashboardViewModel 构造初始化触发），把窗口冻住到扫描结束）。
    /// </summary>
    private async Task<List<DailyUsageSummary>> GetDailyUsageCoreAsync(CancellationToken ct)
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

            // S-002：逐源扫描（顺序与拼接序 = BuildScanPipeline 清单），每源完成后向注册表登记
            // 真实快照（状态 / 累计记录数 / 完成时刻），供设置页数据源表读取。
            // A-6 逐源异常隔离：任一适配器抛出只跳过该源（登记 Error 快照供设置页如实显示），
            // 其余源正常出数——此前无隔离，一源抛出即整轮 19 源全部失败且几乎不可观测。
            var scanResults = new List<JsonlScanResult>(BuildScanPipeline().Length);
            foreach (var (agent, scan) in BuildScanPipeline())
            {
                JsonlScanResult result;
                try
                {
                    result = await scan(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Tokenmetry] 数据源 {agent} 扫描失败（本轮跳过，其余源不受影响）：{ex.Message}");
                    _scanRegistry?.ReportScan(agent, JsonlScanStatus.Error, 0, DateTimeOffset.Now);
                    continue;
                }

                scanResults.Add(result);
                _scanRegistry?.ReportScan(agent, result.Diagnostics.Status, result.Records.Count, DateTimeOffset.Now);
            }

            var records = scanResults.SelectMany(r => r.Records).ToList();
            if (records.Count == 0)
            {
                // 无记录（日志目录不存在 / 全空）时不动存储：保留既有数据（含旧 CLI 行），
                // 避免空扫描把用户仅存的历史清掉。
                return new List<DailyUsageSummary>();
            }

            // I-042 无变化轮短路：全部源账面未动 → 计价/聚合/落库的必然结果与现状逐位相同，
            // 直接读 facts 返回（短路轮连 ClearUnpricedModels 都不做——无新记录，未知清单不变）。
            // facts 读回为空（库被外部清空 / 迁移异常）则落穿全量管道自愈重建。
            if (!_forceFullNextRound && scanResults.All(IsScanUnchanged))
            {
                var cachedFacts = await _store.GetUsageFactsAsync(ct);
                if (cachedFacts.Count > 0)
                {
                    return UsageFactsMapper.ToDailySummaries(cachedFacts);
                }
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
            // 强全量旗标在全量管道成功完成后才清除：中途失败（落库抛错等）保留到下轮重试，
            // 不让失败轮吞掉价格/别名热更新的生效保证。
            _forceFullNextRound = false;
            return UsageFactsMapper.ToDailySummaries(facts);
        }
        finally
        {
            _ingestionGate.Release();
        }
    }
}
