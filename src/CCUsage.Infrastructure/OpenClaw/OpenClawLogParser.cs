using System.Globalization;
using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.OpenClaw;

/// <summary>行级产出归因（蓝本 parse_transcript_message 返回 None 的各分支 + model-tracking 行，仓库 I-004 显式化口径）。</summary>
internal enum OpenClawLineOutcome
{
    /// <summary>产出 usage 记录。</summary>
    Produced,

    /// <summary>model_change / model-snapshot 状态行：更新跟踪状态，不计数。</summary>
    ModelChange,

    /// <summary>type 非 "message" 的行（LinesSkipped）。</summary>
    NotMessage,

    /// <summary>type=="message" 但 message 块缺失 / 非对象 / 内部字段类型不符（InvalidRecords）。</summary>
    MessageUnusable,

    /// <summary>非 assistant role / 缺 usage 块 / 四桶 + extra 全零（LinesSkipped）。</summary>
    NotUsagePayload,
}

/// <summary>单行映射产出（蓝本 parse_transcript_message → (entry, message_id)）。</summary>
internal sealed record OpenClawLineResult(OpenClawLineOutcome Outcome, UsageRecord? Record, string? EventId);

/// <summary>结构解析通过后的行载体（serde OpenClawLine 等价；仅携带后续分支需要的根元素与已校验字段）。</summary>
internal readonly record struct OpenClawParsedLine(JsonElement Root, string? Type, string? CustomType);

/// <summary>
/// OpenClaw 单文件 / 单事件解析（蓝本 ../ccusage/rust/adapters/openclaw/src/parser.rs 逐条移植，I-020；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。JSONL 行与 SQLite transcript_events 的
/// event_json 共用同一行映射（蓝本 OpenClawLine 双侧复用同构）：
/// <list type="bullet">
/// <item>行预过滤（蓝本 LinePrefilter::any）：不含 <c>"model_change"</c> / <c>"model-snapshot"</c> /
/// <c>"usage"</c> 片段的行直接跳过。</item>
/// <item>结构解析（serde 单次 from_slice 等价）：顶层非对象、<c>type</c> / <c>customType</c> 字段
/// 非(null/缺失/字符串) 为结构性失败（其余字段全部 lenient，永不失败——id/modelId/model/provider 为
/// non_empty_string，data/message 为 lenient_object，timestamp 为 Option&lt;Value&gt;）。</item>
/// <item>模型跟踪（蓝本 is_model_change / model_change_source）：type=="model_change" 或
/// (type=="custom" 且 customType=="model-snapshot")；model ← data 块（对象才有）modelId ?? model，
/// 无 data 块回退根级字段，provider 同理；model 缺失不覆盖当前状态（Some 语义）。</item>
/// <item>usage 行（蓝本 parse_transcript_message）：type=="message"、message.role 恰为 "assistant"
/// （原样比较）、usage 块存在且非全零；token 口径 input/output/cacheRead→cache_read/cacheWrite→
/// cache_creation（lenient_u64）；total 兜底（output 为 0 优先补 output，否则缺口并入 extra）；
/// 模型 ← message.modelId ?? message.model ?? 跟踪状态 ?? "unknown"，统一记录带 "[openclaw] " 前缀；
/// 时间戳 ← message.timestamp ?? 根级 timestamp（数值毫秒 / 严格 RFC3339 文本），回退文件 mtime
/// （JSONL）或 created_at（SQLite），再回退 UNIX 纪元；成本 ← usage.cost.total（存在即直采，
/// Auto 口径）。</item>
/// <item>身份：事件 id（行级 id，non_empty_string）仅 SQLite 行用作消息级去重标识（JSONL 时代记录
/// 无 id，蓝本 parse_message_entry 显式丢弃）；去重身份键见 <see cref="OpenClawAdapter"/>。</item>
/// </list>
/// 统一记录无 provider 字段：蓝本 data.version = provider（显示口径）在桌面侧无处落点，读后即弃
/// （见卡片偏差清单）；extra 并入 OutputTokens（I-006 系口径）；行级坏数据按上方 Outcome 显式计数。
/// </summary>
public sealed class OpenClawLogParser
{
    /// <summary>统一记录的采集来源名与项目路径（蓝本 UsageLoadAgent("OpenClaw") / project_path="OpenClaw"）。</summary>
    public const string AgentName = "OpenClaw";
    public const string ProjectPath = "OpenClaw";

    /// <summary>统一记录的模型前缀（蓝本 format!("[openclaw] {model}")）。</summary>
    public const string ModelPrefix = "[openclaw] ";

    /// <summary>缺省模型名（蓝本 parse_transcript_message 的 unwrap_or("unknown")）。</summary>
    public const string DefaultModel = "unknown";

    /// <summary>与蓝本 LinePrefilter::any 一致：行含任一片段才进入 JSON 解析。</summary>
    private static readonly string[] UsageMarkers = ["\"model_change\"", "\"model-snapshot\"", "\"usage\""];

    /// <summary>DateTimeOffset 可表示的 Unix 毫秒上限（9999-12-31）；超出按时间不可用回退（理论边界）。</summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>
    /// 解析单个 JSONL 会话文件（共享扫描引擎的逐文件委托）。打不开 / 读取中途失败交由
    /// <see cref="IncrementalJsonlScanner"/> 处理（计 LinesSkipped、不落账、下次重试）。
    /// 模型 / provider 跟踪状态仅在单文件内存活（蓝本 parse_session_file 局部变量同口径）。
    /// </summary>
    public async Task<JsonlFileParseOutcome> ParseFileAsync(string sourceFile, TextReader reader, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;
        string? currentModel = null;
        string? currentProvider = null;
        var sessionId = OpenClawLogLocator.SessionIdFromFileName(sourceFile);
        var fallbackTimestamp = GetFileTimestamp(sourceFile);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!ContainsAnyMarker(line))
            {
                linesSkipped++;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                malformedLines++;
                continue;
            }

            using (document)
            {
                if (ParseLine(document.RootElement) is not { } parsed)
                {
                    malformedLines++;
                    continue;
                }

                var result = MapLine(parsed, sessionId, currentModel, currentProvider, fallbackTimestamp, sourceFile, useEventIdAsMessageId: false);
                switch (result.Outcome)
                {
                    case OpenClawLineOutcome.Produced:
                        records.Add(result.Record!);
                        break;
                    case OpenClawLineOutcome.ModelChange:
                        var (model, provider) = ModelChangeSource(parsed);
                        if (model is not null)
                        {
                            currentModel = model;
                        }

                        if (provider is not null)
                        {
                            currentProvider = provider;
                        }

                        break;
                    case OpenClawLineOutcome.MessageUnusable:
                        invalidRecords++;
                        break;
                    default:
                        linesSkipped++;
                        break;
                }
            }
        }

        return new JsonlFileParseOutcome(records, FilesRead: 1, linesSkipped, malformedLines, invalidRecords, Interrupted: false);
    }

    /// <summary>
    /// 结构解析（蓝本 serde from_slice&lt;OpenClawLine&gt; 的结构性失败等价）：顶层非对象、
    /// <c>type</c> / <c>customType</c> 非(null/缺失/字符串) 返回 null（调用方计 Malformed）。
    /// </summary>
    internal static OpenClawParsedLine? ParseLine(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var (type, typeValid) = ReadOptionalString(root, "type");
        if (!typeValid)
        {
            return null;
        }

        var (customType, customTypeValid) = ReadOptionalString(root, "customType");
        if (!customTypeValid)
        {
            return null;
        }

        return new OpenClawParsedLine(root, type, customType);
    }

    /// <summary>model-tracking 行判定（蓝本 is_model_change）。</summary>
    internal static bool IsModelChange(in OpenClawParsedLine parsed)
    {
        return parsed.Type == "model_change"
               || (parsed.Type == "custom" && parsed.CustomType == "model-snapshot");
    }

    /// <summary>
    /// model-tracking 行携带的模型 / provider（蓝本 model_change_source）：data 块为对象时只读
    /// data 内字段（缺失不回退根级），否则读根级；model ← modelId ?? model。
    /// </summary>
    internal static (string? Model, string? Provider) ModelChangeSource(in OpenClawParsedLine parsed)
    {
        string? modelId;
        string? model;
        string? provider;
        if (LenientJson.GetObject(parsed.Root, "data") is { } data)
        {
            modelId = LenientJson.GetNonEmptyString(data, "modelId");
            model = LenientJson.GetNonEmptyString(data, "model");
            provider = LenientJson.GetNonEmptyString(data, "provider");
        }
        else
        {
            modelId = LenientJson.GetNonEmptyString(parsed.Root, "modelId");
            model = LenientJson.GetNonEmptyString(parsed.Root, "model");
            provider = LenientJson.GetNonEmptyString(parsed.Root, "provider");
        }

        return (modelId ?? model, provider);
    }

    /// <summary>
    /// 行映射（蓝本 parse_transcript_message，JSONL 与 SQLite 共用）。
    /// <paramref name="useEventIdAsMessageId"/>：SQLite 行用事件 id 作消息级去重标识
    /// （蓝本 sqlite_entry_to_loaded），JSONL 行恒为 null（蓝本 parse_message_entry 丢弃）。
    /// </summary>
    internal static OpenClawLineResult MapLine(
        in OpenClawParsedLine parsed,
        string sessionId,
        string? currentModel,
        string? currentProvider,
        DateTimeOffset fallbackTimestamp,
        string sourceFile,
        bool useEventIdAsMessageId)
    {
        if (IsModelChange(parsed))
        {
            return new OpenClawLineResult(OpenClawLineOutcome.ModelChange, null, null);
        }

        if (parsed.Type != "message")
        {
            return new OpenClawLineResult(OpenClawLineOutcome.NotMessage, null, null);
        }

        // message 块（蓝本 lenient_object<Option<OpenClawMessage>>）：缺失/null/非对象 → 不可用；
        // 对象内 role / usage 非(null/缺失/期望类型) → 结构体反序列化失败 → 不可用（其余字段 lenient）。
        if (LenientJson.GetObject(parsed.Root, "message") is not { } message)
        {
            return new OpenClawLineResult(OpenClawLineOutcome.MessageUnusable, null, null);
        }

        string? role = null;
        if (message.TryGetProperty("role", out var roleElement))
        {
            if (roleElement.ValueKind == JsonValueKind.String)
            {
                role = roleElement.GetString();
            }
            else if (roleElement.ValueKind != JsonValueKind.Null)
            {
                return new OpenClawLineResult(OpenClawLineOutcome.MessageUnusable, null, null);
            }
        }

        JsonElement? usage = null;
        if (message.TryGetProperty("usage", out var usageElement))
        {
            if (usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = usageElement;
            }
            else if (usageElement.ValueKind != JsonValueKind.Null)
            {
                return new OpenClawLineResult(OpenClawLineOutcome.MessageUnusable, null, null);
            }
        }

        if (role != "assistant")
        {
            return new OpenClawLineResult(OpenClawLineOutcome.NotUsagePayload, null, null);
        }

        if (usage is not { } usageValue)
        {
            return new OpenClawLineResult(OpenClawLineOutcome.NotUsagePayload, null, null);
        }

        // 蓝本全程 u64 + saturating；读取层把 u64 溢出钳到 long.MaxValue（token 计数不可达域）。
        var inputTokens = LenientJson.GetU64(usageValue, "input");
        var outputTokens = LenientJson.GetU64(usageValue, "output");
        var cacheReadTokens = LenientJson.GetU64(usageValue, "cacheRead");
        var cacheCreationTokens = LenientJson.GetU64(usageValue, "cacheWrite");
        var totalTokens = LenientJson.GetU64(usageValue, "totalTokens");
        var usageRaw = new RawTokenUsage(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
        var (resolved, extraTotalTokens) = TokenUsageFallback.Apply(usageRaw, 0, totalTokens);
        if (resolved.TotalUsageTokens().SaturatingAddClamped(extraTotalTokens) == 0)
        {
            return new OpenClawLineResult(OpenClawLineOutcome.NotUsagePayload, null, null);
        }

        // 蓝本 total_tokens = usage.total_tokens.max(四桶和 + extra)；extra 加载层余量并入
        // OutputTokens（I-006 系口径）。
        var totalTokensFinal = Math.Max(totalTokens, resolved.TotalUsageTokens().SaturatingAddClamped(extraTotalTokens));

        // 时间戳：message.timestamp 优先（null 视为缺失落到根级），数值毫秒 / 严格 RFC3339 文本；
        // 不可用回退 fallbackTimestamp（JSONL 文件 mtime / SQLite created_at）。
        var timestampElement = GetTimestampElement(message, "timestamp") ?? GetTimestampElement(parsed.Root, "timestamp");
        var timestamp = TimestampFromElement(timestampElement) ?? fallbackTimestamp;

        var model = LenientJson.GetNonEmptyString(message, "modelId")
                    ?? LenientJson.GetNonEmptyString(message, "model")
                    ?? currentModel
                    ?? DefaultModel;

        // 蓝本 cost ← usage.cost.total（lenient：cost 非对象 / total 非数值 → None）。
        decimal? cost = null;
        if (LenientJson.GetObject(usageValue, "cost") is { } costElement
            && LenientJson.GetF64(costElement, "total") is { } totalCost)
        {
            cost = ToStoredCost(totalCost);
        }

        var eventId = LenientJson.GetNonEmptyString(parsed.Root, "id");
        var record = new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = sessionId,
            Model = $"{ModelPrefix}{model}",
            // 计价走蓝本 OC-1 两级候选链（I-024 接线）：装饰名 "[openclaw] m" 仅精确参与（命中按
            // 装饰名计价），未命中回退裸模型名（raw pricing identity）；匹配走向
            // ExactThenFamilyAcrossCandidates——exact 全候选优先（裸名精确不高于装饰名精确），
            // 家族兜底只落在裸名档（蓝本装饰名走 find_exact，从不家族/模糊命中）。
            Pricing = new UsagePricingPlan(
                [$"{ModelPrefix}{model}", model],
                PricingCandidateWalk.ExactThenFamilyAcrossCandidates),
            // 蓝本 CostMode::Auto：cost 存在即采用（含 0），缺失才交定价引擎。
            CostUsd = cost,
            InputTokens = resolved.InputTokens,
            OutputTokens = RawTokenUsage.SaturatingAdd(resolved.OutputTokens, extraTotalTokens),
            CacheCreationTokens = resolved.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = resolved.CacheReadTokens,
            IsSidechain = false,
            MessageId = useEventIdAsMessageId ? eventId : null,
            RequestId = null,
            SourceFile = sourceFile,
        };
        return new OpenClawLineResult(OpenClawLineOutcome.Produced, record, eventId);
    }

    /// <summary>
    /// 时间戳元素读取（蓝本 timestamp_from_value）：整数毫秒（u64 超 i64 域 → 不可用）、浮点截断毫秒
    /// （Rust 饱和转换语义：NaN→0）、严格 RFC3339 文本；负值 / 不可解析 → null（调用方回退）。
    /// </summary>
    internal static DateTimeOffset? TimestampFromElement(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        long? millis;
        if (value.ValueKind != JsonValueKind.Number)
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
            {
                return null;
            }

            return StrictRfc3339Timestamp.TryParse(text, out var parsed) ? parsed : null;
        }

        if (value.TryGetInt64(out var signed))
        {
            millis = signed;
        }
        else if (value.TryGetUInt64(out var unsigned))
        {
            millis = unsigned <= long.MaxValue ? (long)unsigned : null;
        }
        else
        {
            millis = SaturatingToI64(value.GetDouble());
        }

        // 蓝本 timestamp_from_millis：负值 → None（回退）；DateTimeOffset 表示域外的理论边界同样回退
        // （蓝本 jiff 可表示更大年限，真实数据不可达，登记偏差）。
        if (millis is not { } raw || raw < 0 || raw > MaxUnixMilliseconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(raw);
    }

    /// <summary>JSONL 文件 mtime（蓝本 file_modified_timestamp）：毫秒截断；不可得回退 UNIX 纪元。</summary>
    internal static DateTimeOffset GetFileTimestamp(string sourceFile)
    {
        try
        {
            var info = new FileInfo(sourceFile);
            if (info.Exists)
            {
                return DateTimeOffset.UnixEpoch.AddMilliseconds(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落到下方纪元回退（蓝本 unwrap_or(TimestampMs::UNIX_EPOCH)）。
        }

        return StrictRfc3339Timestamp.Epoch;
    }

    /// <summary>载荷 cost → decimal（蓝本 Auto 语义：存在即采用，含 0；非有限按缺失——I-017 同款换算）。</summary>
    internal static decimal? ToStoredCost(double cost)
    {
        if (!double.IsFinite(cost))
        {
            return null;
        }

        // f64 → decimal：用最短往返文本解析保持常见值（0.02）的精确十进制形态。
        if (decimal.TryParse(cost.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        try
        {
            return Convert.ToDecimal(cost, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>timestamp 元素选取：缺失 / null 视为不存在（蓝本 Option&lt;Value&gt; 的 None）。</summary>
    private static JsonElement? GetTimestampElement(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null
            ? element
            : null;
    }

    /// <summary>type / customType 读取（蓝本 Option&lt;String&gt;：缺失 / null → None；非字符串 → 结构性失败）。</summary>
    private static (string? Value, bool Valid) ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (null, true);
        }

        return element.ValueKind == JsonValueKind.String ? (element.GetString(), true) : (null, false);
    }

    /// <summary>Rust 浮点 → i64 饱和转换（`as i64`）：NaN→0、±∞→边界、其余截断。</summary>
    private static long SaturatingToI64(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(value))
        {
            return long.MaxValue;
        }

        if (double.IsNegativeInfinity(value))
        {
            return long.MinValue;
        }

        var truncated = Math.Truncate(value);
        return truncated <= long.MinValue ? long.MinValue
            : truncated >= long.MaxValue ? long.MaxValue
            : (long)truncated;
    }

    /// <summary>与蓝本 LinePrefilter::any 一致：行含任一标记片段。</summary>
    private static bool ContainsAnyMarker(string line)
    {
        foreach (var marker in UsageMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
