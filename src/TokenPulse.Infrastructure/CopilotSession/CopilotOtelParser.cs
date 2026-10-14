using System.Globalization;
using System.Text.Json;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.CopilotSession;

/// <summary>
/// Copilot CLI OpenTelemetry JSONL 逐文件解析（蓝本 ../ccusage/rust/adapters/copilot/src/parser.rs
/// parse_otel_file 逐条移植，I-023）。蓝本许可：MIT License，Copyright (c) 2025 ryoppippi
/// （../ccusage/LICENSE）。
/// <list type="bullet">
/// <item><b>四种记录形态</b>（判别按蓝本 if/else 链，互斥）：chat span（span 且
/// gen_ai.operation.name=chat 或 name 以 "chat " 开头）&gt; inference log（非 span 且
/// event.name=gen_ai.client.inference.operation.details 或 body 以 "GenAI inference:" 开头）&gt;
/// agent turn log（非 span 且 event.name=copilot_chat.agent.turn 或 body 前缀）&gt; agent summary
/// span（span 且 invoke_agent）。span 判别：type="span"，或（有非空 name 且 spanId/traceId/
/// startTime/endTime/duration/kind 任一存在）。</item>
/// <item><b>trace 上下文</b>：同 traceId 的记录共享模型与会话回退——模型取该 trace 首个非空
/// gen_ai.response.model / gen_ai.request.model；会话按属性优先级取最高（conversation.id /
/// copilot_chat.session_id / copilot_chat.chat_session_id / session.id = 3，interaction_id = 2，
/// gen_ai.response.id = 1；同优先级取先出现记录、同记录内取后声明属性——蓝本 max_by_key 尾部胜出）。</item>
/// <item><b>互斥去重</b>（蓝本 CandidateSets）：chat span &gt; inference log &gt; agent turn &gt;
/// agent summary，低优先级记录在同 traceId 或同 gen_ai.response.id 命中高优先级集合时被抑制；
/// chat span 双导出（同 response id + 同用量八元组）计一次，保留时间戳更早者。</item>
/// <item><b>token 口径</b>：gen_ai.usage.input_tokens 含缓存读写（拆分同 session-state：uncached =
/// input − cache_read − cache_write，cache_write 键优先于 cache_creation 键）；reasoning 另记；
/// 仅当 total_tokens 显示 reasoning 未含在桶内时经 apply_total_token_fallback 补 extra（extra 不落
/// 统一记录，见卡片偏差清单）。总用量为 0 的候选不产出。</item>
/// <item><b>时间戳</b>：endTime → startTime → hrTime → _hrTime → time（[秒, 纳秒] 数组）→
/// timestamp → observedTimestamp（标量按数量级猜测秒/毫秒/微秒/纳秒）→ timeUnixNano（纳秒）；
/// 全部缺失回退文件 mtime（蓝本 file_modified_timestamp，再回退当前时刻）。</item>
/// <item><b>会话 / 模型</b>：自身属性优先，缺省回退 trace 上下文，再回退 traceId / "unknown" /
/// "unknown-session"（蓝本同序）。</item>
/// </list>
/// 行级容错为仓库 I-004 口径（蓝本 records() 静默跳过）：坏 JSON / 顶层级非对象 / attributes 键存在
/// 但非对象（serde 结构性失败）计 MalformedLines；预过滤未命中（行内无 "attributes" 字面量）、
/// 未归类四形态、无 attributes、总用量为 0 计 LinesSkipped。时间戳有 mtime 兜底，InvalidRecords 恒 0。
/// </summary>
public sealed class CopilotOtelParser
{
    public const string AgentName = CopilotSessionParser.AgentName;

    /// <summary>与蓝本 LinePrefilter::all([r#""attributes""#]) 一致：可用记录必带 attributes 对象。</summary>
    private const string AttributesMarker = "\"attributes\"";

    /// <summary>蓝本 MODEL_ATTRS：模型属性候选（首个非空生效，normalize 后缀剥离）。</summary>
    private static readonly string[] ModelAttributes =
        ["gen_ai.response.model", "gen_ai.request.model"];

    /// <summary>蓝本 SESSION_ATTRS：会话属性候选与优先级（同优先级取后声明，蓝本 max_by_key）。</summary>
    private static readonly (string Key, byte Priority)[] SessionAttributes =
    [
        ("gen_ai.conversation.id", 3),
        ("copilot_chat.session_id", 3),
        ("copilot_chat.chat_session_id", 3),
        ("session.id", 3),
        ("github.copilot.interaction_id", 2),
        ("gen_ai.response.id", 1),
    ];

    private readonly Func<string, DateTimeOffset> _fallbackTimestamp;

    public CopilotOtelParser(Func<string, DateTimeOffset>? fallbackTimestamp = null)
    {
        _fallbackTimestamp = fallbackTimestamp
            ?? (path =>
            {
                try
                {
                    return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return DateTimeOffset.UtcNow;
                }
            });
    }

    /// <summary>解析一个 otel jsonl 文件，产出互斥去重后的逐调用用量行。</summary>
    public Task<CopilotOtelFileParse> ParseAsync(TextReader reader, string sourceFile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var linesSkipped = 0;
        var malformedLines = 0;
        var records = new List<(int Index, JsonElement Element)>();

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.Contains(AttributesMarker, StringComparison.Ordinal))
            {
                linesSkipped++;
                continue;
            }

            JsonElement element;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                malformedLines++;
                continue;
            }

            // serde 结构性失败（顶层级非对象 / attributes 键存在但非对象）：整行丢弃。
            if (element.ValueKind != JsonValueKind.Object)
            {
                malformedLines++;
                continue;
            }

            if (element.TryGetProperty("attributes", out var attributes)
                && attributes.ValueKind != JsonValueKind.Object
                && attributes.ValueKind != JsonValueKind.Null)
            {
                malformedLines++;
                continue;
            }

            records.Add((records.Count, element));
        }

        var contexts = CollectTraceContexts(records);
        var fallbackTimestamp = _fallbackTimestamp(sourceFile);
        var candidates = new List<CopilotOtelCandidate>();
        foreach (var (index, record) in records)
        {
            if (TryBuildCandidate(record, index, fallbackTimestamp, contexts, sourceFile) is { } candidate)
            {
                candidates.Add(candidate);
            }
            else
            {
                linesSkipped++;
            }
        }

        var sets = CandidateSets.Build(candidates);
        var entries = new List<CopilotOtelEntry>();
        var candidateIndex = 0;
        foreach (var candidate in candidates)
        {
            if (ShouldEmit(candidateIndex, candidate, sets))
            {
                entries.Add(new CopilotOtelEntry(
                    candidate.Timestamp, candidate.SessionId, candidate.Model,
                    candidate.InputTokens, candidate.OutputTokens,
                    candidate.CacheCreationTokens, candidate.CacheReadTokens,
                    candidate.ReasoningOutputTokens, candidate.ExtraTotalTokens,
                    candidate.DedupKey, sourceFile));
            }

            candidateIndex++;
        }

        var counters = new CopilotFileParseCounters(FilesRead: 1, linesSkipped, malformedLines, InvalidRecords: 0);
        return Task.FromResult(new CopilotOtelFileParse(entries, counters));
    }

    // ---- 记录形态判别（蓝本 is_span_record / is_chat_span_record / is_inference_log_record /
    // is_agent_turn_log_record / is_agent_summary_span_record，判别顺序同 to_candidate 的 if/else 链） ----

    /// <summary>
    /// 四形态判别（蓝本 to_candidate 的 if/else 链逐分支保真）：chat span →（非 span）inference log →
    /// agent turn log → agent summary span；各谓词内部的"属性键 || 名称/正文前缀"双通道按蓝本顺序短路。
    /// </summary>
    private static CopilotOtelSource? Classify(JsonElement record, JsonElement attributes)
    {
        if (IsSpanRecord(record))
        {
            // is_chat_span_record：operation.name == "chat" 或 name 以 "chat " 开头。
            if (AttrString(attributes, "gen_ai.operation.name") == "chat"
                || (StringValue(record, "name") is { } chatName
                    && chatName.StartsWith("chat ", StringComparison.Ordinal)))
            {
                return CopilotOtelSource.ChatSpan;
            }

            // is_agent_summary_span_record：operation.name == "invoke_agent" 或 name 前缀。
            if (AttrString(attributes, "gen_ai.operation.name") == "invoke_agent"
                || (StringValue(record, "name") is { } agentName
                    && agentName.StartsWith("invoke_agent ", StringComparison.Ordinal)))
            {
                return CopilotOtelSource.AgentSummarySpan;
            }

            return null;
        }

        // is_inference_log_record：event.name 或 body 前缀（两者按蓝本 || 短路，先于 agent turn）。
        if (AttrString(attributes, "event.name") == "gen_ai.client.inference.operation.details"
            || (RecordBody(record) is { } inferenceBody
                && inferenceBody.StartsWith("GenAI inference:", StringComparison.Ordinal)))
        {
            return CopilotOtelSource.InferenceLog;
        }

        // is_agent_turn_log_record：event.name 或 body 前缀。
        if (AttrString(attributes, "event.name") == "copilot_chat.agent.turn"
            || (RecordBody(record) is { } turnBody
                && turnBody.StartsWith("copilot_chat.agent.turn", StringComparison.Ordinal)))
        {
            return CopilotOtelSource.AgentTurnLog;
        }

        return null;
    }

    private static bool IsSpanRecord(JsonElement record)
    {
        // 蓝本：type 是字符串时直接返回相等判定（type="metric" 不再走启发式）。
        if (record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() == "span";
        }

        if (StringValue(record, "name") is null)
        {
            return false;
        }

        return (NonEmptyString(record, "spanId") is not null || NonEmptyString(record, "traceId") is not null)
               || IsPresent(record, "startTime")
               || IsPresent(record, "endTime")
               || IsPresent(record, "duration")
               || IsPresent(record, "kind");
    }

    // ---- 候选构建（蓝本 to_candidate） ----

    private static CopilotOtelCandidate? TryBuildCandidate(
        JsonElement record,
        int index,
        DateTimeOffset fallbackTimestamp,
        Dictionary<string, TraceContext> contexts,
        string sourceFile)
    {
        if (!record.TryGetProperty("attributes", out var attributesElement)
            || attributesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = Classify(record, attributesElement);
        if (source is null)
        {
            return null;
        }

        var input = AttrNumber(attributesElement, "gen_ai.usage.input_tokens");
        var output = AttrNumber(attributesElement, "gen_ai.usage.output_tokens");
        var cacheRead = AttrNumber(attributesElement, "gen_ai.usage.cache_read.input_tokens");
        var cacheCreation = AttrNumberFirst(
            attributesElement,
            ["gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens"]);
        var reasoning = AttrNumberFirst(
            attributesElement,
            ["gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens"]);
        var total = AttrNumberFirst(
            attributesElement,
            ["gen_ai.usage.total_tokens", "gen_ai.usage.total.token_count"]);

        // Copilot 把缓存读写计入 input_tokens（同 session-state inputTokens）：uncached 拆分 +
        // total 兜底（蓝本 apply_total_token_fallback）；总用量为 0 的候选不产出。
        var usage = new RawTokenUsage(
            SaturatingSubtract(input, SaturatingAdd(cacheRead, cacheCreation)),
            output,
            cacheCreation,
            cacheRead);
        (usage, var extraTotalTokens) = TokenUsageFallback.Apply(usage, 0, total);
        if (usage.TotalUsageTokens() + extraTotalTokens == 0)
        {
            return null;
        }

        var traceId = TraceIdFromRecord(record);
        var context = traceId is not null && contexts.TryGetValue(traceId, out var found) ? found : null;
        var responseId = AttrString(attributesElement, "gen_ai.response.id");
        var model = FirstNonEmptyModelAttr(attributesElement)
                    ?? context?.Model
                    ?? "unknown";
        var sessionId = BestSessionAttr(attributesElement)?.Value
                        ?? context?.SessionId
                        ?? traceId
                        ?? "unknown-session";
        var timestamp = TimestampFromRecord(record) ?? fallbackTimestamp;
        var dedupKey = DedupKeyForRecord(
            source.Value, record, attributesElement, traceId, sessionId, timestamp, index);

        return new CopilotOtelCandidate(
            source.Value, traceId, responseId, model, sessionId, timestamp,
            usage.InputTokens, usage.OutputTokens, usage.CacheCreationTokens, usage.CacheReadTokens,
            reasoning, extraTotalTokens, dedupKey);
    }

    // ---- trace 上下文（蓝本 collect_trace_contexts） ----

    private static Dictionary<string, TraceContext> CollectTraceContexts(
        List<(int Index, JsonElement Element)> records)
    {
        var contexts = new Dictionary<string, TraceContext>(StringComparer.Ordinal);
        foreach (var (_, record) in records)
        {
            var traceId = TraceIdFromRecord(record);
            if (traceId is null
                || !record.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!contexts.TryGetValue(traceId, out var existing))
            {
                existing = new TraceContext();
                contexts.Add(traceId, existing);
            }

            // 模型：该 trace 首个非空模型属性（蓝本 context.model.is_none() 才覆写；本条缺失时
            // 保持 None 由后续记录继续尝试）。
            existing.Model ??= FirstNonEmptyModelAttr(attributes);
            if (BestSessionAttr(attributes) is { } session
                && session.Priority > existing.SessionIdPriority)
            {
                existing.SessionId = session.Value;
                existing.SessionIdPriority = session.Priority;
            }
        }

        return contexts;
    }

    // ---- 互斥去重（蓝本 CandidateSets / should_emit_candidate / repeated_chat_spans） ----

    private static bool ShouldEmit(int index, CopilotOtelCandidate candidate, CandidateSets sets)
    {
        bool TraceMatch(HashSet<string> values)
            => candidate.TraceId is not null && values.Contains(candidate.TraceId);

        bool ResponseMatch(HashSet<string> values)
            => candidate.ResponseId is not null && values.Contains(candidate.ResponseId);

        return candidate.Source switch
        {
            CopilotOtelSource.ChatSpan => !sets.RepeatedChatSpans.Contains(index),
            CopilotOtelSource.InferenceLog =>
                !TraceMatch(sets.ChatTraces) && !ResponseMatch(sets.ChatResponseIds),
            CopilotOtelSource.AgentTurnLog =>
                !TraceMatch(sets.ChatTraces) && !TraceMatch(sets.InferenceTraces)
                && !ResponseMatch(sets.ChatResponseIds) && !ResponseMatch(sets.InferenceResponseIds),
            CopilotOtelSource.AgentSummarySpan =>
                !TraceMatch(sets.ChatTraces) && !TraceMatch(sets.InferenceTraces)
                && !TraceMatch(sets.AgentTurnTraces)
                && !ResponseMatch(sets.ChatResponseIds) && !ResponseMatch(sets.InferenceResponseIds)
                && !ResponseMatch(sets.AgentTurnResponseIds),
            _ => false,
        };
    }

    private static HashSet<int> RepeatedChatSpans(List<CopilotOtelCandidate> candidates)
    {
        // 蓝本 repeated_chat_spans：同 (response id, model, 用量八元组) 的 chat span 计一次；
        // 后出现且时间戳更早者顶替先前保留者（保留时间戳最早的导出）。
        var earliest = new Dictionary<(string ResponseId, string Model, long Input, long Output,
            long CacheCreation, long CacheRead, long Reasoning, long Extra), int>();
        var repeated = new HashSet<int>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Source != CopilotOtelSource.ChatSpan || candidate.ResponseId is not { } responseId)
            {
                continue;
            }

            var call = (responseId, candidate.Model, candidate.InputTokens, candidate.OutputTokens,
                candidate.CacheCreationTokens, candidate.CacheReadTokens,
                candidate.ReasoningOutputTokens, candidate.ExtraTotalTokens);
            if (!earliest.TryGetValue(call, out var kept))
            {
                earliest[call] = index;
                continue;
            }

            if (candidate.Timestamp < candidates[kept].Timestamp)
            {
                repeated.Add(kept);
                earliest[call] = index;
            }
            else
            {
                repeated.Add(index);
            }
        }

        return repeated;
    }

    private static string DedupKeyForRecord(
        CopilotOtelSource source,
        JsonElement record,
        JsonElement attributes,
        string? traceId,
        string sessionId,
        DateTimeOffset timestamp,
        int index)
    {
        var spanId = SpanIdFromRecord(record);
        switch (source)
        {
            case CopilotOtelSource.ChatSpan:
            case CopilotOtelSource.AgentSummarySpan:
                if (traceId is not null && spanId is not null)
                {
                    return $"{traceId}:{spanId}";
                }

                return $"span:{sessionId}:{timestamp.ToUnixTimeMilliseconds()}:{index}";
            case CopilotOtelSource.InferenceLog:
                if (traceId is not null && spanId is not null)
                {
                    return $"log:{traceId}:{spanId}";
                }

                return $"log:{sessionId}:{timestamp.ToUnixTimeMilliseconds()}:{index}";
            case CopilotOtelSource.AgentTurnLog:
                var turnIndex = NumberValue(TryGetProperty(attributes, "turn.index"))
                                ?? NumberValue(TryGetProperty(attributes, "copilot_chat.turn.index"));
                var turn = turnIndex is { } value ? value.ToString() : $"idx-{index}";
                return traceId is not null
                    ? $"agent-turn:{traceId}:{turn}"
                    : $"agent-turn:{sessionId}:{turn}:{index}";
            default:
                return $"span:{sessionId}:{timestamp.ToUnixTimeMilliseconds()}:{index}";
        }
    }

    // ---- 属性与字段读取（蓝本 attr_string / attr_number / number_value / string_value 等） ----

    private static JsonElement? TryGetProperty(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary>蓝本 string_value：字符串去首尾空白后非空才生效。</summary>
    private static string? StringValue(JsonElement parent, string name)
        => TryGetProperty(parent, name) is { ValueKind: JsonValueKind.String } value
            ? NullIfEmpty(value.GetString()?.Trim())
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>蓝本字段存在性（Option&lt;Value&gt; 的 Some：键存在且非 null，任意 JSON 类型）。</summary>
    private static bool IsPresent(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    private static string? NonEmptyString(JsonElement parent, string name)
        => StringValue(parent, name);

    /// <summary>
    /// 蓝本 number_value：JSON 整数（u64 / 非负 i64）或可解析为非负整数的十进制字符串；
    /// 浮点、负数、其余类型为 null（u64 溢出钳到 long 域——RawTokenUsage 同口径）。
    /// </summary>
    private static long? NumberValue(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetUInt64(out var unsigned))
            {
                return (long)Math.Min(unsigned, long.MaxValue);
            }

            if (element.TryGetInt64(out var signed) && signed >= 0)
            {
                return signed;
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()?.Trim();
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? (long)Math.Min(parsed, long.MaxValue)
                : null;
        }

        return null;
    }

    private static string? AttrString(JsonElement attributes, string key)
        => TryGetProperty(attributes, key) is { ValueKind: JsonValueKind.String } value
            ? NullIfEmpty(value.GetString()?.Trim())
            : null;

    private static long AttrNumber(JsonElement attributes, string key)
        => NumberValue(TryGetProperty(attributes, key)) ?? 0;

    private static long AttrNumberFirst(JsonElement attributes, string[] keys)
    {
        foreach (var key in keys)
        {
            if (AttrNumber(attributes, key) is { } value && value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static string? FirstNonEmptyModelAttr(JsonElement attributes)
    {
        foreach (var key in ModelAttributes)
        {
            if (AttrString(attributes, key) is { } model)
            {
                return CopilotSessionParser.NormalizeModel(model);
            }
        }

        return null;
    }

    /// <summary>蓝本 best_session_attr：按 SESSION_ATTRS 序遍历取最高优先级（同优先级后声明胜出）。</summary>
    private static (string Value, byte Priority)? BestSessionAttr(JsonElement attributes)
    {
        (string, byte)? best = null;
        foreach (var (key, priority) in SessionAttributes)
        {
            if (AttrString(attributes, key) is { } value
                && (best is null || priority >= best.Value.Item2))
            {
                best = (value, priority);
            }
        }

        return best;
    }

    private static string? TraceIdFromRecord(JsonElement record)
        => NonEmptyString(record, "traceId")
           ?? NestedString(record, "spanContext", "traceId");

    private static string? SpanIdFromRecord(JsonElement record)
        => NonEmptyString(record, "spanId")
           ?? NestedString(record, "spanContext", "spanId");

    private static string? NestedString(JsonElement parent, string objectName, string key)
        => TryGetProperty(parent, objectName) is { ValueKind: JsonValueKind.Object } nested
            ? StringValue(nested, key)
            : null;

    private static string? RecordBody(JsonElement record)
        => StringValue(record, "body") ?? StringValue(record, "_body");

    // ---- 时间戳（蓝本 timestamp_from_record / timestamp_from_parts / timestamp_from_scalar /
    // timestamp_from_unix_nanos） ----

    private static DateTimeOffset? TimestampFromRecord(JsonElement record)
        => TimestampFromParts(record, "endTime")
           ?? TimestampFromParts(record, "startTime")
           ?? TimestampFromParts(record, "hrTime")
           ?? TimestampFromParts(record, "_hrTime")
           ?? TimestampFromParts(record, "time")
           ?? TimestampFromScalar(record, "timestamp")
           ?? TimestampFromScalar(record, "observedTimestamp")
           ?? TimestampFromUnixNanos(record, "timeUnixNano");

    private static DateTimeOffset? TimestampFromParts(JsonElement record, string name)
    {
        if (TryGetProperty(record, name) is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var items = array.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var seconds = NumberValue(items[0]);
        var nanos = items.Count > 1 ? NumberValue(items[1]) : null;
        if (seconds is not { } secondsValue || nanos is not { } nanosValue)
        {
            return null;
        }

        // 蓝本 checked_mul(1_000).checked_add(nanos / 1_000_000)：溢出则放弃该时间戳来源。
        var millisPart = nanosValue / 1_000_000;
        if (secondsValue > (long.MaxValue - millisPart) / 1_000)
        {
            return null;
        }

        return FromMillis(secondsValue * 1_000 + millisPart);
    }

    private static DateTimeOffset? TimestampFromScalar(JsonElement record, string name)
    {
        if (NumberValue(TryGetProperty(record, name)) is not { } raw)
        {
            return null;
        }

        // 蓝本按数量级猜测刻度：≥1e17 纳秒、≥1e14 微秒、≥1e11 毫秒，否则秒。
        long millis = raw switch
        {
            >= 100_000_000_000_000_000 => raw / 1_000_000,
            >= 100_000_000_000_000 => raw / 1_000,
            >= 100_000_000_000 => raw,
            _ => raw * 1_000,
        };
        return FromMillis(millis);
    }

    private static DateTimeOffset? TimestampFromUnixNanos(JsonElement record, string name)
    {
        if (NumberValue(TryGetProperty(record, name)) is not { } raw || raw <= 0)
        {
            return null;
        }

        return FromMillis(raw / 1_000_000);
    }

    private static DateTimeOffset? FromMillis(long millis)
        => millis > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(millis, 0));

    // ---- 饱和运算（long 域，操作数经 NumberValue 保证 ≥ 0） ----

    private static long SaturatingAdd(long left, long right)
        => left >= 0 && right >= 0 && left <= long.MaxValue - right ? left + right : long.MaxValue;

    private static long SaturatingSubtract(long left, long right)
        => left > right ? left - right : 0;

    private sealed class TraceContext
    {
        public string? Model { get; set; }

        public string? SessionId { get; set; }

        public byte SessionIdPriority { get; set; }
    }

    private sealed record CopilotOtelCandidate(
        CopilotOtelSource Source,
        string? TraceId,
        string? ResponseId,
        string Model,
        string SessionId,
        DateTimeOffset Timestamp,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        long ReasoningOutputTokens,
        long ExtraTotalTokens,
        string DedupKey);

    private sealed class CandidateSets
    {
        private CandidateSets(
            HashSet<int> repeatedChatSpans,
            HashSet<string> chatTraces,
            HashSet<string> inferenceTraces,
            HashSet<string> agentTurnTraces,
            HashSet<string> chatResponseIds,
            HashSet<string> inferenceResponseIds,
            HashSet<string> agentTurnResponseIds)
        {
            RepeatedChatSpans = repeatedChatSpans;
            ChatTraces = chatTraces;
            InferenceTraces = inferenceTraces;
            AgentTurnTraces = agentTurnTraces;
            ChatResponseIds = chatResponseIds;
            InferenceResponseIds = inferenceResponseIds;
            AgentTurnResponseIds = agentTurnResponseIds;
        }

        public HashSet<int> RepeatedChatSpans { get; }

        public HashSet<string> ChatTraces { get; }

        public HashSet<string> InferenceTraces { get; }

        public HashSet<string> AgentTurnTraces { get; }

        public HashSet<string> ChatResponseIds { get; }

        public HashSet<string> InferenceResponseIds { get; }

        public HashSet<string> AgentTurnResponseIds { get; }

        public static CandidateSets Build(List<CopilotOtelCandidate> candidates)
            => new(
                repeatedChatSpans: RepeatedChatSpans(candidates),
                chatTraces: TraceIdsOf(candidates, CopilotOtelSource.ChatSpan),
                inferenceTraces: TraceIdsOf(candidates, CopilotOtelSource.InferenceLog),
                agentTurnTraces: TraceIdsOf(candidates, CopilotOtelSource.AgentTurnLog),
                chatResponseIds: ResponseIdsOf(candidates, CopilotOtelSource.ChatSpan),
                inferenceResponseIds: ResponseIdsOf(candidates, CopilotOtelSource.InferenceLog),
                agentTurnResponseIds: ResponseIdsOf(candidates, CopilotOtelSource.AgentTurnLog));

        private static HashSet<string> TraceIdsOf(List<CopilotOtelCandidate> candidates, CopilotOtelSource source)
            => candidates
                .Where(candidate => candidate.Source == source && candidate.TraceId is not null)
                .Select(candidate => candidate.TraceId!)
                .ToHashSet(StringComparer.Ordinal);

        private static HashSet<string> ResponseIdsOf(List<CopilotOtelCandidate> candidates, CopilotOtelSource source)
            => candidates
                .Where(candidate => candidate.Source == source && candidate.ResponseId is not null)
                .Select(candidate => candidate.ResponseId!)
                .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>OpenTelemetry 记录形态（蓝本 CopilotUsageSource；优先级 chat &gt; inference &gt; turn &gt; summary）。</summary>
internal enum CopilotOtelSource
{
    ChatSpan,
    InferenceLog,
    AgentTurnLog,
    AgentSummarySpan,
}
