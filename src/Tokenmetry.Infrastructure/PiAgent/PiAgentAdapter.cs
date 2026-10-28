using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.PiAgent;

/// <summary>
/// pi-agent 直读适配器（I-019，蓝本 ../ccusage/rust/adapters/pi/src/loader.rs 移植主线 +
/// crates/ccusage-adapter-all 的命名存储装配；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="PiLogLocator"/>，PI_AGENT_DIR → ~/.pi/agent/sessions；ccusage.json 的
/// pi.stores[] 附加命名存储）→ 汇总全部作用域文件清单（递归收集 .jsonl，剔除含 "subagent-artifacts"
/// 路径段的派生转录）→ 文件戳探针（<see cref="FileStampMemo"/>，路径 + mtime + 大小，只 stat 不读内容）→
/// 账面未动复用上轮记录（零读取零解析）→ 否则逐作用域装载数据：逐文件解析（<see cref="PiSessionParser"/>）→
/// 重放抑制计划（<see cref="PiReplayPlan"/>，跳过子会话与父文件一致的前缀）→ 跨文件复合 entry id
/// first-wins 去重 → 时间戳稳定排序。命名存储<b>作为独立 agent</b> 出账（Agent = store 名，蓝本
/// adapter-all loader 同语义），与默认存储的作用域隔离（各自的重放计划与去重集）。
/// 以 I-042 文件戳记忆化替代每轮全量重读（与共享扫描器的零变更缓存同口径）；记忆化准入：
/// 全部发现文件都成功读取才落账，有文件打不开的轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class PiAgentAdapter : IPiAgentAdapter
{
    private readonly string? _configuredDataDir;
    private readonly PiLogLocator _locator;
    private readonly PiSessionParser _parser;
    private readonly FileStampMemo _memo = new();

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
        // 作用域发现 + 文件清单收集（只 stat 前的目录枚举，不做任何解析）。
        // 默认作用域（蓝本 load_entries）：store 名 "pi"；命名存储作用域（蓝本
        // resolve_named_pi_store_paths + load_entries_for_store_paths）：路径与已占用路径重叠的
        // 存储跳过（蓝本报配置错误终止命令，桌面降级为跳过——见卡片偏差⑤）。
        var defaultRoots = _locator.ResolveSessionDirectories(_configuredDataDir);
        var roots = new List<string>(defaultRoots);
        var scopes = new List<(string StoreName, List<(string File, string? StoreRoot)> Files)>
        {
            ("pi", CollectScopeFiles(defaultRoots, isNamedScope: false, ct)),
        };

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
            scopes.Add((store.Name, CollectScopeFiles(accepted, isNamedScope: true, ct)));
        }

        var allFiles = scopes.SelectMany(scope => scope.Files).Select(entry => entry.File).ToList();
        // 文件戳 + 解析期配置签名：文件集不变而 store 改名 / 重叠根迁移时，记录归属（Agent /
        // Model / ProjectPath）会变而文件戳一字不变——签名折进指纹才不会复用旧归属。
        var stamps = FileStampMemo.WithConfig(FileStampMemo.Capture(allFiles), ScopeSignature(scopes));

        if (_memo.TryReuse(stamps, allFiles.Count, roots, out var reused))
        {
            return Task.FromResult(reused);
        }

        var diagnostics = new JsonlScanDiagnostics();
        var records = new List<UsageRecord>();
        foreach (var scope in scopes)
        {
            LoadScope(scope.StoreName, scope.Files, records, diagnostics, ct);
        }

        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = records.Count(static record => record.Model is null);
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：走到全量读取即本轮账面有变化（有文件填 1）；无文件不设值，
        // 由管道按"本轮发现即空"规则处理（与 ZCode 探针同口径）。
        diagnostics.FilesChanged = allFiles.Count > 0 ? 1 : null;

        // 记忆化准入：全部发现文件都成功读取才落账（有文件打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == allFiles.Count ? stamps : null, records, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 解析期配置签名（折进文件戳指纹）：作用域名 + 该作用域解析出的根。风险场景：store 改名、或
    /// 新增一个与既有根重叠的 store（文件在作用域间迁移）——文件清单与戳可以完全不变，而 Agent /
    /// Model / ProjectPath 全变（前两者还是 facts 主键维度：旧归属继续出账、新归属永不出账）。
    /// </summary>
    private static string ScopeSignature(
        IReadOnlyList<(string StoreName, List<(string File, string? StoreRoot)> Files)> scopes)
    {
        return string.Join('\n', scopes.Select(static scope =>
            scope.StoreName
            + "\u001f"
            + string.Join('|', scope.Files
                .Select(static entry => entry.StoreRoot ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static root => root, StringComparer.Ordinal))));
    }

    /// <summary>单作用域文件清单收集：跨全部路径汇总发现清单（路径序 × 枚举序，无排序——蓝本同），
    /// 剔除派生转录（subagent-artifacts 段）；命名存储的每文件作用域根 = 收出该文件的路径
    /// （蓝本 read_session_file_data_for_store(file, &amp;path)）。</summary>
    private List<(string File, string? StoreRoot)> CollectScopeFiles(
        IReadOnlyList<string> paths,
        bool isNamedScope,
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

        return files;
    }

    /// <summary>单作用域装载（蓝本 load_entries_from_paths）：单次重放计划 + 单个去重集；
    /// 文件清单由 <see cref="CollectScopeFiles"/> 预先收集（探针指纹的输入）。</summary>
    private void LoadScope(
        string storeName,
        List<(string File, string? StoreRoot)> files,
        List<UsageRecord> records,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
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
