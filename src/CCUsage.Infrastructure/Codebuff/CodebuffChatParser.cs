using System.Globalization;
using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.Codebuff;

/// <summary>
/// Codebuff 单文件解析（蓝本 ../ccusage/rust/adapters/codebuff/src/parser.rs 逐条移植，I-018；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。每个 chat-messages.json 为单层 JSON
/// <b>数组</b>（非 JSONL），lenient 口径：
/// <list type="bullet">
/// <item>顶层必须可反序列化为数组（serde from_slice&lt;Vec&lt;Value&gt;&gt;）：解析失败 / 非数组
/// （对象、标量、null）整文件跳过（蓝本静默返回空，仓库口径计 Malformed）。</item>
/// <item>assistant 判定（蓝本 is_assistant_message）：variant 优先、role 兜底（均 non_empty_string），
/// 恰为 "ai" | "agent" | "assistant"。</item>
/// <item>取数回退链（蓝本 extract_assistant_usage，merge_fallback 逐字段 0 值兜底）：
/// metadata.model + metadata.usage → metadata.codebuff.usage → runState.sessionState.mainAgentState.
/// messageHistory <b>倒序</b>扫描 assistant 条目的 providerOptions（providerOptions.usage 与
/// providerOptions.codebuff.usage，codebuff.model 覆盖 usage.model）；消息级 credits 兜底。</item>
/// <item>usage 对象（蓝本 parse_usage_object）：input/output/cache_creation 按多键名别名取<b>第一个
/// &gt;0</b> 的 u64；cache_read 取直接键与 promptTokensDetails.cached_tokens /
/// prompt_tokens_details.cached_tokens 的最大值；total 兜底（output 为 0 补 output、否则缺口并入
/// extra）；usage 对象内的 credits / model 也并入（后续字段仍以先到者为准）。</item>
/// <item>时间戳（蓝本 message_timestamp）：message.timestamp（字符串严格 RFC3339 或数字秒/毫秒
/// 自适应，&lt;10^10 视为秒）→ createdAt → metadata.timestamp → chat 目录名时间戳
/// （"2026-01-02T03-04-05.000Z" 形态，前两个 '-' 还原为 ':'）→ 文件 mtime → UNIX 纪元。</item>
/// <item>缺省模型（蓝本 DEFAULT_CODEBUFF_MODEL）："codebuff-unknown"。</item>
/// <item>去重键（蓝本 dedup_key）：消息有 id 时 "codebuff:{session}:{id}"，否则
/// "codebuff:{session}:{RFC3339 毫秒}:{model}:{ordinal}:{in}:{out}:{cr}:{cc}:{extra}"（ordinal 为
/// 数组原始下标）；跨文件 <b>last-wins</b>（蓝本 HashMap insert 同口径）。</item>
/// </list>
/// 成本落点：统一记录 CostUsd 恒 null 交定价引擎（蓝本 calculate_codebuff_cost 的 provider/
/// 前缀候选重试与缺失定价报告属 I-024 端到端欠账，见卡片偏差①）；extra 并入统一记录 OutputTokens
/// （仓库口径，与蓝本计价式 output+extra 同值，见卡片偏差②）。行级容错为仓库 I-004 口径
/// （蓝本静默跳过处显式计数）。
/// </summary>
public sealed class CodebuffChatParser
{
    public const string AgentName = "Codebuff";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Codebuff"）。</summary>
    public const string ProjectPath = "Codebuff";

    /// <summary>缺省模型名（蓝本 DEFAULT_CODEBUFF_MODEL）。</summary>
    public const string DefaultModel = "codebuff-unknown";

    /// <summary>解析单个 chat-messages.json（蓝本 load_chat_file）。读取失败计 LinesSkipped、
    /// 不计 FilesRead（共享扫描引擎同口径）；顶层非数组 / 坏 JSON 计 MalformedLines。</summary>
    public CodebuffFileOutcome ParseChatFile(string sourceFile)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodebuffFileOutcome.Empty(linesSkipped: 1);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new CodebuffFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 1, InvalidRecords: 0);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                // 蓝本 from_slice::<Vec<Value>> 对非数组输入失败 → 整文件空（仓库口径计 malformed）。
                return new CodebuffFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 1, InvalidRecords: 0);
            }

            var context = DeriveContext(sourceFile);
            var chatTimestamp = ParseChatIdTimestamp(context.ChatId);
            var fileTimestamp = SourceFileCollector.TryGetModifiedTimestamp(sourceFile) ?? StrictRfc3339Timestamp.Epoch;

            var entries = new List<CodebuffEntry>();
            var skipped = 0;
            var ordinal = -1;
            foreach (var message in document.RootElement.EnumerateArray())
            {
                ordinal++;
                if (message.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                if (!IsAssistantMessage(message))
                {
                    skipped++;
                    continue;
                }

                var usage = ExtractAssistantUsage(message);
                if (!HasSignal(usage))
                {
                    skipped++;
                    continue;
                }

                var timestamp = MessageTimestamp(message)
                                ?? chatTimestamp
                                ?? fileTimestamp;
                var model = usage.Model ?? DefaultModel;
                var dedupKey = BuildDedupKey(message, context.SessionId, timestamp, model, usage, ordinal);
                entries.Add(new CodebuffEntry(
                    ToRecord(timestamp, model, usage, context.SessionId, sourceFile),
                    dedupKey));
            }

            return new CodebuffFileOutcome(entries, FilesRead: 1, skipped, MalformedLines: 0, InvalidRecords: 0);
        }
    }

    /// <summary>统一记录映射：CostUsd 恒 null 交定价引擎；extra 并入 OutputTokens（蓝本计价式
    /// output+extra 同值）；MessageId/RequestId 置 null——去重键由 <see cref="CodebuffEntry"/>
    /// 单独承载（复合键非 Claude MessageId 语义，防共享去重器误并）；IsSidechain 恒 false。</summary>
    private static UsageRecord ToRecord(
        DateTimeOffset timestamp, string model, AssistantUsage usage, string sessionId, string sourceFile)
    {
        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = sessionId,
            Model = model,
            CostUsd = null,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens.SaturatingAddClamped(usage.ExtraTotalTokens),
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

    /// <summary>会话上下文（蓝本 derive_context）：chat 目录名 / 项目目录名 / channel 目录名，
    /// 任一环节空白回退 "unknown"（channel 回退 "manicode"），session id = "{channel}/{project}/{chat}"。</summary>
    internal static CodebuffContext DeriveContext(string sourceFile)
    {
        var chatDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        var chatId = DirectoryNameOr(chatDirectory, "unknown");
        var chatsDirectory = chatDirectory is null ? null : Path.GetDirectoryName(chatDirectory);
        var projectDirectory = chatsDirectory is null ? null : Path.GetDirectoryName(chatsDirectory);
        var project = DirectoryNameOr(projectDirectory, "unknown");
        var channel = DirectoryNameOr(projectDirectory is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(projectDirectory)), "manicode");
        return new CodebuffContext(channel, project, chatId, $"{channel}/{project}/{chatId}");
    }

    private static string DirectoryNameOr(string? directory, string fallback)
    {
        var name = directory is null ? null : Path.GetFileName(directory);
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    /// <summary>assistant 判定（蓝本 is_assistant_message）：variant 优先、role 兜底，恰为三值之一。</summary>
    internal static bool IsAssistantMessage(JsonElement message)
    {
        var variant = LenientJson.GetNonEmptyString(message, "variant")
                      ?? LenientJson.GetNonEmptyString(message, "role");
        return variant is "ai" or "agent" or "assistant";
    }

    /// <summary>取数回退链（蓝本 extract_assistant_usage）。</summary>
    internal static AssistantUsage ExtractAssistantUsage(JsonElement message)
    {
        var usage = new AssistantUsage();
        if (LenientJson.GetObject(message, "metadata") is { } metadata)
        {
            usage.Model = LenientJson.GetNonEmptyString(metadata, "model");
            MergeFallback(usage, ParseUsageObject(TryGet(metadata, "usage")));
            // 蓝本 metadata.codebuff 仅在为对象时取其 usage（非对象 → parse_usage_object(None) 同为空合并）。
            if (LenientJson.GetObject(metadata, "codebuff") is { } codebuff)
            {
                MergeFallback(usage, ParseUsageObject(TryGet(codebuff, "usage")));
            }

            if (ExtractUsageFromRunState(metadata) is { } runStateUsage)
            {
                MergeFallback(usage, runStateUsage);
            }
        }

        var credits = NumberField(message, "credits");
        if (credits > 0.0 && usage.Credits <= 0.0)
        {
            usage.Credits = credits;
        }

        return usage;
    }

    /// <summary>runState 倒序扫描（蓝本 extract_usage_from_run_state）：末位 assistant 条目字段优先
    /// （merge_fallback 先到先得）；任一条目有信号或有 model 即算 found。</summary>
    private static AssistantUsage? ExtractUsageFromRunState(JsonElement metadata)
    {
        if (LenientJson.GetObject(metadata, "runState") is not { } runState
            || LenientJson.GetObject(runState, "sessionState") is not { } sessionState
            || LenientJson.GetObject(sessionState, "mainAgentState") is not { } mainAgentState
            || TryGet(mainAgentState, "messageHistory") is not { } history
            || history.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var usage = new AssistantUsage();
        var found = false;
        foreach (var item in history.EnumerateArray().Reverse())
        {
            if (item.ValueKind != JsonValueKind.Object
                || LenientJson.GetNonEmptyString(item, "role") != "assistant"
                || LenientJson.GetObject(item, "providerOptions") is not { } providerOptions)
            {
                continue;
            }

            var entryUsage = new AssistantUsage();
            MergeFallback(entryUsage, ParseUsageObject(TryGet(providerOptions, "usage")));
            if (LenientJson.GetObject(providerOptions, "codebuff") is { } codebuff)
            {
                MergeFallback(entryUsage, ParseUsageObject(TryGet(codebuff, "usage")));
                if (LenientJson.GetNonEmptyString(codebuff, "model") is { } codebuffModel)
                {
                    // 蓝本 entry_usage.model = codebuff.model.or(entry_usage.model)：codebuff.model 有值即覆盖。
                    entryUsage.Model = codebuffModel;
                }
            }

            if (HasSignal(entryUsage) || entryUsage.Model is not null)
            {
                found = true;
            }

            MergeFallback(usage, entryUsage);
        }

        return found ? usage : null;
    }

    /// <summary>usage 载荷解析（蓝本 parse_usage_object）：非对象（null/标量/数组/缺失）全零返回。</summary>
    internal static AssistantUsage ParseUsageObject(JsonElement? value)
    {
        var usage = new AssistantUsage();
        if (value is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return usage;
        }

        var record = element;
        usage.InputTokens = PickU64(record, "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
        usage.OutputTokens = PickU64(record, "outputTokens", "output_tokens", "completionTokens", "completion_tokens");
        usage.CacheReadTokens = Math.Max(
            PickU64(record, "cacheReadInputTokens", "cache_read_input_tokens"),
            Math.Max(
                PickNestedU64(record, "promptTokensDetails", "cachedTokens"),
                PickNestedU64(record, "prompt_tokens_details", "cached_tokens")));
        usage.CacheCreationTokens = PickU64(
            record,
            "cacheCreationInputTokens", "cache_creation_input_tokens", "cacheCreationTokens",
            "cache_creation_tokens", "cachedTokensCreated", "cached_tokens_created");
        var totalTokens = PickU64(record, "totalTokens", "total_tokens", "total");
        var (resolved, extraTotalTokens) = TokenUsageFallback.Apply(
            new RawTokenUsage(usage.InputTokens, usage.OutputTokens, usage.CacheCreationTokens, usage.CacheReadTokens),
            usage.ExtraTotalTokens,
            totalTokens);
        usage.InputTokens = resolved.InputTokens;
        usage.OutputTokens = resolved.OutputTokens;
        usage.CacheCreationTokens = resolved.CacheCreationTokens;
        usage.CacheReadTokens = resolved.CacheReadTokens;
        usage.ExtraTotalTokens = extraTotalTokens;
        usage.Credits = NumberField(record, "credits");
        usage.Model = LenientJson.GetNonEmptyString(record, "model");
        return usage;
    }

    /// <summary>逐字段 0 值兜底（蓝本 merge_fallback）：target 非零字段保留，零/无字段取 fallback。</summary>
    private static void MergeFallback(AssistantUsage target, AssistantUsage fallback)
    {
        if (target.InputTokens == 0)
        {
            target.InputTokens = fallback.InputTokens;
        }

        if (target.OutputTokens == 0)
        {
            target.OutputTokens = fallback.OutputTokens;
        }

        if (target.CacheCreationTokens == 0)
        {
            target.CacheCreationTokens = fallback.CacheCreationTokens;
        }

        if (target.CacheReadTokens == 0)
        {
            target.CacheReadTokens = fallback.CacheReadTokens;
        }

        if (target.ExtraTotalTokens == 0)
        {
            target.ExtraTotalTokens = fallback.ExtraTotalTokens;
        }

        if (target.Credits <= 0.0)
        {
            target.Credits = fallback.Credits;
        }

        if (target.Model is null)
        {
            target.Model = fallback.Model;
        }
    }

    private static bool HasSignal(AssistantUsage usage)
    {
        return usage.InputTokens > 0
               || usage.OutputTokens > 0
               || usage.CacheCreationTokens > 0
               || usage.CacheReadTokens > 0
               || usage.ExtraTotalTokens > 0
               || usage.Credits > 0.0;
    }

    /// <summary>消息时间戳（蓝本 message_timestamp）：timestamp → createdAt → metadata.timestamp。</summary>
    private static DateTimeOffset? MessageTimestamp(JsonElement message)
    {
        return TimestampValue(TryGet(message, "timestamp"))
               ?? TimestampValue(TryGet(message, "createdAt"))
               ?? (LenientJson.GetObject(message, "metadata") is { } metadata
                   ? TimestampValue(TryGet(metadata, "timestamp"))
                   : null);
    }

    /// <summary>chat 目录名时间戳（蓝本 parse_codebuff_chat_id_timestamp）："2026-01-02T03-04-05.000Z"
    /// 形态，时间部分前两个 '-' 还原为 ':' 后按严格 RFC3339 解析。</summary>
    internal static DateTimeOffset? ParseChatIdTimestamp(string chatId)
    {
        var separator = chatId.IndexOf('T');
        if (separator < 0)
        {
            return null;
        }

        var date = chatId[..separator];
        var time = chatId[(separator + 1)..];
        for (var index = 0; index < 2; index++)
        {
            var dash = time.IndexOf('-');
            if (dash < 0)
            {
                break;
            }

            time = $"{time[..dash]}:{time[(dash + 1)..]}";
        }

        return StrictRfc3339Timestamp.TryParse($"{date}T{time}", out var timestamp)
            ? timestamp
            : null;
    }

    /// <summary>时间戳字段（蓝本 timestamp_value）：字符串按严格 RFC3339；数字 &lt;10^10 视为秒
    /// （×1000），否则毫秒；必须 &gt;0；其余（含浮点）None。</summary>
    private static DateTimeOffset? TimestampValue(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                return text is not null && StrictRfc3339Timestamp.TryParse(text, out var parsed) ? parsed : null;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var raw))
                {
                    return null;
                }

                // 秒 → 毫秒分支受 raw < 10^10 门控，×1000 不会溢出 long。
                var millis = raw < 10_000_000_000 ? raw * 1_000 : raw;
                if (millis <= 0)
                {
                    return null;
                }

                try
                {
                    return DateTimeOffset.UnixEpoch.AddMilliseconds(millis);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 蓝本 jiff Timestamp 同域受限（±9999 年），越界理论角落同回退 None。
                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>去重键（蓝本 dedup_key）：有 id 走 "codebuff:{session}:{id}"，否则内容键
    /// （RFC3339 毫秒文本 + 模型 + 数组 ordinal + 五个计数分量 in/out/cr/cc/extra）。</summary>
    private static string BuildDedupKey(
        JsonElement message, string sessionId, DateTimeOffset timestamp, string model, AssistantUsage usage, int ordinal)
    {
        if (LenientJson.GetNonEmptyString(message, "id") is { } id)
        {
            return $"codebuff:{sessionId}:{id}";
        }

        return $"codebuff:{sessionId}:{FormatRfc3339Millis(timestamp)}:{model}:{ordinal}"
               + $":{usage.InputTokens}:{usage.OutputTokens}:{usage.CacheReadTokens}"
               + $":{usage.CacheCreationTokens}:{usage.ExtraTotalTokens}";
    }

    /// <summary>RFC3339 毫秒 UTC 文本（蓝本 format_rfc3339_millis："yyyy-MM-ddTHH:mm:ss.fffZ"）。</summary>
    internal static string FormatRfc3339Millis(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>多键名别名取值（蓝本 pick_u64）：第一个「非负整数且 &gt;0」的键生效；0 / 负数 /
    /// 浮点跳过试下一键；u64 溢出钳到 long.MaxValue（I-017 偏差⑥同款）。</summary>
    private static long PickU64(JsonElement record, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (record.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetUInt64(out var parsed)
                && parsed > 0)
            {
                return parsed > long.MaxValue ? long.MaxValue : (long)parsed;
            }
        }

        return 0;
    }

    private static long PickNestedU64(JsonElement record, string key, params string[] keys)
    {
        return LenientJson.GetObject(record, key) is { } nested ? PickU64(nested, keys) : 0;
    }

    /// <summary>正有限数值（蓝本 number_field）：非数值 / 非有限 / ≤0 返回 0。</summary>
    private static double NumberField(JsonElement record, string key)
    {
        return LenientJson.GetF64(record, key) is { } value && double.IsFinite(value) && value > 0.0 ? value : 0.0;
    }

    private static JsonElement? TryGet(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
}

/// <summary>
/// Codebuff assistant usage 载体（蓝本 AssistantUsage）：Model/Credits 可选，四桶 + extra 为 long
/// （u64 溢出钳 long.MaxValue，仓库口径）；credits 蓝本为 f64 —— 桌面统一记录无 credits 字段，
/// 仅参与 has_signal 门槛（见卡片偏差③）。
/// </summary>
public sealed class AssistantUsage
{
    public string? Model { get; set; }

    public double Credits { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheCreationTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long ExtraTotalTokens { get; set; }
}

/// <summary>会话上下文（蓝本 CodebuffContext）：channel / project / chat 目录名与拼装的 session id。</summary>
public sealed record CodebuffContext(string Channel, string Project, string ChatId, string SessionId);

/// <summary>单条解析产出：统一记录 + 蓝本 dedup_key（last-wins 去重键，适配器消费）。</summary>
public sealed record CodebuffEntry(UsageRecord Record, string DedupKey);

/// <summary>单聊天文件解析产出：键控条目 + 诊断计数（InvalidRecords 无对应场景，恒 0）。</summary>
public sealed record CodebuffFileOutcome(
    List<CodebuffEntry> Entries,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords)
{
    public static CodebuffFileOutcome Empty(int linesSkipped)
        => new([], FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0);
}
