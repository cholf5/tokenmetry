using System.Globalization;
using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.PiAgent;

/// <summary>单文件解析的作用域上下文：store 名（记录 Agent 与模型前缀）+ 命名存储根（项目名提取用，
/// 默认作用域为 null）。</summary>
public sealed record PiStoreContext(string StoreName, string? StoreRoot);

/// <summary>会话头（蓝本 PiSessionHeader）：parentSession 文本、是否畸形（键存在但非非空字符串）、
/// 严格 RFC3339 时间戳。</summary>
public sealed record PiSessionHeader(string? ParentSession, bool ParentSessionIsMalformed, DateTimeOffset? Timestamp);

/// <summary>
/// 重放签名（蓝本 PiUsageSignature 逐字段移植）：时间戳时刻、<b>裸</b>模型名、total 兜底后的四桶、
/// 有效 total（四桶和 + extra 余量）、有效成本（存储成本缺省时按内置快照现算——蓝本 billed_cost_bits 的
/// auto 口径，见卡片偏差③）。值相等语义。
/// </summary>
public readonly record struct PiUsageSignature(
    DateTimeOffset Timestamp,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long EffectiveTotalTokens,
    decimal BilledCost);

/// <summary>单条 usage 记录：签名 + 消息行 id（树形活动分支回溯用，蓝本 usage_by_id 键）。</summary>
public sealed record PiUsageRecord(PiUsageSignature Signature, string? LinkId);

/// <summary>重放分支路径（蓝本 PiReplayUsagePath）：Linear（v1 无链接）、Active（叶子回溯的活动分支
/// usage 下标序列，物理序）、Invalid（部分链接 / 重复 id / 悬挂引用 / 环）。</summary>
public abstract record PiReplayPath
{
    public sealed record Linear : PiReplayPath;

    public sealed record Active(IReadOnlyList<int> Indices) : PiReplayPath;

    public sealed record Invalid : PiReplayPath;
}

/// <summary>单条解析产出：统一记录 + 复合 entry id（跨文件 first-wins 去重键，蓝本 entry_id_for_store）。</summary>
public sealed record PiEntry(UsageRecord Record, string CompositeId);

/// <summary>
/// pi 会话文件解析（蓝本 ../ccusage/rust/adapters/pi/src/parser.rs 逐条移植，I-019；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。行式 JSONL，三遍读取：
/// <list type="bullet">
/// <item>首行会话头（蓝本 parse_session_header）：type 恰为 "session"，parentSession（non_empty_string）、
/// 畸形标记（键存在但非「非空字符串」）、timestamp 严格 RFC3339。</item>
/// <item>usage 行（蓝本 jsonl::records + is_pi_message_usage）：行预过滤须<b>同时</b>含 "usage" 与
/// "message" 片段；type 缺省放行、非 "message" 拒绝；message/usage 须为对象（null 视为缺失；标量/数组为
/// serde 结构性失败整行拒绝——仓库口径计 Malformed）；role 恰为 "assistant"；token 五桶 lenient_u64
/// （input/output/cacheRead/cacheWrite/totalTokens）；total 兜底；cost 为 lenient 对象（非对象不拒行，
/// 蓝本 keeps_record_when_cost_is_not_an_object）；时间戳严格 RFC3339。</item>
/// <item>链接行（蓝本 replay_usage_path 第二遍）：全行（无预过滤）解析 type/id/parentId，type 恰为
/// "session" 的行排除；全部无 id/parentId → Linear；逐链接校验后自叶子回溯 parentId 得活动分支下标序。</item>
/// </list>
/// 记录映射：Agent = store 名（默认 "pi"，命名存储为 store 名——蓝本命名存储作为独立 agent 出账）；
/// 模型 = "[{store}] {裸模型}"（裸模型缺失为 null）；SessionId = 文件名去扩展名后首个 '_' 之后的部分；
/// 项目名 = 路径中首个 "sessions" 段的后一段（默认作用域）/ 相对存储根的首段（命名作用域）；
/// CostUsd = usage.cost.total（有限且 ≥0，CLI auto 口径；负值/NaN/Inf 交引擎重算，见卡片偏差②）。
/// 行级容错为仓库 I-004 口径（蓝本静默跳过处显式计数）。
/// </summary>
public sealed class PiSessionParser
{
    /// <summary>与蓝本 LinePrefilter::all(["\"usage\"", "\"message\""]) 一致：两片段齐备才进入解析。</summary>
    private const string UsageMarker = "\"usage\"";
    private const string MessageMarker = "\"message\"";

    private readonly Func<UsageRecord, decimal> _identityCostResolver;

    /// <summary>
    /// <paramref name="identityCostResolver"/> 为重放签名与复合 entry id 的「有效成本」解析
    /// （蓝本 calculate → entry.cost：存储成本缺省时按定价表现算）。缺省用内置快照引擎
    /// （<see cref="PricingEngine"/>，离线确定性；未定价模型记 0——与蓝本空 PricingMap 同塌缩语义）。
    /// </summary>
    public PiSessionParser(Func<UsageRecord, decimal>? identityCostResolver = null)
    {
        _identityCostResolver = identityCostResolver ?? (record =>
        {
            var engine = new PricingEngine();
            return engine.PriceRecord(record).CostUsd ?? 0m;
        });
    }

    /// <summary>解析单个会话文件（蓝本 read_session_file_data_with_context）。读取失败计 LinesSkipped、
    /// 不计 FilesRead；坏 JSON / 顶层非对象 / message·usage 非对象计 MalformedLines。</summary>
    public PiFileOutcome ParseSessionFile(string sourceFile, PiStoreContext context)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PiFileOutcome.Empty(linesSkipped: 1);
        }

        var lines = SplitLines(content);
        var header = ParseSessionHeader(lines.Count > 0 ? lines[0] : null);
        var project = context.StoreRoot is { } storeRoot
            ? ExtractProjectForStore(sourceFile, storeRoot)
            : ExtractProject(sourceFile);
        var sessionId = ExtractSessionId(sourceFile);

        var entries = new List<PiEntry>();
        var usages = new List<PiUsageRecord>();
        var skipped = 0;
        var malformed = 0;
        var invalid = 0;

        foreach (var line in lines)
        {
            if (!line.Contains(UsageMarker, StringComparison.Ordinal)
                || !line.Contains(MessageMarker, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || (TryGet(root, "message") is { } messageElement
                        && messageElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    || (LenientJson.GetObject(root, "message") is { } messageObject
                        && TryGet(messageObject, "usage") is { } usageElement
                        && usageElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)))
                {
                    // serde 单次 from_slice::<PiLine>：message/usage 为标量/数组时整行反序列化失败。
                    malformed++;
                    continue;
                }

                if (!IsPiMessageUsage(root, out var message))
                {
                    skipped++;
                    continue;
                }

                var timestampText = LenientJson.GetNonEmptyString(root, "timestamp");
                if (timestampText is null || !StrictRfc3339Timestamp.TryParse(timestampText, out var timestamp))
                {
                    // 缺失/不可解析时间戳：区间无法归日（仓库口径计 invalid，Amp/Copilot 同款）。
                    invalid++;
                    continue;
                }

                var usageElement2 = LenientJson.GetObject(message, "usage")!.Value;
                var usage = new RawTokenUsage(
                    LenientJson.GetU64(usageElement2, "input"),
                    LenientJson.GetU64(usageElement2, "output"),
                    LenientJson.GetU64(usageElement2, "cacheWrite"),
                    LenientJson.GetU64(usageElement2, "cacheRead"));
                var (resolvedUsage, extraTotalTokens) = TokenUsageFallback.Apply(
                    usage, 0, LenientJson.GetU64(usageElement2, "totalTokens"));
                if (resolvedUsage.TotalUsageTokens() + extraTotalTokens == 0)
                {
                    skipped++;
                    continue;
                }

                var rawModel = LenientJson.GetNonEmptyString(message, "model");
                var displayModel = rawModel is null ? null : $"[{context.StoreName}] {rawModel}";
                var displayCost = LenientJson.GetObject(usageElement2, "cost") is { } costObject
                    ? LenientJson.GetF64(costObject, "total")
                    : null;
                var storedCost = displayCost is { } cost && double.IsFinite(cost) && cost >= 0.0
                    ? (decimal?)cost
                    : null;

                var record = new UsageRecord
                {
                    Timestamp = timestamp,
                    Agent = context.StoreName,
                    ProjectPath = project,
                    SessionId = sessionId,
                    Model = displayModel,
                    CostUsd = storedCost,
                    InputTokens = resolvedUsage.InputTokens,
                    OutputTokens = resolvedUsage.OutputTokens.SaturatingAddClamped(extraTotalTokens),
                    CacheCreationTokens = resolvedUsage.CacheCreationTokens,
                    CacheCreationEphemeral5mTokens = 0,
                    CacheCreationEphemeral1hTokens = 0,
                    CacheReadTokens = resolvedUsage.CacheReadTokens,
                    IsSidechain = false,
                    MessageId = null,
                    RequestId = null,
                    SourceFile = sourceFile,
                };
                var effectiveCost = storedCost ?? _identityCostResolver(record);
                var signature = new PiUsageSignature(
                    timestamp,
                    rawModel,
                    resolvedUsage.InputTokens,
                    resolvedUsage.OutputTokens,
                    resolvedUsage.CacheCreationTokens,
                    resolvedUsage.CacheReadTokens,
                    resolvedUsage.TotalUsageTokens().SaturatingAddClamped(extraTotalTokens),
                    effectiveCost);
                var compositeId = string.Join(":",
                    context.StoreName,
                    project,
                    sessionId,
                    timestampText,
                    displayModel ?? string.Empty,
                    resolvedUsage.InputTokens.ToString(CultureInfo.InvariantCulture),
                    resolvedUsage.OutputTokens.ToString(CultureInfo.InvariantCulture),
                    resolvedUsage.CacheCreationTokens.ToString(CultureInfo.InvariantCulture),
                    resolvedUsage.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                    extraTotalTokens.ToString(CultureInfo.InvariantCulture),
                    effectiveCost.ToString(CultureInfo.InvariantCulture));
                usages.Add(new PiUsageRecord(signature, LenientJson.GetNonEmptyString(root, "id")));
                entries.Add(new PiEntry(record, compositeId));
            }
        }

        var replayPath = ComputeReplayUsagePath(lines, usages);
        return new PiFileOutcome(
            new PiSessionData(header, usages, replayPath, entries),
            FilesRead: 1,
            LinesSkipped: skipped,
            MalformedLines: malformed,
            InvalidRecords: invalid);
    }

    /// <summary>会话头（蓝本 parse_session_header）：首行 JSON、type 恰为 "session"。</summary>
    internal static PiSessionHeader? ParseSessionHeader(string? firstLine)
    {
        if (firstLine is null)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(firstLine);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || LenientJson.GetNonEmptyString(root, "type") != "session")
            {
                return null;
            }

            var parentMalformed = root.TryGetProperty("parentSession", out var parentValue)
                                  && !(parentValue.ValueKind == JsonValueKind.String
                                       && !string.IsNullOrWhiteSpace(parentValue.GetString()));
            var parentSession = parentMalformed ? null : LenientJson.GetNonEmptyString(root, "parentSession");
            var timestampText = LenientJson.GetNonEmptyString(root, "timestamp");
            DateTimeOffset? timestamp = timestampText is not null
                                        && StrictRfc3339Timestamp.TryParse(timestampText, out var parsed)
                ? parsed
                : null;
            return new PiSessionHeader(parentSession, parentMalformed, timestamp);
        }
    }

    /// <summary>usage 行判定（蓝本 is_pi_message_usage）：type 缺省放行、非 "message" 拒绝；
    /// message 对象、role 恰为 "assistant"、usage 对象存在。</summary>
    private static bool IsPiMessageUsage(JsonElement root, out JsonElement message)
    {
        message = default;
        var type = LenientJson.GetNonEmptyString(root, "type");
        if (type is not null && type != "message")
        {
            return false;
        }

        if (LenientJson.GetObject(root, "message") is not { } messageElement)
        {
            return false;
        }

        message = messageElement;
        if (LenientJson.GetNonEmptyString(messageElement, "role") != "assistant")
        {
            return false;
        }

        return LenientJson.GetObject(messageElement, "usage") is not null;
    }

    /// <summary>重放分支路径（蓝本 replay_usage_path 逐条移植）。</summary>
    internal static PiReplayPath ComputeReplayUsagePath(List<string> lines, List<PiUsageRecord> usages)
    {
        var links = new List<(string? Id, string? ParentId)>();
        foreach (var line in lines)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (LenientJson.GetNonEmptyString(root, "type") == "session")
                {
                    continue;
                }

                links.Add((LenientJson.GetNonEmptyString(root, "id"), LenientJson.GetNonEmptyString(root, "parentId")));
            }
        }

        if (links.All(link => link.Id is null && link.ParentId is null))
        {
            // v1 会话先于 entry 链接存在，物理顺序即唯一可用线性历史。
            return new PiReplayPath.Linear();
        }

        var parentsById = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? leafId = null;
        foreach (var (id, parentId) in links)
        {
            if (id is null)
            {
                // 部分链接的会话无法安全识别活动分支。
                return new PiReplayPath.Invalid();
            }

            if (!parentsById.TryAdd(id, parentId))
            {
                return new PiReplayPath.Invalid();
            }

            leafId = id;
        }

        if (leafId is null || parentsById.Values.Any(parent => parent is not null && !parentsById.ContainsKey(parent)))
        {
            return new PiReplayPath.Invalid();
        }

        if (HasCyclicEntryLinks(parentsById))
        {
            return new PiReplayPath.Invalid();
        }

        var usageById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < usages.Count; index++)
        {
            if (usages[index].LinkId is { } linkId)
            {
                usageById[linkId] = index;
            }
        }

        var active = new List<int>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var entryId = leafId;
        while (true)
        {
            if (!visited.Add(entryId))
            {
                return new PiReplayPath.Invalid();
            }

            if (usageById.TryGetValue(entryId, out var usageIndex))
            {
                active.Add(usageIndex);
            }

            if (!parentsById.TryGetValue(entryId, out var parentHolder))
            {
                return new PiReplayPath.Invalid();
            }

            if (parentHolder is null)
            {
                break;
            }

            entryId = parentHolder;
        }

        active.Reverse();
        return new PiReplayPath.Active(active);
    }

    /// <summary>链接环检测（蓝本 has_cyclic_entry_links）。</summary>
    private static bool HasCyclicEntryLinks(Dictionary<string, string?> parentsById)
    {
        var validated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in parentsById.Keys)
        {
            if (validated.Contains(start))
            {
                continue;
            }

            var path = new HashSet<string>(StringComparer.Ordinal);
            var entryId = start;
            while (!validated.Contains(entryId))
            {
                if (!path.Add(entryId))
                {
                    return true;
                }

                if (!parentsById.TryGetValue(entryId, out var parentHolder))
                {
                    return true;
                }

                if (parentHolder is null)
                {
                    break;
                }

                entryId = parentHolder;
            }

            validated.UnionWith(path);
        }

        return false;
    }

    /// <summary>会话 id（蓝本 extract_session_id）：文件名去扩展名后首个 '_' 之后的部分（无 '_' 取全名）。</summary>
    internal static string ExtractSessionId(string sourceFile)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var underscore = stem.IndexOf('_');
        return underscore >= 0 ? stem[(underscore + 1)..] : stem;
    }

    /// <summary>项目名（蓝本 extract_project）：路径段中首个 "sessions" 段的后一段；无则 "unknown"。</summary>
    internal static string ExtractProject(string sourceFile)
    {
        var previousWasSessions = false;
        foreach (var segment in SplitComponents(Path.GetFullPath(sourceFile)))
        {
            if (previousWasSessions)
            {
                return segment;
            }

            previousWasSessions = segment == "sessions";
        }

        return "unknown";
    }

    /// <summary>命名存储项目名（蓝本 extract_project_for_store）：相对存储根的首段；回退默认提取。</summary>
    internal static string ExtractProjectForStore(string sourceFile, string storeRoot)
    {
        var fullFile = Path.GetFullPath(sourceFile);
        var fullRoot = Path.GetFullPath(storeRoot);
        if (fullFile.StartsWith(fullRoot, StringComparison.Ordinal)
            && fullFile.Length > fullRoot.Length
            && (fullFile[fullRoot.Length] == Path.DirectorySeparatorChar
                || fullFile[fullRoot.Length] == Path.AltDirectorySeparatorChar))
        {
            var relative = fullFile[(fullRoot.Length + 1)..];
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var firstSegment = separator >= 0 ? relative[..separator] : relative;
            if (firstSegment.Length > 0)
            {
                return firstSegment;
            }
        }

        return ExtractProject(sourceFile);
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static string[] SplitComponents(string path)
    {
        return path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static JsonElement? TryGet(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
}

/// <summary>单会话文件解析产出（蓝本 PiSessionData）：头、usage 签名序列、重放分支路径、条目
/// （与 usage 序列 1:1 平行）。</summary>
public sealed record PiSessionData(
    PiSessionHeader? Header,
    List<PiUsageRecord> Usages,
    PiReplayPath ReplayPath,
    List<PiEntry> Entries)
{
    /// <summary>子会话可抑制前缀长度（蓝本 matching_replay_prefix）：父序列取 fork 时间戳之前的部分与
    /// 子序列逐签名比对，首异即止；任一侧分支路径 Invalid 时不抑制（返回 null）。</summary>
    public int? MatchingReplayPrefix(PiSessionData child, DateTimeOffset forkTimestamp)
    {
        if (child.ReplayPath is PiReplayPath.Invalid || ReplayPath is PiReplayPath.Invalid)
        {
            return null;
        }

        var parentSequence = ReplayPath switch
        {
            PiReplayPath.Linear => Usages.Select(usage => usage.Signature),
            PiReplayPath.Active active => active.Indices.Select(index => Usages[index].Signature),
            _ => [],
        };
        var matched = 0;
        using var parentEnumerator = parentSequence
            .TakeWhile(usage => usage.Timestamp <= forkTimestamp)
            .GetEnumerator();
        using var childEnumerator = child.Usages.Select(usage => usage.Signature).GetEnumerator();
        while (parentEnumerator.MoveNext() && childEnumerator.MoveNext())
        {
            if (parentEnumerator.Current != childEnumerator.Current)
            {
                break;
            }

            matched++;
        }

        return matched;
    }
}

/// <summary>单会话文件解析产出 + 诊断计数（DuplicateRecords 无对应场景，恒 0）。</summary>
public sealed record PiFileOutcome(
    PiSessionData? Data,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords)
{
    public static PiFileOutcome Empty(int linesSkipped)
        => new(null, FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0);
}
