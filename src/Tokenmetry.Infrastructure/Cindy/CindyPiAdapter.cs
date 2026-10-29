using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.PiAgent;

namespace Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy（桌面 Agent 宿主）Pi runtime 会话适配器：直读
/// <c>&lt;Cindy userData&gt;/pi-agent-home/sessions/*.jsonl</c>（pi 会话格式，usage / 内置 cost 逐条
/// assistant message 落盘），以 <b>Agent = "Cindy"</b> 出账，不区分 runtime。
/// <para>
/// 范围（有意为之）：只接 Pi runtime。Cindy 借用 Claude Code / Codex 当 runtime 时，transcript 落在
/// 与用户自己 CLI <b>共享</b>的 <c>~/.claude/projects</c>（本机实测：entrypoint=claude-desktop、
/// cwd=Cindy owners 目录）或 Cindy 私有的 <c>codex-home</c>；要把它们归给 Cindy 必须按
/// <c>sdk_session_id</c> 做归属仲裁才不会与 ClaudeCode / Codex 适配器双算——本轮不做，那些会话
/// 继续由原适配器出账（口径诚实：它们确实是 Claude Code / Codex 的执行）。
/// </para>
/// <para>
/// 解析复用 pi 蓝本管线（<see cref="PiSessionParser"/> + <see cref="PiReplayPlan"/>：父子重放抑制、
/// subagent-artifacts 剔除、跨文件复合 entry id 去重、行级容错），仅两处改写：
/// ① Agent 走 parser 的 store 名口径传常量 "Cindy"，模型名去掉 parser 加的 "[Cindy] " 前缀（Cindy 是
/// agent 身份而非模型命名空间，否则模型维度会多出一套伪模型名）；
/// ② 项目维度取会话头 <c>cwd</c>（蓝本口径是路径 "sessions" 段后一段，Cindy 的 sessions 目录是扁平的，
/// 会退化成时间戳文件名）——无 cwd 时回落 "Unknown Project"，与 Claude / Codex 适配器同口径。
/// </para>
/// <para>每次扫描按文件戳记忆化（<see cref="FileStampMemo"/>）：账面无变化轮零读取零解析并交
/// <c>FilesChanged=0</c>，驱动采集管道短路。实例持有扫描记忆，非线程安全，以单例接入 App DI，
/// 并发由 <c>LocalIngestionDataSource</c> 的管道信号量串行化。</para>
/// </summary>
public class CindyPiAdapter : ICindyAdapter
{
    /// <summary>出账 Agent 名（= facts agent 维度值，= 数据源表源名）。</summary>
    public const string AgentName = "Cindy";

    /// <summary>无 cwd 会话头的项目回落值（Claude / Codex 适配器同串）。</summary>
    public const string UnknownProject = "Unknown Project";

    /// <summary>parser 为命名存储加在模型前的 store 前缀；Cindy 出账名不带它。</summary>
    private const string ModelStorePrefix = "[" + AgentName + "] ";

    private readonly string? _configuredUserDataRoot;
    private readonly CindyLogLocator _locator;
    private readonly PiSessionParser _parser;
    private readonly FileStampMemo _memo = new();

    public CindyPiAdapter(
        string? configuredUserDataRoot = null,
        CindyLogLocator? locator = null,
        PiSessionParser? parser = null,
        PricingEngine? pricingEngine = null)
    {
        _configuredUserDataRoot = configuredUserDataRoot;
        _locator = locator ?? new CindyLogLocator();
        // 与 PiAdapter 同接线：identity cost（重放签名 / 复合 entry id 的「有效成本」）用共享定价引擎，
        // 用户覆盖与 models.dev 远程数据才能影响去重口径；未传时退回内置快照（离线确定性）。
        _parser = parser ?? new PiSessionParser(pricingEngine is null
            ? null
            : record => pricingEngine.PriceRecord(record).CostUsd ?? 0m);
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveSessionDirectories(_configuredUserDataRoot);
        var files = new List<string>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = new List<string>();
            SourceFileCollector.Collect(root, "jsonl", candidates);
            foreach (var file in candidates)
            {
                // 派生转录（子会话 artifacts）由父会话覆盖：与 pi 适配器同一剔除规则，避免重放前缀双算。
                if (!PiAgentAdapter.IsSubagentArtifactTranscript(file))
                {
                    files.Add(file);
                }
            }
        }

        // 解析期配置签名（扫描根清单）折进指纹：目录迁移/换根时文件可以一字未动，但归属的根变了。
        var stamps = FileStampMemo.WithConfig(FileStampMemo.Capture(files), string.Join('\n', roots));
        if (_memo.TryReuse(stamps, files.Count, roots, out var reused))
        {
            return Task.FromResult(reused);
        }

        var diagnostics = new JsonlScanDiagnostics();
        var loaded = new List<PiFileOutcome>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            // storeRoot 传 null：Cindy 的 sessions 目录是扁平的，命名存储口径（相对根首段）取不到项目名，
            // 项目维度随后由会话头 cwd 覆盖。
            var outcome = _parser.ParseSessionFile(file, new PiStoreContext(AgentName, StoreRoot: null));
            loaded.Add(outcome);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;
        }

        diagnostics.FilesDiscovered += files.Count;

        var plan = PiReplayPlan.Build(files, loaded.Select(static outcome => outcome.Data).ToList());

        var records = new List<UsageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalEntries = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var data = loaded[index].Data;
            if (data is null)
            {
                continue;
            }

            var project = ResolveProject(data.Header?.Cwd);
            var skip = plan.SkipPrefix(index);
            for (var entryIndex = skip; entryIndex < data.Entries.Count; entryIndex++)
            {
                var entry = data.Entries[entryIndex];
                totalEntries++;
                ApplyCindyIdentity(entry.Record, project);
                if (seen.Add(entry.CompositeId))
                {
                    records.Add(entry.Record);
                }
            }
        }

        diagnostics.DuplicateRecords += totalEntries - records.Count;
        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = records.Count(static record => record.Model is null);
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // 走到全量读取即本轮账面有变化（有文件填 1）；无文件不设值，由管道按"本轮发现即空"规则处理。
        diagnostics.FilesChanged = files.Count > 0 ? 1 : null;

        var ordered = records.OrderBy(static record => record.Timestamp).ToList();
        // 记忆化准入：全部发现文件都成功读取才落账（有文件打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == files.Count ? stamps : null, ordered, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = ordered,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 改写 parser 的归属：Agent 恒为 "Cindy"、模型去掉 store 前缀、项目取会话头 cwd，
    /// 并把「网关回填 0 成本」改写成未知（见下）。逐条在去重前做（改写只影响 facts 维度与成本，
    /// 不影响 parser 既算好的复合 entry id / 重放签名）。
    /// </summary>
    internal static void ApplyCindyIdentity(UsageRecord record, string? project)
    {
        record.Agent = AgentName;
        if (record.Model is { } model && model.StartsWith(ModelStorePrefix, StringComparison.Ordinal))
        {
            record.Model = model[ModelStorePrefix.Length..];
        }

        record.ProjectPath = project ?? UnknownProject;

        // Cindy 走自有网关，会话日志里 cost.total 恒为 0（本机实测 glm-5-3-flash / deepseek-flash 都是 0）
        // ——那是「没报成本」而不是「免费」。留着 0 会锁死定价引擎（记录自带成本非空即直接采用，
        // 用户价目覆盖 / 模型别名永远不生效），故非零 token 的 0 成本按未知交给主管道计价：
        // 命中即真实成本，未命中记 null（界面对账显示「未计价」，而不是谎报 0 元）。
        if (record.CostUsd == 0m && record.TotalTokens > 0)
        {
            record.CostUsd = null;
        }
    }

    /// <summary>会话头 cwd → 项目路径（去首尾空白与尾部分隔符）；缺失/空串返回 null（调用方回落 Unknown Project）。</summary>
    internal static string? ResolveProject(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var trimmed = cwd.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 0 ? trimmed : null;
    }
}
