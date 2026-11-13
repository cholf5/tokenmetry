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
/// 路径段的派生转录）→ <b>逐文件</b>文件戳记账（<see cref="FileStampMemo.TryCapture"/>，路径 + mtime +
/// 大小，只 stat 不读内容）：戳与解析期配置（store 名 / 作用域根）都没变的文件不重读不重解析，直接取用上轮
/// <see cref="PiFileOutcome"/>；本轮零变化即复用上轮记录（零读取零解析）→ 否则逐作用域装载数据：重放抑制
/// 计划（<see cref="PiReplayPlan"/>，跳过子会话与父文件一致的前缀）→ 跨文件复合 entry id first-wins
/// 去重 → 时间戳稳定排序。命名存储<b>作为独立 agent</b> 出账（Agent = store 名，蓝本 adapter-all loader
/// 同语义），与默认存储的作用域隔离（各自的重放计划与去重集）。
/// <para>
/// 记账必须逐文件而非整批：pi 的活跃会话文件每个回合都在长，「整批戳集全等才复用」会让一份活跃会话
/// 的存在把整批历史拉回全量重读（Cindy 源同病灶，本机实测 399 MB/轮）。记账准入：打不开 / stat 失败的
/// 文件不落账，下次扫描重试整个文件。实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由
/// LocalIngestionDataSource 串行化）。
/// </para>
/// </summary>
public class PiAgentAdapter : IPiAgentAdapter
{
    private readonly string? _configuredDataDir;
    private readonly PiLogLocator _locator;
    private readonly PiSessionParser _parser;

    /// <summary>逐文件扫描记忆：路径 → (文件戳, 解析期配置, 解析产出)。文件戳与解析期配置都没变才复用。</summary>
    private readonly Dictionary<string, (FileStamp Stamp, PiStoreContext Context, PiFileOutcome Outcome)> _fileCache =
        new(StringComparer.Ordinal);

    /// <summary>按文件记「存在解析错误」：状态聚合要纳入未重解析的历史文件，否则坏文件在第二轮之后
    /// 会被误报为 Ok（与共享扫描器同款）。</summary>
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    /// <summary>零变更轮缓存：全历史重建的产物与上轮逐位相同，直接复用（同实例，消费方视为只读）。</summary>
    private List<UsageRecord>? _lastRecords;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

    /// <summary>解析期配置签名（<see cref="ScopeSignature"/>）：变了即清空逐文件记忆。</summary>
    private string? _scopeSignature;

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
        // 解析期配置签名：文件集不变而 store 改名 / 重叠根迁移时，记录归属（Agent / Model /
        // ProjectPath）会变而文件戳一字不变——签名变化即清空记忆，绝不复用旧归属。
        var scopeSignature = ScopeSignature(scopes);
        if (_scopeSignature != scopeSignature)
        {
            _scopeSignature = scopeSignature;
            ResetMemory();
        }

        var diagnostics = new JsonlScanDiagnostics { FilesDiscovered = allFiles.Count };
        // 本轮账面变化文件数（消失 + 新增/变更 / 解析期配置变化）；stat / 读取失败的轮次不落账。
        var filesChanged = 0;

        // 先清理已消失文件的记账，与全量重扫语义一致。
        var currentFiles = new HashSet<string>(allFiles, StringComparer.Ordinal);
        foreach (var gone in _fileCache.Keys.Where(file => !currentFiles.Contains(file)).ToList())
        {
            _fileCache.Remove(gone);
            _filesWithParseErrors.Remove(gone);
            filesChanged++;
        }

        // 逐作用域对齐解析产出（只有文件戳或解析期配置变了的文件才重读重解析）。
        foreach (var scope in scopes)
        {
            RefreshScope(scope.StoreName, scope.Files, diagnostics, ref filesChanged, ct);
        }

        if (filesChanged == 0 && _lastRecords is { } cachedRecords && _lastAggregate is { } aggregate)
        {
            // 零变更轮：复用上一次的重建产物（同实例），本轮诊断只反映本次扫描事件
            // （FilesRead / Malformed / Invalid 恒 0，stat 失败的 LinesSkipped 如实保留）。
            return Task.FromResult(new JsonlScanResult
            {
                Roots = roots,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = aggregate.Status,
                    FilesDiscovered = allFiles.Count,
                    FilesRead = 0,
                    LinesSkipped = diagnostics.LinesSkipped,
                    MalformedLines = 0,
                    InvalidRecords = 0,
                    DuplicateRecords = aggregate.Duplicates,
                    MissingCostRecords = aggregate.MissingCost,
                    UnknownModelRecords = aggregate.UnknownModel,
                    FilesChanged = allFiles.Count > 0 ? 0 : null,
                },
            });
        }

        var records = new List<UsageRecord>();
        foreach (var scope in scopes)
        {
            BuildScopeRecords(scope.Files, records, diagnostics);
        }

        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = records.Count(static record => record.Model is null);
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：本轮账面变化的文件数；无文件不设值，由管道按「本轮发现即空」规则处理
        // （与 ZCode 探针同口径）。
        diagnostics.FilesChanged = allFiles.Count > 0 ? filesChanged : null;

        _lastRecords = records;
        _lastAggregate = (diagnostics.Status, diagnostics.DuplicateRecords, diagnostics.MissingCostRecords,
            diagnostics.UnknownModelRecords);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>清空全部扫描记忆（解析期配置变化时：文件可以一字未动，归属却变了）。</summary>
    private void ResetMemory()
    {
        _fileCache.Clear();
        _filesWithParseErrors.Clear();
        _lastRecords = null;
        _lastAggregate = null;
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

    /// <summary>
    /// 单作用域文件清单对齐（蓝本逐文件解析）：只有文件戳或解析期配置（store 名 / 作用域根）变了的文件
    /// 才重读重解析，未变的直接取用上轮产出。打不开 / stat 失败的文件不落账（下次重试整个文件）。
    /// </summary>
    private void RefreshScope(
        string storeName,
        List<(string File, string? StoreRoot)> files,
        JsonlScanDiagnostics diagnostics,
        ref int filesChanged,
        CancellationToken ct)
    {
        foreach (var (file, storeRoot) in files)
        {
            ct.ThrowIfCancellationRequested();

            // 文件作用域根是解析期配置的一部分（影响 ProjectPath），随缓存条目一起校验。
            var context = new PiStoreContext(storeName, storeRoot);
            var stamped = FileStampMemo.TryCapture(file, out var stamp);
            if (stamped
                && _fileCache.TryGetValue(file, out var cached)
                && cached.Stamp == stamp
                && cached.Context == context)
            {
                // 未变更：不重读不重解析（本轮无解析事件，内容诊断不累加）。
                continue;
            }

            if (!stamped)
            {
                // 戳取不到（stat 失败）：保守重读，且无论成败都不落账。
                diagnostics.LinesSkipped++;
            }

            var outcome = _parser.ParseSessionFile(file, context);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;

            if (!stamped || outcome.FilesRead == 0)
            {
                // 打不开 / stat 失败：不落账，下次扫描重试整个文件（共享扫描器同口径）。
                _fileCache.Remove(file);
                _filesWithParseErrors.Remove(file);
                continue;
            }

            _fileCache[file] = (stamp, context, outcome);
            filesChanged++;
            if (outcome.MalformedLines > 0 || outcome.InvalidRecords > 0)
            {
                _filesWithParseErrors.Add(file);
            }
            else
            {
                _filesWithParseErrors.Remove(file);
            }
        }
    }

    /// <summary>单作用域重建记录（蓝本 load_entries_from_paths：单次重放计划 + 单个去重集）。
    /// 逐个文件的数据均从逐文件记忆取（本轮的或上轮的），不重新读盘。</summary>
    private void BuildScopeRecords(
        List<(string File, string? StoreRoot)> files,
        List<UsageRecord> records,
        JsonlScanDiagnostics diagnostics)
    {
        var filePaths = new List<string>(files.Count);
        var loaded = new List<PiSessionData?>(files.Count);
        foreach (var (file, _) in files)
        {
            filePaths.Add(file);
            // 未落账的文件（本次打不开）本轮不贡献记录，下轮会重试。
            loaded.Add(_fileCache.TryGetValue(file, out var cached) ? cached.Outcome.Data : null);
        }

        var plan = PiReplayPlan.Build(filePaths, loaded);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scopeRecords = new List<UsageRecord>();
        var totalEntries = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var data = loaded[index];
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
