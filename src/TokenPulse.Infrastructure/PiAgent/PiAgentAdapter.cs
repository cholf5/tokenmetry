using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.PiAgent;

/// <summary>
/// pi-agent 直读适配器（I-019，蓝本 ../ccusage/rust/adapters/pi/src/loader.rs 移植主线 +
/// crates/ccusage-adapter-all 的命名存储装配；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="PiLogLocator"/>，PI_AGENT_DIR → ~/.pi/agent/sessions；ccusage.json 的
/// pi.stores[] 附加命名存储）→ 逐作用域装载数据：递归收集 .jsonl（剔除含 "subagent-artifacts"
/// 路径段的派生转录）→ 逐文件解析（<see cref="PiSessionParser"/>）→ 重放抑制计划
/// （<see cref="PiReplayPlan"/>，跳过子会话与父文件一致的前缀）→ 跨文件复合 entry id first-wins
/// 去重 → 时间戳稳定排序。命名存储<b>作为独立 agent</b> 出账（Agent = store 名，蓝本
/// adapter-all loader 同语义），与默认存储的作用域隔离（各自的重放计划与去重集）。
/// 每次全量重读；实例无扫描状态，线程安全；以单例接入 App DI。
/// </summary>
public class PiAgentAdapter : IPiAgentAdapter
{
    private readonly string? _configuredDataDir;
    private readonly PiLogLocator _locator;
    private readonly PiSessionParser _parser;

    public PiAgentAdapter(
        string? configuredDataDir = null,
        PiLogLocator? locator = null,
        PiSessionParser? parser = null,
        PricingEngine? pricingEngine = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new PiLogLocator();
        // PI-3 接线（I-024）：应用装配传入共享定价引擎（用户覆盖 / 远程数据与计价循环同源），
        // identity cost 与引擎同一套候选/别名口径；未传时解析器退回内置快照默认引擎（离线确定性）。
        _parser = parser ?? new PiSessionParser(pricingEngine is null
            ? null
            : record => pricingEngine.PriceRecord(record).CostUsd ?? 0m);
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var diagnostics = new JsonlScanDiagnostics();
        var roots = new List<string>();
        var records = new List<UsageRecord>();

        // 默认作用域（蓝本 load_entries）：store 名 "pi"。
        var defaultRoots = _locator.ResolveSessionDirectories(_configuredDataDir);
        roots.AddRange(defaultRoots);
        LoadScope(defaultRoots, storeName: "pi", isNamedScope: false, records, diagnostics, ct);

        // 命名存储作用域（蓝本 resolve_named_pi_store_paths + load_entries_for_store_paths）：
        // 每个命名存储独立 agent、独立重放计划与去重集；路径与已占用路径重叠的存储跳过
        // （蓝本报配置错误终止命令，桌面降级为跳过——见卡片偏差⑤）。
        var claimedRoots = defaultRoots.Select(NormalizeForOverlap).ToList();
        foreach (var store in _locator.LoadNamedStores())
        {
            ct.ThrowIfCancellationRequested();
            var storeRoots = _locator.ResolveNamedStoreDirectories(store.Path);
            var accepted = new List<string>();
            foreach (var storeRoot in storeRoots)
            {
                var key = NormalizeForOverlap(storeRoot);
                if (claimedRoots.Any(claimed => PathsOverlap(key, claimed)))
                {
                    continue;
                }

                claimedRoots.Add(key);
                accepted.Add(storeRoot);
            }

            if (accepted.Count == 0)
            {
                continue;
            }

            roots.AddRange(accepted);
            LoadScope(accepted, store.Name, isNamedScope: true, records, diagnostics, ct);
        }

        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = records.Count(static record => record.Model is null);
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>单作用域装载（蓝本 load_entries_from_paths）：跨全部路径汇总发现清单（路径序 × 枚举序，
    /// 无排序——蓝本同），单次重放计划 + 单个去重集；命名存储的每文件作用域根 = 收出该文件的路径
    /// （蓝本 read_session_file_data_for_store(file, &amp;path)）。</summary>
    private void LoadScope(
        IReadOnlyList<string> paths,
        string storeName,
        bool isNamedScope,
        List<UsageRecord> records,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
        var files = new List<(string File, string? StoreRoot)>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = new List<string>();
            SourceFileCollector.Collect(path, "jsonl", candidates);
            foreach (var file in candidates)
            {
                if (!IsSubagentArtifactTranscript(file))
                {
                    // 命名存储：文件作用域根 = 收出该文件的具体路径（蓝本 read_session_file_data_for_store
                    // 的 &path 参数）；默认作用域根恒 null（走 "sessions" 段扫描提取）。
                    files.Add((file, isNamedScope ? path : null));
                }
            }
        }

        var loaded = new List<PiFileOutcome>(files.Count);
        foreach (var (file, storeRoot) in files)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = _parser.ParseSessionFile(file, new PiStoreContext(storeName, storeRoot));
            loaded.Add(outcome);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;
        }

        var plan = PiReplayPlan.Build(files.Select(entry => entry.File).ToList(),
            loaded.Select(outcome => outcome.Data).ToList());

        // 蓝本 discovered_files 为 subagent-artifacts 剔除后的清单（剔除文件不计数）。
        diagnostics.FilesDiscovered += files.Count;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scopeRecords = new List<UsageRecord>();
        var totalEntries = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var data = loaded[index].Data;
            if (data is null)
            {
                continue;
            }

            var skip = plan.SkipPrefix(index);
            for (var entryIndex = skip; entryIndex < data.Entries.Count; entryIndex++)
            {
                var entry = data.Entries[entryIndex];
                totalEntries++;
                if (seen.Add(entry.CompositeId))
                {
                    scopeRecords.Add(entry.Record);
                }
            }
        }

        diagnostics.DuplicateRecords += totalEntries - scopeRecords.Count;

        // 蓝本 entries.sort_by_key(timestamp)：稳定排序（同刻保持文件/行序，Enumerable.OrderBy 稳定）。
        records.AddRange(scopeRecords.OrderBy(static record => record.Timestamp));
    }

    /// <summary>派生转录判定（蓝本 is_subagent_artifact_transcript）：路径任一段恰为
    /// "subagent-artifacts"（run-*/ 新上下文子会话是主记录，保留）。</summary>
    public static bool IsSubagentArtifactTranscript(string path)
    {
        return path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "subagent-artifacts");
    }

    /// <summary>重叠检测键（蓝本 path_key：canonicalize，失败用原路径；桌面用 GetFullPath 不解析
    /// 符号链接——理论角落见卡片偏差⑧）。</summary>
    private static string NormalizeForOverlap(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return path;
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
