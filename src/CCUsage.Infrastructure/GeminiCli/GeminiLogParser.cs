using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.GeminiCli;

/// <summary>
/// Gemini CLI 单文件解析（蓝本 ../ccusage/rust/adapters/gemini/src/parser.rs 逐条移植，I-010；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。.json（整文件 JSON）与 .jsonl（行式）
/// 双格式共用一套记录信封（蓝本 GeminiRecord：仅声明 ccusage 消费的字段，其余跳过；token 块保留
/// 原始 JSON 逐键导航，因 <c>parse_tokens</c> 接受多键别名并截断浮点计数，与共享 lenient_u64 不同）：
/// <list type="bullet">
/// <item>整文件 JSON 三分支（蓝本 parse_json_file）：messages 数组 → 只取 type 恰为 "gemini"（原样、
/// 不 trim）的对象消息，按消息内 timestamp/created_at、缺省回退会话级 startTime/lastUpdated/mtime；
/// 顶层 type=="gemini" → 单条直接事件（缺时间戳回退 <b>文件 mtime</b>）；否则 stats 路径（顶层 stats，
/// 缺省回退 result.stats），models 明细逐模型一条（恒 subtract_cached_overlap 口径），models 无产出时
/// 整个 stats 当作 token 块解析、模型缺省 "unknown"，时间戳取顶层 timestamp、缺省 mtime。</item>
/// <item>JSONL 逐行（蓝本 parse_jsonl_file）：sessionId / model 跨行滚动更新；type=="gemini" 行带 id
/// 时按 id 就地替换（同 id 后行覆盖前行，蓝本 direct_event_indexes），无 id 直接追加；非 gemini 行走
/// stats（模型用滚动 current_model）。</item>
/// <item>token 口径（蓝本 parse_tokens）：input←input/prompt/input_tokens/prompt_tokens，
/// output←output/candidates/output_tokens/candidates_tokens，cached←cached/cached_tokens，
/// thoughts←thoughts/reasoning/thoughts_tokens/reasoning_tokens，tool←tool/tool_tokens，
/// total←total（键存在即优先生效，即使为 null）/total_tokens；数值接受浮点截断、负数归 0
/// （value_u64 口径，与 Amp/Droid 的整数 lenient_u64 不同）。</item>
/// <item>input 归一化：直接事件走 normalize_session_input——仅当 total 恰等于"含 cached 的四桶和"
/// （cached>0 且 total≠含 cached+cached 的和）时把 cached 从 input 拆出为 cache_read（Gemini 的
/// total 有时含 cached 有时不含，按 total 自洽性判别）；stats 事件恒走 subtract_cached_overlap
/// （input−min(input,cached)，cached 全额计 cache_read）。</item>
/// <item>total 兜底（蓝本 apply_total_token_fallback）：四桶+thoughts 不足 total 时，output 为 0
/// 补 output、否则缺口并入 thoughts（extra）。四桶与 extra 全零的事件不产出。</item>
/// </list>
/// 成本落点：统一记录 CostUsd 恒 null 交定价引擎（蓝本 model_candidates 的 provider 前缀候选计价
/// 属 I-024 端到端欠账，见卡片偏差①）；reasoning/total 余量并入 OutputTokens（蓝本 cost_usage
/// 同式，I-017 偏差②同款）。行级容错为仓库 I-004 口径（蓝本静默跳过处显式计数）。
/// </summary>
public sealed class GeminiLogParser
{
    public const string AgentName = "Gemini CLI";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Gemini"）。</summary>
    public const string ProjectPath = "Gemini";

    /// <summary>stats 兜底模型名（蓝本 DEFAULT_MODEL）。</summary>
    private const string DefaultModel = "unknown";

    /// <summary>解析单个日志文件；读取失败不计 FilesRead、计 LinesSkipped（打不开的文件，扫描器同口径）。</summary>
    public GeminiFileOutcome ParseFile(string sourceFile)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GeminiFileOutcome.Empty(linesSkipped: 1);
        }

        var fallbackTimestamp = SourceFileCollector.TryGetModifiedTimestamp(sourceFile) ?? StrictRfc3339Timestamp.Epoch;

        List<UsageRecord> records;
        var malformed = 0;
        var skipped = 0;
        var duplicates = 0;
        if (string.Equals(Path.GetExtension(sourceFile), ".jsonl", StringComparison.Ordinal))
        {
            (records, malformed, skipped, duplicates) = ParseJsonlFile(sourceFile, content, fallbackTimestamp);
        }
        else
        {
            (records, malformed, skipped) = ParseJsonFile(sourceFile, content, fallbackTimestamp);
        }

        return new GeminiFileOutcome(
            records, FilesRead: 1, LinesSkipped: skipped, MalformedLines: malformed, DuplicateRecords: duplicates);
    }

    /// <summary>整文件 JSON（蓝本 parse_json_file）。</summary>
    private static (List<UsageRecord> Records, int Malformed, int Skipped) ParseJsonFile(
        string sourceFile, string content, DateTimeOffset fallbackTimestamp)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // 蓝本 from_str 失败静默返回空（仓库口径计 malformed，显式计数偏差）。
            return ([], Malformed: 1, Skipped: 0);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // 顶层非对象：serde 结构性反序列化失败同口径（显式计数）。
                return ([], Malformed: 1, Skipped: 0);
            }

            var record = new GeminiRecord(document.RootElement);
            var sessionId = record.ResolveSessionId()
                            ?? Path.GetFileNameWithoutExtension(sourceFile)
                            ?? "unknown";
            var sessionTimestamp = TryParseTimestamp(record.StartTime)
                                   ?? TryParseTimestamp(record.LastUpdated)
                                   ?? fallbackTimestamp;

            if (record.Messages is { } messages && messages.ValueKind == JsonValueKind.Array)
            {
                var records = new List<UsageRecord>();
                var skipped = 0;
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object
                        || !IsGeminiType(message))
                    {
                        skipped++;
                        continue;
                    }

                    var fallback = ParseDirectEvent(
                        message, modelHint: null, sessionId, sessionTimestamp);
                    if (fallback is null)
                    {
                        skipped++;
                        continue;
                    }

                    records.Add(MapEvent(fallback, sourceFile));
                }

                return (records, Malformed: 0, skipped);
            }

            if (record.Type == "gemini")
            {
                // 顶层直接事件的缺省时间戳回退文件 mtime（蓝本传 fallback_timestamp，非 session_timestamp）。
                var fallback = ParseDirectEventFromRecord(
                    record, modelHint: null, sessionId, fallbackTimestamp);
                return fallback is null
                    ? ([], Malformed: 0, Skipped: 1)
                    : ([MapEvent(fallback, sourceFile)], Malformed: 0, Skipped: 0);
            }

            var statsTimestamp = TryParseTimestamp(record.Timestamp) ?? fallbackTimestamp;
            var statsEvents = ParseStatsEvents(
                record.ResolveStats(), record.Model, sessionId, statsTimestamp, out var statsSkipped);
            var mapped = statsEvents.Select(eventEntry => MapEvent(eventEntry, sourceFile)).ToList();
            // 与 JSONL 行口径一致：整个文件未产出事件（stats 缺失/null/解析为空）计 1 条 skipped。
            return (mapped, Malformed: 0, mapped.Count == 0 ? statsSkipped + 1 : statsSkipped);
        }
    }

    /// <summary>行式 JSONL（蓝本 parse_jsonl_file）：sessionId / model 跨行滚动、直接事件按 id 就地替换。</summary>
    private static (List<UsageRecord> Records, int Malformed, int Skipped, int Duplicates) ParseJsonlFile(
        string sourceFile, string content, DateTimeOffset fallbackTimestamp)
    {
        var sessionId = Path.GetFileNameWithoutExtension(sourceFile) ?? "unknown";
        string? currentModel = null;
        var events = new List<GeminiUsageEvent>();
        var eventIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var malformed = 0;
        var skipped = 0;
        var duplicates = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 蓝本 records() 对解析失败的行静默跳过（仓库口径计 malformed，显式计数偏差）。
                malformed++;
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    malformed++;
                    continue;
                }

                var record = new GeminiRecord(document.RootElement);
                if (record.ResolveSessionId() is { } recordSessionId)
                {
                    sessionId = recordSessionId;
                }

                if (record.Model is { } recordModel)
                {
                    currentModel = recordModel;
                }

                if (record.Type == "gemini")
                {
                    var fallback = ParseDirectEventFromRecord(record, currentModel, sessionId, fallbackTimestamp);
                    if (fallback is null)
                    {
                        skipped++;
                        continue;
                    }

                    if (record.Id is { } messageId)
                    {
                        if (eventIndexes.TryGetValue(messageId, out var index))
                        {
                            events[index] = fallback;
                            duplicates++;
                        }
                        else
                        {
                            eventIndexes[messageId] = events.Count;
                            events.Add(fallback);
                        }
                    }
                    else
                    {
                        events.Add(fallback);
                    }

                    continue;
                }

                var stats = record.ResolveStats();
                if (stats is null)
                {
                    skipped++;
                    continue;
                }

                var statsTimestamp = TryParseTimestamp(record.Timestamp) ?? fallbackTimestamp;
                var statsEvents = ParseStatsEvents(stats, currentModel, sessionId, statsTimestamp, out var statsSkipped);
                if (statsEvents.Count == 0)
                {
                    skipped += 1 + statsSkipped;
                    continue;
                }

                events.AddRange(statsEvents);
            }
        }

        var records = events.Select(eventEntry => MapEvent(eventEntry, sourceFile)).ToList();
        return (records, malformed, skipped, duplicates);
    }

    /// <summary>messages 数组内的直接事件（蓝本 parse_direct_event，原始对象导航）。</summary>
    private static GeminiUsageEvent? ParseDirectEvent(
        JsonElement message, string? modelHint, string sessionId, DateTimeOffset fallbackTimestamp)
    {
        var tokens = ParseTokens(TryGetProperty(message, "tokens"));
        if (tokens is null)
        {
            return null;
        }

        var model = LenientJson.GetNonEmptyString(message, "model") ?? modelHint;
        var timestamp = TryParseTimestamp(RawString(message, "timestamp"))
                        ?? TryParseTimestamp(RawString(message, "created_at"))
                        ?? fallbackTimestamp;
        return BuildEvent(
            model,
            sessionId,
            timestamp,
            tokens.Value,
            NormalizeSessionInput,
            LenientJson.GetNonEmptyString(message, "id"));
    }

    /// <summary>信封记录的直接事件（蓝本 parse_direct_event_record）：缺省时间戳回退 mtime。</summary>
    private static GeminiUsageEvent? ParseDirectEventFromRecord(
        GeminiRecord record, string? modelHint, string sessionId, DateTimeOffset fallbackTimestamp)
    {
        var tokens = ParseTokens(record.Tokens);
        if (tokens is null)
        {
            return null;
        }

        var timestamp = TryParseTimestamp(record.Timestamp)
                        ?? TryParseTimestamp(record.CreatedAt)
                        ?? fallbackTimestamp;
        return BuildEvent(
            record.Model ?? modelHint,
            sessionId,
            timestamp,
            tokens.Value,
            NormalizeSessionInput,
            record.Id);
    }

    /// <summary>stats 事件（蓝本 parse_stats_events）：models 明细优先，无产出回退整个 stats 当 token 块。</summary>
    private static List<GeminiUsageEvent> ParseStatsEvents(
        JsonElement? stats, string? modelHint, string sessionId, DateTimeOffset timestamp, out int skipped)
    {
        skipped = 0;
        if (stats is not { } statsElement || statsElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var events = new List<GeminiUsageEvent>();
        var modelsSkipped = 0;
        if (LenientJson.GetObject(statsElement, "models") is { } models)
        {
            foreach (var modelEntry in models.EnumerateObject())
            {
                if (modelEntry.Value.ValueKind != JsonValueKind.Object)
                {
                    modelsSkipped++;
                    continue;
                }

                var tokens = ParseTokens(TryGetProperty(modelEntry.Value, "tokens"));
                if (tokens is null)
                {
                    modelsSkipped++;
                    continue;
                }

                var fallback = BuildEvent(
                    modelEntry.Name,
                    sessionId,
                    timestamp,
                    tokens.Value,
                    SubtractCachedOverlapTokens,
                    messageId: null);
                if (fallback is null)
                {
                    modelsSkipped++;
                    continue;
                }

                events.Add(fallback);
            }

            if (events.Count > 0)
            {
                return events;
            }
        }

        // models 无产出（或缺失）：整个 stats 对象当作 token 块解析，模型缺省 "unknown"。
        var wholeStatsTokens = ParseTokens(statsElement);
        if (wholeStatsTokens is null)
        {
            skipped = modelsSkipped;
            return [];
        }

        var wholeStatsEvent = BuildEvent(
            modelHint ?? DefaultModel,
            sessionId,
            timestamp,
            wholeStatsTokens.Value,
            SubtractCachedOverlapTokens,
            messageId: null);
        if (wholeStatsEvent is null)
        {
            skipped = modelsSkipped + 1;
            return [];
        }

        return [wholeStatsEvent];
    }

    private static GeminiUsageEvent? BuildEvent(
        string? model,
        string sessionId,
        DateTimeOffset timestamp,
        GeminiTokens tokens,
        Func<GeminiTokens, (long InputWithoutCache, long CacheRead)> normalizeInput,
        string? messageId)
    {
        if (model is null || model.Trim().Length == 0)
        {
            return null;
        }

        var (inputWithoutCache, cacheRead) = normalizeInput(tokens);
        var inputTokens = RawTokenUsage.SaturatingAdd(inputWithoutCache, tokens.Tool);
        var totalTokens = tokens.Total
                          ?? RawTokenUsage.SaturatingAdd(
                              RawTokenUsage.SaturatingAdd(
                                  RawTokenUsage.SaturatingAdd(inputTokens, tokens.Output), cacheRead),
                              tokens.Thoughts);
        var displayUsage = new RawTokenUsage(inputTokens, tokens.Output, 0, cacheRead);
        var (usage, extraTotalTokens) = TokenUsageFallback.Apply(displayUsage, tokens.Thoughts, totalTokens);
        if (usage.InputTokens == 0 && usage.OutputTokens == 0
            && usage.CacheReadTokens == 0 && extraTotalTokens == 0)
        {
            return null;
        }

        return new GeminiUsageEvent(
            timestamp,
            sessionId,
            model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            extraTotalTokens,
            messageId);
    }

    /// <summary>token 块解析（蓝本 parse_tokens）：多键别名、首个可读键生效；total 键存在即优先。</summary>
    private static GeminiTokens? ParseTokens(JsonElement? value)
    {
        if (value is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new GeminiTokens(
            Input: TokenNumber(element, "input", "prompt", "input_tokens", "prompt_tokens"),
            Output: TokenNumber(element, "output", "candidates", "output_tokens", "candidates_tokens"),
            Cached: TokenNumber(element, "cached", "cached_tokens"),
            Thoughts: TokenNumber(element, "thoughts", "reasoning", "thoughts_tokens", "reasoning_tokens"),
            Tool: TokenNumber(element, "tool", "tool_tokens"),
            Total: TotalValue(element));
    }

    private static long TokenNumber(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && ValueU64(value) is { } parsed)
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>total（蓝本 value_u64(record.get("total").or_else(get("total_tokens"))）：total 键
    /// 存在即优先（即使为 null，不再回退 total_tokens——Option 链同口径）。</summary>
    private static long? TotalValue(JsonElement element)
    {
        if (element.TryGetProperty("total", out var total))
        {
            return ValueU64(total);
        }

        return element.TryGetProperty("total_tokens", out var totalTokens) ? ValueU64(totalTokens) : null;
    }

    /// <summary>value_u64：浮点截断、负数归 0、非有限为 null；u64 域钳到 long（saturating，偏差⑥）。</summary>
    private static long? ValueU64(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var parsed = value.GetDouble();
        if (!double.IsFinite(parsed))
        {
            return null;
        }

        if (parsed <= 0)
        {
            return 0;
        }

        if (parsed >= 9223372036854775808.0)
        {
            return long.MaxValue;
        }

        return (long)parsed;
    }

    /// <summary>直接事件的 input 归一化（蓝本 normalize_session_input）：total 自洽性判别 cached 是否已含于 input。</summary>
    private static (long InputWithoutCache, long CacheRead) NormalizeSessionInput(GeminiTokens tokens)
    {
        var inclusiveTotal = RawTokenUsage.SaturatingAdd(
            RawTokenUsage.SaturatingAdd(
                RawTokenUsage.SaturatingAdd(tokens.Input, tokens.Output), tokens.Thoughts),
            tokens.Tool);
        var exclusiveTotal = RawTokenUsage.SaturatingAdd(inclusiveTotal, tokens.Cached);
        if (tokens.Cached > 0 && tokens.Total == inclusiveTotal && tokens.Total != exclusiveTotal)
        {
            return SubtractCachedOverlapTokens(tokens);
        }

        return (tokens.Input, tokens.Cached);
    }

    /// <summary>stats 事件的 input 归一化（蓝本 subtract_cached_overlap_tokens）：input − min(input, cached)。</summary>
    private static (long InputWithoutCache, long CacheRead) SubtractCachedOverlapTokens(GeminiTokens tokens)
    {
        var cachedPortion = Math.Min(tokens.Input, tokens.Cached);
        return (tokens.Input - cachedPortion, tokens.Cached);
    }

    /// <summary>统一记录映射（蓝本 event_to_loaded）：CostUsd 恒 null 交定价引擎；reasoning/total 余量
    /// 并入 OutputTokens（蓝本 cost_usage 同式）；MessageId 置 null——蓝本 id 仅用于文件内就地替换去重，
    /// 置空防共享去重器跨文件误并（Copilot 同款）。</summary>
    private static UsageRecord MapEvent(GeminiUsageEvent eventEntry, string sourceFile)
    {
        return new UsageRecord
        {
            Timestamp = eventEntry.Timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = eventEntry.SessionId,
            Model = eventEntry.Model,
            CostUsd = null,
            InputTokens = eventEntry.InputTokens,
            OutputTokens = eventEntry.OutputTokens.SaturatingAddClamped(eventEntry.ReasoningTokens),
            CacheCreationTokens = 0,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = eventEntry.CacheReadTokens,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = sourceFile,
        };
    }

    /// <summary>type 判别（蓝本 lenient_str 原样字符串，比较不得 trim：" gemini " 不命中）。</summary>
    private static bool IsGeminiType(JsonElement element)
        => RawString(element, "type") == "gemini";

    /// <summary>lenient_str：字符串原样返回（不 trim），非字符串为 null。</summary>
    private static string? RawString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? TryGetProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;

    private static DateTimeOffset? TryParseTimestamp(string? value)
        => value is not null && StrictRfc3339Timestamp.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>一条 Gemini usage 事件（蓝本 GeminiUsageEvent；四桶为显示口径，ReasoningTokens 为
    /// total 兜底并入 extra 的余量）。</summary>
    private sealed record GeminiUsageEvent(
        DateTimeOffset Timestamp,
        string SessionId,
        string Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long ReasoningTokens,
        string? MessageId);

    /// <summary>token 块解析产物（蓝本 GeminiTokens；Total 为 null 表示源数据未给 total）。</summary>
    private readonly record struct GeminiTokens(
        long Input,
        long Output,
        long Cached,
        long Thoughts,
        long Tool,
        long? Total);

    /// <summary>蓝本 GeminiRecord 信封的 JsonElement 导航视图：字段语义见类型注释。</summary>
    private readonly struct GeminiRecord(JsonElement element)
    {
        /// <summary>type 原样字符串（不 trim）。</summary>
        public string? Type => RawString(element, "type");

        public string? Id => LenientJson.GetNonEmptyString(element, "id");

        public string? Model => LenientJson.GetNonEmptyString(element, "model");

        public string? Timestamp => RawString(element, "timestamp");

        public string? CreatedAt => RawString(element, "created_at");

        public string? StartTime => RawString(element, "startTime");

        public string? LastUpdated => RawString(element, "lastUpdated");

        public JsonElement? Messages => TryGetProperty(element, "messages");

        public JsonElement? Tokens => TryGetProperty(element, "tokens");

        public string? ResolveSessionId()
            => LenientJson.GetNonEmptyString(element, "sessionId")
               ?? LenientJson.GetNonEmptyString(element, "session_id");

        /// <summary>stats 解析（蓝本 stats()）：顶层 stats 为 Option&lt;Value&gt;——serde 对 JSON null
        /// 反序列化为 None，故 "stats": null 同缺失，<b>回退 result.stats</b>；result.stats 走
        /// Value::get（返回 Some(&amp;Null)），null 不再二次回退（解析为空，与蓝本同）。</summary>
        public JsonElement? ResolveStats()
        {
            if (TryGetProperty(element, "stats") is { } stats
                && stats.ValueKind != JsonValueKind.Null)
            {
                return stats;
            }

            return TryGetProperty(element, "result") is { } result
                   && result.ValueKind == JsonValueKind.Object
                   && result.TryGetProperty("stats", out var nested)
                   && nested.ValueKind != JsonValueKind.Null
                ? nested
                : (JsonElement?)null;
        }
    }
}

/// <summary>单文件解析产出：记录 + 诊断计数（InvalidRecords 对 Gemini 无对应拒绝场景，恒 0）。</summary>
public sealed record GeminiFileOutcome(
    List<UsageRecord> Records,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int DuplicateRecords)
{
    public static GeminiFileOutcome Empty(int linesSkipped)
        => new([], FilesRead: 0, linesSkipped, MalformedLines: 0, DuplicateRecords: 0);
}
