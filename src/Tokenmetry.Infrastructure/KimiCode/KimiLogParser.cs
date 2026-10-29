using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.SqliteSource;

namespace Tokenmetry.Infrastructure.KimiCode;

/// <summary>
/// Kimi Code 单文件解析（蓝本 ../ccusage/rust/adapters/kimi/src/parser.rs 逐条移植，I-013；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。每个 sessions/ 下的 wire.jsonl 逐行解析为
/// 统一 <see cref="UsageRecord"/>，新旧双格式共用一套信封（蓝本 KimiWireLine：仅声明 ccusage 消费的
/// 字段，其余跳过）：
/// <list type="bullet">
/// <item>行预过滤（蓝本 LinePrefilter::any）：不含 <c>"token_usage"</c>（旧格式）且不含
/// <c>"usage.record"</c>（新格式）片段的行直接跳过。</item>
/// <item>模型（蓝本 read_model_from_config）：从 wire 路径向上定位 Kimi 根目录（统一口径：旧布局
/// 自 wire 文件 4 次 GetDirectoryName / 自 wire 所在目录 3 级；agents 布局自 wire 所在目录 5 级）
/// 读 config.json 的 model 字段（non_empty_string），不可得回退 "kimi-for-coding"
/// （蓝本 DEFAULT_MODEL）。</item>
/// <item>旧格式（type 非 usage.record/metadata）：message.type 恰为 "StatusUpdate"（trim 后比较）且
/// message.payload.token_usage 存在；token 口径 input←input_other、output←output、
/// cache_creation←input_cache_creation、cache_read←input_cache_read（lenient_u64）；total 兜底
/// （output 为 0 补 output、否则缺口并入 extra）；时间戳 timestamp 为 lenient_f64 秒
/// （毫秒 = 截断(seconds×1000)，非有限/越界回退 mtime）。</item>
/// <item>新格式（type 恰为 "usage.record"）：usageScope 恰为 "turn"（session 级为累计总量，跳过）；
/// token 口径 input←inputOther、output←output、cache_creation←inputCacheCreation、
/// cache_read←inputCacheRead；时间戳 time 为 lenient_i64 毫秒；模型取行内 model 并剥离
/// "kimi-code/" 前缀；message id 恒无。</item>
/// <item>会话 id（蓝本 extract_session_id）：从 wire 路径推导（agents 布局取 session 目录名，旧布局取
/// wire 所在目录名），空白回退 "unknown"。</item>
/// </list>
/// 全零（四桶 + extra = 0）的行不产出。成本落点：统一记录 CostUsd 恒 null 交定价引擎（蓝本
/// calculate_kimi_cost 的 moonshot/、kimi/ 前缀候选计价与缺省模型按时间戳选 kimi-k2.5/k2.6 定价档
/// 属 I-024 端到端欠账，见卡片偏差①）；extra 并入统一记录 OutputTokens（仓库口径，蓝本 CLI 列
/// 不含 extra 的显示差异 fail-loud，见卡片偏差②）。行级容错为仓库 I-004 口径（蓝本静默跳过处
/// 显式计数）。
/// </summary>
public sealed class KimiLogParser
{
    public const string AgentName = "Kimi";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Kimi"）。</summary>
    public const string ProjectPath = "Kimi";

    /// <summary>缺省模型名（蓝本 DEFAULT_MODEL）。</summary>
    public const string DefaultModel = "kimi-for-coding";

    /// <summary>与 kimi.rs DEFAULT_PROVIDER 一致：候选链的默认 provider 前缀。</summary>
    public const string DefaultProvider = "moonshot";

    /// <summary>
    /// 与 kimi.rs KIMI_FOR_CODING_K2_6_CUTOFF_MS 一致：该毫秒时刻及之后 `kimi-for-coding`
    /// 按 kimi-k2.6 计价，之前按 kimi-k2.5（模型名本身不含版本号，只能按时间切档）。
    /// </summary>
    public const long KimiForCodingK26CutoffUnixMs = 1_776_698_890_072;

    /// <summary>新格式模型前缀（蓝本 wire_line_to_entry_new 的 strip_prefix）。</summary>
    private const string KimiCodeModelPrefix = "kimi-code/";

    /// <summary>与蓝本 LinePrefilter::any 一致：行含任一片段才进入 JSON 解析。</summary>
    private static readonly string[] UsageMarkers = ["\"token_usage\"", "\"usage.record\""];

    /// <summary>
    /// 解析单个 wire 文件（共享扫描引擎的逐文件委托）。打不开 / 读取中途失败交由
    /// <see cref="IncrementalJsonlScanner"/> 处理（计 LinesSkipped、不落账、下次重试）。
    /// </summary>
    public async Task<JsonlFileParseOutcome> ParseFileAsync(string sourceFile, TextReader reader, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;

        var model = ReadModelFromConfig(sourceFile);
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
                var record = ParseLine(document.RootElement, sourceFile, model, fallbackTimestamp);
                if (record is null)
                {
                    invalidRecords++;
                    continue;
                }

                records.Add(record);
            }
        }

        return new JsonlFileParseOutcome(records, FilesRead: 1, linesSkipped, malformedLines, invalidRecords, Interrupted: false);
    }

    /// <summary>解析一行已通过预过滤的 JSON。返回 null 表示该行被结构/语义校验拒绝（计 InvalidRecords）。</summary>
    internal UsageRecord? ParseLine(JsonElement root, string sourceFile, string configModel, DateTimeOffset fallbackTimestamp)
    {
        // serde 单次 from_slice::<KimiWireLine>：顶层非对象为结构性反序列化失败（Claude/Droid 口径）。
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = LenientJson.GetNonEmptyString(root, "type");
        if (type == "usage.record")
        {
            return ParseNewFormatLine(root, sourceFile, fallbackTimestamp);
        }

        if (type == "metadata")
        {
            // 蓝本 wire_line_to_entry 对 metadata 显式跳过（带 marker 的 metadata 行才走到这里）。
            return null;
        }

        return ParseOldFormatLine(root, sourceFile, configModel, fallbackTimestamp);
    }

    /// <summary>旧格式行（蓝本 wire_line_to_entry_old）。</summary>
    private UsageRecord? ParseOldFormatLine(
        JsonElement root, string sourceFile, string configModel, DateTimeOffset fallbackTimestamp)
    {
        // message / payload / token_usage 均为严格 Option<结构体>：null/缺失 → None（拒绝该行）；
        // 非对象（标量/数组等）→ serde 结构性失败（同样拒绝该行）。
        if (LenientJson.GetObject(root, "message") is not { } message)
        {
            return null;
        }

        if (LenientJson.GetNonEmptyString(message, "type") != "StatusUpdate")
        {
            return null;
        }

        if (LenientJson.GetObject(message, "payload") is not { } payload)
        {
            return null;
        }

        if (LenientJson.GetObject(payload, "token_usage") is not { } tokenUsage)
        {
            return null;
        }

        var usage = new RawTokenUsage(
            LenientJson.GetU64(tokenUsage, "input_other"),
            LenientJson.GetU64(tokenUsage, "output"),
            LenientJson.GetU64(tokenUsage, "input_cache_creation"),
            LenientJson.GetU64(tokenUsage, "input_cache_read"));
        var (resolved, extraTotalTokens) = TokenUsageFallback.Apply(usage, 0, LenientJson.GetU64(tokenUsage, "total"));
        if (resolved.TotalUsageTokens() + extraTotalTokens == 0)
        {
            return null;
        }

        var timestamp = TimestampFromSeconds(LenientJson.GetF64(root, "timestamp")) ?? fallbackTimestamp;
        return BuildRecord(
            sourceFile, timestamp, configModel, resolved, extraTotalTokens,
            LenientJson.GetNonEmptyString(payload, "message_id"));
    }

    /// <summary>新格式行（蓝本 wire_line_to_entry_new）：仅聚合 turn 级记录，session 级为累计总量。</summary>
    private UsageRecord? ParseNewFormatLine(JsonElement root, string sourceFile, DateTimeOffset fallbackTimestamp)
    {
        if (LenientJson.GetNonEmptyString(root, "usageScope") != "turn")
        {
            return null;
        }

        if (LenientJson.GetObject(root, "usage") is not { } usageElement)
        {
            return null;
        }

        var usage = new RawTokenUsage(
            LenientJson.GetU64(usageElement, "inputOther"),
            LenientJson.GetU64(usageElement, "output"),
            LenientJson.GetU64(usageElement, "inputCacheCreation"),
            LenientJson.GetU64(usageElement, "inputCacheRead"));
        // 蓝本 apply_total_token_fallback(usage, 0, 0)：新格式无 total，调用为恒等（保真保留）。
        var (resolved, extraTotalTokens) = TokenUsageFallback.Apply(usage, 0, 0);
        if (resolved.TotalUsageTokens() + extraTotalTokens == 0)
        {
            return null;
        }

        var timestamp = LenientJson.GetI64(root, "time") is { } millis
            ? DateTimeOffset.UnixEpoch.AddMilliseconds(millis)
            : fallbackTimestamp;
        var model = LenientJson.GetNonEmptyString(root, "model") ?? DefaultModel;
        if (model.StartsWith(KimiCodeModelPrefix, StringComparison.Ordinal))
        {
            model = model[KimiCodeModelPrefix.Length..];
        }

        return BuildRecord(sourceFile, timestamp, model, resolved, extraTotalTokens, messageId: null);
    }

    private static UsageRecord BuildRecord(
        string sourceFile, DateTimeOffset timestamp, string model, RawTokenUsage usage, long extraTotalTokens, string? messageId)
    {
        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = KimiLogLocator.SessionIdFromWirePath(sourceFile),
            Model = model,
            CostUsd = null,
            // 蓝本 calculate_kimi_cost 的候选链：kimi-for-coding 按时间切到具体 k2.x，
            // 其余模型走 provider/、kimi/ 前缀与裸名——裸名 kimi-for-coding 在内置快照里不存在，
            // 不填候选链该源就整源未定价。
            Pricing = BuildPricingPlan(model, timestamp),
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens.SaturatingAddClamped(extraTotalTokens),
            CacheCreationTokens = usage.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = usage.CacheReadTokens,
            IsSidechain = false,
            MessageId = messageId,
            RequestId = null,
            SourceFile = sourceFile,
        };
    }

    /// <summary>与 kimi.rs model_candidates 一致（去重保序）。</summary>
    private static UsagePricingPlan BuildPricingPlan(string model, DateTimeOffset timestamp)
    {
        var candidates = new List<string>(4);
        if (string.Equals(model, DefaultModel, StringComparison.Ordinal))
        {
            candidates.Add(timestamp.ToUnixTimeMilliseconds() < KimiForCodingK26CutoffUnixMs
                ? "moonshot/kimi-k2.5"
                : "moonshot/kimi-k2.6");
        }

        candidates.Add($"{DefaultProvider}/{model}");
        candidates.Add($"kimi/{model}");
        candidates.Add(model);

        return new UsagePricingPlan(candidates.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>旧格式 timestamp（蓝本 timestamp_from_seconds）：非有限 / 越界回退 null（调用方回退 mtime）。</summary>
    private static DateTimeOffset? TimestampFromSeconds(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        var millis = Math.Truncate(value * 1000.0);
        if (millis < long.MinValue || millis > long.MaxValue)
        {
            return null;
        }

        return DateTimeOffset.UnixEpoch.AddMilliseconds(millis);
    }

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

    /// <summary>
    /// 从 Kimi 根目录的 config.json 读显示模型（蓝本 read_model_from_config）：根目录 / 文件 / JSON
    /// / model 字段任一不可得回退 <see cref="DefaultModel"/>。
    /// </summary>
    internal static string ReadModelFromConfig(string wirePath)
    {
        var root = KimiLogLocator.RootFromWirePath(wirePath);
        if (root is null)
        {
            return DefaultModel;
        }

        string content;
        try
        {
            content = File.ReadAllText(Path.Combine(root, "config.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultModel;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? LenientJson.GetNonEmptyString(document.RootElement, "model") ?? DefaultModel
                : DefaultModel;
        }
        catch (JsonException)
        {
            return DefaultModel;
        }
    }

    /// <summary>文件 mtime（蓝本 file_modified_timestamp）：毫秒截断；不可得回退 UNIX 纪元。</summary>
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
}
