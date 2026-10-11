using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.Amp;

/// <summary>
/// Amp 线程文件解析（蓝本 ../ccusage/rust/adapters/amp/src/parser.rs 逐条移植，I-014；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。单层 JSON 文件（非 JSONL），lenient 口径：
/// 顶层 id（去空白非空）、messages（lenient_vec：数组中非对象元素跳过）、usageLedger（lenient_object）：
/// <list type="bullet">
/// <item><b>usageLedger.events 优先</b>（蓝本 read_thread_file）：events 为数组即走账本路径——含
/// "存在但为空数组"（Some([]) 同样走账本路径、产出 0 条、<b>不回退</b> messages）；events 缺失/非数组
/// （lenient_array → None）才回退 messages。账本事件：timestamp（去空白非空）+ 严格 RFC3339 可解析 +
/// model（去空白非空）三关 + tokens 过 Option&lt;Value&gt; 关（缺失/null 均跳过，标量过门且字段读取
/// 全 0——toMessageId 缓存照常 attach）；cache_creation/cache_read
/// 由 toMessageId（仅整数）查 messages 内 assistant 消息的 usage（cacheCreationInputTokens /
/// cacheReadInputTokens，整数口径） attach；total 兜底同 Gemini（output 为 0 补 output、否则缺口入
/// extra）；四桶与 extra 全零跳过。</item>
/// <item>messages 路径（账本缺失/非对象时）：仅 role 恰为 "assistant"（原样比较，不 trim）的消息；
/// usage 为 Option&lt;Value&gt;——缺失/null 跳过，标量过门且 Value::get 全 None（timestamp/model 回退
/// 消息级字段、四桶 0，随后按全零口径跳过）；timestamp = usage.timestamp ?? message.timestamp、
/// model = usage.model ?? message.model（均去空白非空）；四桶 inputTokens/outputTokens/
/// cacheCreationInputTokens/cacheReadInputTokens + totalTokens；total 兜底与全零跳过同上。</item>
/// </list>
/// 成本落点：CostUsd 恒 null 交定价引擎（蓝本 calculate_cost 裸模型名 + PricingMap 别名/模糊回退，属
/// I-024 端到端欠账，见卡片偏差①）；extra 并入 OutputTokens（蓝本 cost_usage 同式）；credits 字段
/// （账本 event.credits / usage.credits）桌面统一记录无对应字段，不采集（见卡片偏差②）。
/// 行级容错为仓库 I-004 口径（蓝本静默跳过处显式计数）：坏 JSON 计 MalformedLines；有效 JSON 但缺
/// thread id 计 InvalidRecords；事件缺/不可解析时间戳计 InvalidRecords（Copilot 同款）；缺 model/tokens
/// 与全零行计 LinesSkipped；非 assistant 消息计 LinesSkipped。
/// </summary>
public sealed class AmpThreadParser
{
    public const string AgentName = "Amp";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Amp"）。</summary>
    public const string ProjectPath = "Amp";

    /// <summary>解析单个线程文件；读取失败不计 FilesRead、计 LinesSkipped（扫描器同口径）。</summary>
    public AmpFileOutcome ParseThreadFile(string sourceFile)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AmpFileOutcome.Empty(linesSkipped: 1);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // 蓝本 from_slice 失败静默返回空（仓库口径计 malformed，显式计数偏差）。
            return new AmpFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 1, InvalidRecords: 0);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // 顶层非对象：serde 结构性反序列化失败同口径（显式计数）。
                return new AmpFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 1, InvalidRecords: 0);
            }

            var threadId = LenientJson.GetNonEmptyString(root, "id");
            if (threadId is null)
            {
                // 蓝本 thread.id None → Ok(vec![])（显式计数偏差）。
                return new AmpFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 1);
            }

            var messages = CollectMessages(root);
            var usageLedger = LenientJson.GetObject(root, "usageLedger");

            List<UsageRecord> records;
            var skipped = 0;
            var invalid = 0;
            if (usageLedger is { } ledger && TryGetLedgerEvents(ledger) is { } events)
            {
                // 蓝本账本路径：events 数组存在即走账本（含空数组，不回退 messages）。
                var cacheTokens = CacheTokensByMessageId(messages);
                records = ParseLedgerEvents(events, cacheTokens, threadId, sourceFile, out skipped, out invalid);
            }
            else
            {
                records = ParseMessageUsage(messages, threadId, sourceFile, out skipped, out invalid);
            }

            return new AmpFileOutcome(records, FilesRead: 1, LinesSkipped: skipped, MalformedLines: 0, InvalidRecords: invalid);
        }
    }

    /// <summary>messages 收集（蓝本 lenient_vec）：数组内对象元素保留，非对象元素跳过，非数组/缺失为空。</summary>
    private static List<JsonElement> CollectMessages(JsonElement root)
    {
        var messages = new List<JsonElement>();
        if (root.TryGetProperty("messages", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    messages.Add(element);
                }
            }
        }

        return messages;
    }

    /// <summary>usageLedger.events（蓝本 lenient_array）：数组内对象元素保留；缺失/非数组返回 null
    /// （与"存在但为空数组"区分——后者仍走账本路径）。</summary>
    private static List<JsonElement>? TryGetLedgerEvents(JsonElement ledger)
    {
        if (!ledger.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var collected = new List<JsonElement>();
        foreach (var element in events.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                collected.Add(element);
            }
        }

        return collected;
    }

    /// <summary>assistant 消息的缓存四桶索引（蓝本 cache_tokens_by_message_id）：messageId 仅整数，
    /// cacheCreation/cacheRead 整数口径读取。</summary>
    private static Dictionary<long, (long CacheCreation, long CacheRead)> CacheTokensByMessageId(
        List<JsonElement> messages)
    {
        var cacheTokens = new Dictionary<long, (long, long)>();
        foreach (var message in messages)
        {
            if (!IsAssistant(message))
            {
                continue;
            }

            if (TryGetProperty(message, "messageId") is not { } messageIdElement
                || messageIdElement.ValueKind != JsonValueKind.Number
                || !messageIdElement.TryGetInt64(out var messageId))
            {
                continue;
            }

            // 蓝本 cache_tokens_by_message_id：message.usage 为 Option<Value>——缺失/null（serde null →
            // None）或非对象（标量过 Value::get 全部 None）都按 (0,0) 登记该 messageId，缓存映射不缺失。
            var usage = TryGetProperty(message, "usage");
            cacheTokens[messageId] = (
                usage is { } creationUsage && creationUsage.ValueKind == JsonValueKind.Object
                    ? LenientJson.GetU64(creationUsage, "cacheCreationInputTokens")
                    : 0,
                usage is { } readUsage && readUsage.ValueKind == JsonValueKind.Object
                    ? LenientJson.GetU64(readUsage, "cacheReadInputTokens")
                    : 0);
        }

        return cacheTokens;
    }

    /// <summary>账本事件解析（蓝本 parse_ledger_events）。</summary>
    private static List<UsageRecord> ParseLedgerEvents(
        List<JsonElement> events,
        Dictionary<long, (long CacheCreation, long CacheRead)> cacheTokens,
        string threadId,
        string sourceFile,
        out int skipped,
        out int invalid)
    {
        skipped = 0;
        invalid = 0;
        var records = new List<UsageRecord>();
        foreach (var eventElement in events)
        {
            var timestampText = LenientJson.GetNonEmptyString(eventElement, "timestamp");
            if (timestampText is null || !StrictRfc3339Timestamp.TryParse(timestampText, out var timestamp))
            {
                // 缺时间戳 / 不可解析：区间无法归日（仓库口径计 invalid，Copilot 同款偏差）。
                invalid++;
                continue;
            }

            var model = LenientJson.GetNonEmptyString(eventElement, "model");
            if (model is null)
            {
                skipped++;
                continue;
            }

            // 蓝本 event.tokens 为 Option<Value>：缺失与 null（serde null → None）都不过
            // `let Some(tokens)` 关——事件跳过；标量（Some(标量)）过门，字段读取全 None → 四桶 0，
            // toMessageId 缓存照常 attach（全 0 + 缓存可产出记录）。
            if (TryGetProperty(eventElement, "tokens") is not { } tokens
                || tokens.ValueKind == JsonValueKind.Null)
            {
                skipped++;
                continue;
            }

            var cache = TryGetProperty(eventElement, "toMessageId") is { } toMessageId
                        && toMessageId.ValueKind == JsonValueKind.Number
                        && toMessageId.TryGetInt64(out var toMessageKey)
                        && cacheTokens.TryGetValue(toMessageKey, out var lookedUp)
                ? lookedUp
                : (0, 0);

            var hasTokenObject = tokens.ValueKind == JsonValueKind.Object;
            var usage = new RawTokenUsage(
                InputTokens: hasTokenObject ? LenientJson.GetU64(tokens, "input") : 0,
                OutputTokens: hasTokenObject ? LenientJson.GetU64(tokens, "output") : 0,
                CacheCreationTokens: cache.Item1,
                CacheReadTokens: cache.Item2);
            var totalTokens = hasTokenObject ? LenientJson.GetU64(tokens, "total") : 0;
            var (resolvedUsage, extraTokens) = TokenUsageFallback.Apply(usage, 0, totalTokens);
            if (resolvedUsage.InputTokens == 0 && resolvedUsage.OutputTokens == 0
                && resolvedUsage.CacheCreationTokens == 0 && resolvedUsage.CacheReadTokens == 0
                && extraTokens == 0)
            {
                skipped++;
                continue;
            }

            records.Add(MapRecord(resolvedUsage, extraTokens, model, threadId, timestamp, timestampText, sourceFile));
        }

        return records;
    }

    /// <summary>messages 用量解析（蓝本 parse_message_usage）。</summary>
    private static List<UsageRecord> ParseMessageUsage(
        List<JsonElement> messages,
        string threadId,
        string sourceFile,
        out int skipped,
        out int invalid)
    {
        skipped = 0;
        invalid = 0;
        var records = new List<UsageRecord>();
        foreach (var message in messages)
        {
            if (!IsAssistant(message))
            {
                skipped++;
                continue;
            }

            // 蓝本 message.usage 为 Option<Value>：缺失与 null（serde null → None）都不过
            // `let Some(usage)` 关——消息跳过；标量（Some(标量)）过门，其 Value::get 全 None →
            // timestamp/model 回退消息级字段、四桶 0（随后按全零口径跳过）。
            if (TryGetProperty(message, "usage") is not { } usageElement
                || usageElement.ValueKind == JsonValueKind.Null)
            {
                skipped++;
                continue;
            }

            var hasUsageObject = usageElement.ValueKind == JsonValueKind.Object;
            var timestampText = (hasUsageObject
                                    ? LenientJson.GetNonEmptyString(usageElement, "timestamp")
                                    : null)
                                ?? LenientJson.GetNonEmptyString(message, "timestamp");
            if (timestampText is null || !StrictRfc3339Timestamp.TryParse(timestampText, out var timestamp))
            {
                invalid++;
                continue;
            }

            var model = (hasUsageObject
                            ? LenientJson.GetNonEmptyString(usageElement, "model")
                            : null)
                        ?? LenientJson.GetNonEmptyString(message, "model");
            if (model is null)
            {
                skipped++;
                continue;
            }

            var rawUsage = new RawTokenUsage(
                InputTokens: hasUsageObject ? LenientJson.GetU64(usageElement, "inputTokens") : 0,
                OutputTokens: hasUsageObject ? LenientJson.GetU64(usageElement, "outputTokens") : 0,
                CacheCreationTokens: hasUsageObject ? LenientJson.GetU64(usageElement, "cacheCreationInputTokens") : 0,
                CacheReadTokens: hasUsageObject ? LenientJson.GetU64(usageElement, "cacheReadInputTokens") : 0);
            var totalTokens = hasUsageObject ? LenientJson.GetU64(usageElement, "totalTokens") : 0;
            var (resolvedUsage, extraTokens) = TokenUsageFallback.Apply(rawUsage, 0, totalTokens);
            if (resolvedUsage.InputTokens == 0 && resolvedUsage.OutputTokens == 0
                && resolvedUsage.CacheCreationTokens == 0 && resolvedUsage.CacheReadTokens == 0
                && extraTokens == 0)
            {
                skipped++;
                continue;
            }

            records.Add(MapRecord(resolvedUsage, extraTokens, model, threadId, timestamp, timestampText, sourceFile));
        }

        return records;
    }

    /// <summary>统一记录映射：CostUsd 恒 null 交定价引擎；extra 并入 OutputTokens（蓝本 cost_usage 同式）；
    /// MessageId/RequestId 置 null——蓝本不做消息级去重（Copilot 同款直通口径，防未来接入共享去重器
    /// 跨文件误并）；credits 字段不采集（偏差②）。</summary>
    private static UsageRecord MapRecord(
        RawTokenUsage usage,
        long extraTokens,
        string model,
        string threadId,
        DateTimeOffset timestamp,
        string timestampText,
        string sourceFile)
    {
        _ = timestampText; // 蓝本 UsageEntry.timestamp 携带原文；桌面统一记录仅存 DateTimeOffset。
        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = threadId,
            Model = model,
            CostUsd = null,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens.SaturatingAddClamped(extraTokens),
            CacheCreationTokens = usage.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = usage.CacheReadTokens,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = sourceFile,
        };
    }

    /// <summary>role 判别（蓝本 Value::as_str 原样比较，不 trim：" assistant " 不命中）。</summary>
    private static bool IsAssistant(JsonElement message)
        => TryGetProperty(message, "role") is { } role
           && role.ValueKind == JsonValueKind.String
           && role.GetString() == "assistant";

    private static JsonElement? TryGetProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
}

/// <summary>单线程文件解析产出：记录 + 诊断计数（DuplicateRecords 无对应场景，恒 0）。</summary>
public sealed record AmpFileOutcome(
    List<UsageRecord> Records,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords)
{
    public static AmpFileOutcome Empty(int linesSkipped)
        => new([], FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0);
}
