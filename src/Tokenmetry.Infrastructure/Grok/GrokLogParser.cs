using System.Text;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.SqliteSource;

namespace Tokenmetry.Infrastructure.Grok;

/// <summary>
/// Grok updates.jsonl 解析（蓝本 ../ccusage/rust/adapters/grok/src/parser.rs 逐条移植，I-022；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。行式 JSONL，行预过滤须含
/// "turn_completed" 片段；仅 sessionUpdate 恰为 "turn_completed" 且 usage 为对象的行出账：
/// <list type="bullet">
/// <item><b>modelUsage 逐模型拆行</b>（蓝本 model_usage_rows）：map 非空时逐键产出行（键 Ordinal
/// 排序，与蓝本 sort_by 同口径）；缺失/null/<b>空 map</b> 回退单行——顶层 usage 字段 + 会话默认模型
/// （summary.json current_model_id，缺省 "unknown"）。</item>
/// <item><b>token 拆分</b>（蓝本 split_input_tokens）：OpenAI 风格 inputTokens 含缓存——
/// cache_read = min(cachedReadTokens, input)、uncached = input − cache_read；cache_creation 后到、
/// 恒为 uncached 子集：min(cacheCreationTokens, uncached) 再从 uncached 扣除（三段还原 input）。
/// reasoningTokens 已含在 outputTokens 内——<b>丢弃</b>（extra 恒 0，加回会二次计费）；五桶（含
/// reasoning）全零跳过（reasoning&gt;0 单独成行、token 全零）。</item>
/// <item><b>成本</b>（蓝本 cost_usd_from_ticks）：costUsdTicks 为定点 USD，1 tick = 1e-10 USD——
/// &gt;0 时 decimal 精确换算（从 long 构造，不经 double）入统一记录 CostUsd（LogCost 短路，与 CLI
/// display/auto 优先采纳发票成本一致）；无 ticks 交定价引擎（I-024 起带蓝本 pricing_candidates
/// 候选链与 exact-优先匹配走向，见 <see cref="BuildPricingCandidates"/>）。</item>
/// <item><b>时间戳</b>（蓝本 resolve_timestamp_ms）：_meta.agentTimestampMs（整数或浮点截断，
/// Rust `as i64` saturating 语义）&gt;0 → 毫秒；envelope timestamp（Unix 秒）&gt;0 → ×1000
/// （saturating）；否则 UNIX 纪元。</item>
/// <item><b>会话元数据</b>（蓝本 load_session_meta）：会话目录名为默认 id、项目目录名 URL 轻量解码
/// （%XX 逐字节解码后 UTF-8 宽松还原）为默认项目路径；summary.json 的 info.id / info.cwd ??
/// git_root_dir / current_model_id 依次覆盖；行内 params.sessionId 优先于 summary id。</item>
/// <item><b>去重</b>（蓝本 dedupe_key + loader 全局去重）：文件内有 eventId → "eventId|模型"，无 →
/// 内容键（session|毫秒|模型|in|out|cr|cc|reasoning——reasoning 不入统一记录，内容键须在解析层比较），
/// seen 集合 first-wins；跨文件全局 "eventId|模型" first-wins 由适配器注入共享扫描引擎的去重钩子
/// 承担（蓝本 loader 同键同序）。</item>
/// </list>
/// 行级容错为仓库 I-004 口径（蓝本静默跳过处显式计数）：params/update/usage 为标量/数组、modelUsage
/// 非对象或其值为标量（serde 结构性失败）计 Malformed；非 turn_completed / 缺 usage / 全零 / 文件内
/// 去重折叠计 LinesSkipped；时间戳有纪元兜底，InvalidRecords 无对应场景恒 0。
/// </summary>
public sealed class GrokLogParser
{
    public const string AgentName = "Grok";

    /// <summary>项目名占位（蓝本 LoadedEntry.project = "grok"；桌面统一记录 ProjectPath 取会话元数据）。</summary>
    public const string ProjectName = "grok";

    /// <summary>顶层 usage 回退时的缺省模型名（蓝本 model_usage_rows 的 "unknown"）。</summary>
    public const string UnknownModel = "unknown";

    /// <summary>1 tick = 1e-10 USD（蓝本 COST_USD_TICKS_PER_USD = 1e10 ticks/USD）。</summary>
    internal const decimal TicksPerUsd = 10_000_000_000m;

    /// <summary>与蓝本 LinePrefilter::all(["\"turn_completed\""]) 一致。</summary>
    private const string TurnCompletedMarker = "\"turn_completed\"";

    /// <summary>
    /// 解析单个 updates.jsonl（共享扫描引擎的逐文件委托）。打不开 / 读取中途失败交由
    /// <see cref="IncrementalJsonlScanner"/> 处理（计 LinesSkipped、不落账、下次重试）。
    /// </summary>
    public async Task<JsonlFileParseOutcome> ParseFileAsync(string sourceFile, TextReader reader, CancellationToken ct)
    {
        var meta = LoadSessionMeta(sourceFile);
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.Contains(TurnCompletedMarker, StringComparison.Ordinal))
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
                var produced = ParseLine(document.RootElement, meta, seen, sourceFile, out var skipped, out var dedupeSkipped);
                if (produced is null)
                {
                    // serde 单次 from_slice::<GrokUpdateLine>：params/update/usage 标量、modelUsage
                    // 非对象或值为标量 → 整行反序列化失败（结构性，仓库口径计 Malformed）。
                    malformedLines++;
                    continue;
                }

                // 蓝本静默 continue 处（缺 params/update/usage、非 turn_completed、全零行）与文件内
                // dedupe_key 折叠行均显式计数（跨文件全局折叠由扫描器钩子计 DuplicateRecords）。
                linesSkipped += skipped + dedupeSkipped;
                if (produced.Count > 0)
                {
                    records.AddRange(produced);
                }
            }
        }

        return new JsonlFileParseOutcome(records, FilesRead: 1, linesSkipped, malformedLines, InvalidRecords: 0, Interrupted: false);
    }

    /// <summary>
    /// 解析单行（蓝本 parse_session_files 行循环逐条）。返回 null = 结构性失败（serde 整行拒绝）；
    /// 否则 <paramref name="skipped"/> 为本行语义跳过数（1 = 整行跳过、modelUsage 全零行逐行计），
    /// <paramref name="dedupeSkipped"/> 为文件内 dedupe_key 折叠数；列表为产出的统一记录
    /// （modelUsage 每模型一条）。
    /// </summary>
    internal List<UsageRecord>? ParseLine(
        JsonElement root,
        GrokSessionMeta meta,
        HashSet<string> seen,
        string sourceFile,
        out int skipped,
        out int dedupeSkipped)
    {
        skipped = 0;
        dedupeSkipped = 0;
        // serde 结构性失败面：顶层非对象、params/update/usage 标量（Option<结构体> 反序列化失败）、
        // modelUsage 非对象或其值非对象（HashMap<String, GrokModelUsage> 反序列化失败）。
        // timestamp / agentTimestampMs 为 Option<Value>（任意 JSON 值均合法，非数字在取值时返回 None）。
        if (root.ValueKind != JsonValueKind.Object
            || IsScalar(root, "params"))
        {
            return null;
        }

        if (LenientJson.GetObject(root, "params") is not { } parameters)
        {
            // params 为 null / 缺失 → serde Option None → 行跳过。
            skipped++;
            return [];
        }

        var update = LenientJson.GetObject(parameters, "update");
        if (update is null)
        {
            if (IsScalar(parameters, "update"))
            {
                return null;
            }

            skipped++;
            return [];
        }

        if (LenientJson.GetNonEmptyString(update.Value, "sessionUpdate") != "turn_completed")
        {
            skipped++;
            return [];
        }

        if (IsScalar(update.Value, "usage"))
        {
            return null;
        }

        var usage = LenientJson.GetObject(update.Value, "usage");
        if (usage is null)
        {
            // update.usage 为 null/缺失 → serde Option None → 蓝本 `let Some(usage)` 不过，行跳过。
            skipped++;
            return [];
        }

        if (IsScalar(usage.Value, "modelUsage")
            || (LenientJson.GetObject(usage.Value, "modelUsage") is { } modelUsageObject
                && modelUsageObject.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Object)))
        {
            return null;
        }

        var metaElement = LenientJson.GetObject(parameters, "_meta");
        var eventId = metaElement is null ? null : LenientJson.GetNonEmptyString(metaElement.Value, "eventId");
        var timestamp = ResolveTimestamp(root, parameters);
        var sessionId = LenientJson.GetNonEmptyString(parameters, "sessionId") ?? meta.SessionId;

        var produced = new List<UsageRecord>();
        foreach (var (rawModel, modelUsage) in ModelUsageRows(usage.Value, meta.DefaultModel))
        {
            var (uncached, cacheRead, cacheCreation) = SplitInputTokens(
                modelUsage.InputTokens, modelUsage.CachedReadTokens, modelUsage.CacheCreationTokens);
            var outputTokens = modelUsage.OutputTokens;
            var reasoningTokens = modelUsage.ReasoningTokens;
            if (uncached == 0 && cacheRead == 0 && cacheCreation == 0 && outputTokens == 0 && reasoningTokens == 0)
            {
                skipped++;
                continue;
            }

            // 蓝本 dedupe_key：有 eventId → "eventId|模型"；无 → 内容键（session|毫秒|模型|四桶|reasoning）。
            var dedupeKey = eventId is not null
                ? $"{eventId}|{rawModel}"
                : $"{sessionId}|{timestamp.ToUnixTimeMilliseconds()}|{rawModel}|{uncached}|{outputTokens}|{cacheRead}|{cacheCreation}|{reasoningTokens}";
            if (!seen.Add(dedupeKey))
            {
                dedupeSkipped++;
                continue;
            }

            var ticks = modelUsage.CostUsdTicks;
            produced.Add(new UsageRecord
            {
                Timestamp = timestamp,
                Agent = AgentName,
                ProjectPath = meta.ProjectPath,
                SessionId = sessionId,
                Model = rawModel,
                // 计价走蓝本 pricing_candidates 候选链（I-024 接线，GK-1/GK-2）：剥 "[grok] " 前缀、
                // 剥 "-build" 后缀、补 xai/ 与 x-ai/ 前缀；匹配走向为蓝本 calculate_grok_cost_at 的
                // "exact 全候选优先、再 fuzzy"（ExactThenFamilyAcrossCandidates）。
                Pricing = new UsagePricingPlan(
                    BuildPricingCandidates(rawModel),
                    PricingCandidateWalk.ExactThenFamilyAcrossCandidates),
                CostUsd = ticks > 0 ? ticks / TicksPerUsd : null,
                InputTokens = uncached,
                OutputTokens = outputTokens,
                CacheCreationTokens = cacheCreation,
                CacheCreationEphemeral5mTokens = 0,
                CacheCreationEphemeral1hTokens = 0,
                CacheReadTokens = cacheRead,
                IsSidechain = false,
                MessageId = eventId,
                RequestId = eventId,
                SourceFile = sourceFile,
            });
        }

        return produced;
    }

    /// <summary>modelUsage 逐模型拆行（蓝本 model_usage_rows）：map 非空逐键（Ordinal 排序）；缺失/null/
    /// 空 map 回退顶层 usage 单行（模型 = 会话默认模型 ?? "unknown"）。</summary>
    internal static List<(string Model, GrokModelUsage Usage)> ModelUsageRows(JsonElement usage, string? defaultModel)
    {
        if (LenientJson.GetObject(usage, "modelUsage") is { } modelUsage
            && modelUsage.EnumerateObject().Any())
        {
            return modelUsage.EnumerateObject()
                .Select(property => (property.Name, ReadModelUsage(property.Value)))
                .OrderBy(row => row.Name, StringComparer.Ordinal)
                .ToList();
        }

        return [(defaultModel ?? UnknownModel, ReadModelUsage(usage))];
    }

    /// <summary>
    /// 计价模型候选（蓝本 pricing_candidates 逐字移植，I-024 接线 GK-1/GK-2）：剥 "[grok] " 前缀
    /// （GK-2）后依次为 [stripped, "xai/{stripped}", "x-ai/{stripped}", 剥 "-build" 后缀的
    /// normalized, "xai/{normalized}", "x-ai/{normalized}"]，相邻去重保序；剥前缀后为空串则无候选。
    /// 匹配走向（蓝本 calculate_grok_cost_at）：exact 全候选优先、再 fuzzy——桌面口径为
    /// <see cref="PricingCandidateWalk.ExactThenFamilyAcrossCandidates"/>。
    /// </summary>
    public static IReadOnlyList<string> BuildPricingCandidates(string rawModel)
    {
        var candidates = new List<string>();
        void Push(string value)
        {
            if (!candidates.Contains(value))
            {
                candidates.Add(value);
            }
        }

        var stripped = rawModel.StartsWith("[grok] ", StringComparison.Ordinal)
            ? rawModel["[grok] ".Length..].Trim()
            : rawModel.Trim();
        if (stripped.Length == 0)
        {
            return candidates;
        }

        var normalized = stripped.EndsWith("-build", StringComparison.Ordinal)
            ? stripped[..^"-build".Length]
            : stripped;

        Push(stripped);
        Push($"xai/{stripped}");
        Push($"x-ai/{stripped}");
        Push(normalized);
        Push($"xai/{normalized}");
        Push($"x-ai/{normalized}");
        return candidates;
    }

    private static GrokModelUsage ReadModelUsage(JsonElement element)
    {
        return new GrokModelUsage(
            LenientJson.GetU64(element, "inputTokens"),
            LenientJson.GetU64(element, "outputTokens"),
            LenientJson.GetU64(element, "cachedReadTokens"),
            LenientJson.GetU64(element, "cacheCreationTokens"),
            LenientJson.GetU64(element, "reasoningTokens"),
            LenientJson.GetU64(element, "totalTokens"),
            LenientJson.GetU64(element, "costUsdTicks"));
    }

    /// <summary>会话元数据（蓝本 load_session_meta）：目录名推导 + summary.json 覆盖（任一读取/解析
    /// 失败回退目录推导缺省）。</summary>
    internal static GrokSessionMeta LoadSessionMeta(string updatesPath)
    {
        var full = Path.GetFullPath(updatesPath);
        var sessionDirectory = Path.GetDirectoryName(full);
        var sessionId = DirectoryNameOr(sessionDirectory, "unknown");
        var projectDirectory = sessionDirectory is null ? null : Path.GetDirectoryName(sessionDirectory);
        var projectPath = projectDirectory is null || Path.GetFileName(projectDirectory) is not { } projectSegment
            || projectSegment.Length == 0
            ? "unknown"
            : UrlDecodeLightweight(projectSegment);

        string? defaultModel = null;
        var summaryPath = GrokLogLocator.SiblingSummary(updatesPath);
        if (summaryPath is not null)
        {
            string content;
            try
            {
                content = File.ReadAllText(summaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                content = string.Empty;
            }

            if (content.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && !IsScalar(root, "info"))
                    {
                        // 蓝本 from_str::<GrokSummary>：info 为标量/数组 → 整份摘要反序列化失败 →
                        // 全部用目录推导缺省。
                        if (LenientJson.GetObject(root, "info") is { } info)
                        {
                            if (LenientJson.GetNonEmptyString(info, "id") is { } summaryId)
                            {
                                sessionId = summaryId;
                            }

                            var cwd = LenientJson.GetNonEmptyString(info, "cwd")
                                      ?? LenientJson.GetNonEmptyString(root, "git_root_dir");
                            if (cwd is not null)
                            {
                                projectPath = cwd;
                            }
                        }
                        else if (LenientJson.GetNonEmptyString(root, "git_root_dir") is { } gitRoot)
                        {
                            projectPath = gitRoot;
                        }

                        defaultModel = LenientJson.GetNonEmptyString(root, "current_model_id");
                    }
                }
                catch (JsonException)
                {
                    // 蓝本 from_str 失败 → 全部用目录推导缺省。
                }
            }
        }

        return new GrokSessionMeta(sessionId, projectPath, defaultModel);
    }

    private static string DirectoryNameOr(string? directory, string fallback)
    {
        var name = directory is null ? null : Path.GetFileName(directory);
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    /// <summary>时间戳解析（蓝本 resolve_timestamp_ms）：agentTimestampMs 毫秒 &gt;0 优先，envelope
    /// Unix 秒 &gt;0 ×1000（saturating），否则 UNIX 纪元。</summary>
    internal static DateTimeOffset ResolveTimestamp(JsonElement root, JsonElement parameters)
    {
        if (LenientJson.GetObject(parameters, "_meta") is { } metaElement
            && ValueAsI64(TryGet(metaElement, "agentTimestampMs")) is { } agentMillis
            && agentMillis > 0)
        {
            return ToTimestamp(agentMillis);
        }

        if (ValueAsI64(TryGet(root, "timestamp")) is { } seconds && seconds > 0)
        {
            // Rust seconds.saturating_mul(1000)：同号溢出钳 long.MaxValue（负秒已由 >0 门控排除）。
            var millis = seconds > long.MaxValue / 1000 ? long.MaxValue : seconds * 1000;
            return ToTimestamp(millis);
        }

        return StrictRfc3339Timestamp.Epoch;
    }

    /// <summary>毫秒 → 时刻；越界（DateTimeOffset 表达域外）回退 UNIX 纪元（理论角落，卡片偏差⑧）。</summary>
    private static DateTimeOffset ToTimestamp(long millis)
    {
        try
        {
            return DateTimeOffset.UnixEpoch.AddMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return StrictRfc3339Timestamp.Epoch;
        }
    }

    /// <summary>JSON 值 → i64（蓝本 value_as_i64）：整数直取；浮点截断（Rust `as i64` saturating 语义：
    /// 非有限 → 0，越界钳边界）；其余 null。</summary>
    private static long? ValueAsI64(JsonElement? value)
    {
        if (value is not { } element || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (element.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        var raw = element.GetDouble();
        if (!double.IsFinite(raw))
        {
            return 0;
        }

        return raw switch
        {
            > long.MaxValue => long.MaxValue,
            < long.MinValue => long.MinValue,
            _ => (long)raw,
        };
    }

    /// <summary>OpenAI 风格 input 拆分（蓝本 split_input_tokens）：cache_read 与 cache_creation 均为
    /// input 子集，三段还原 input。</summary>
    public static (long Uncached, long CacheRead, long CacheCreation) SplitInputTokens(
        long input, long cachedRead, long cacheCreation)
    {
        var cacheRead = Math.Min(cachedRead, input);
        var uncached = input - cacheRead;
        var creation = Math.Min(cacheCreation, uncached);
        return (uncached - creation, cacheRead, creation);
    }

    /// <summary>URL 轻量解码（蓝本 url_decode_lightweight）：%XX 逐字节解码后 UTF-8 宽松还原
    /// （非法序列 → U+FFFD）；% 三元组不完整/非法按原样保留。</summary>
    public static string UrlDecodeLightweight(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var output = new List<byte>(bytes.Length);
        var index = 0;
        while (index < bytes.Length)
        {
            if (bytes[index] == (byte)'%'
                && index + 2 < bytes.Length
                && FromHex(bytes[index + 1]) is { } high
                && FromHex(bytes[index + 2]) is { } low)
            {
                output.Add((byte)(high * 16 + low));
                index += 3;
                continue;
            }

            output.Add(bytes[index]);
            index++;
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static byte? FromHex(byte value)
    {
        return value switch
        {
            >= (byte)'0' and <= (byte)'9' => (byte)(value - (byte)'0'),
            >= (byte)'a' and <= (byte)'f' => (byte)(value - (byte)'a' + 10),
            >= (byte)'A' and <= (byte)'F' => (byte)(value - (byte)'A' + 10),
            _ => null,
        };
    }

    /// <summary>字段存在且为标量/数组（serde Option&lt;结构体&gt; 反序列化失败的形状；null 不算——
    /// null 反序列化为 None）。</summary>
    private static bool IsScalar(JsonElement parent, string name)
    {
        return TryGet(parent, name) is { } element
               && element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null);
    }

    private static JsonElement? TryGet(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
}

/// <summary>会话元数据（蓝本 SessionMeta）。</summary>
public sealed record GrokSessionMeta(string SessionId, string ProjectPath, string? DefaultModel);

/// <summary>单个模型的用量行（蓝本 GrokModelUsage；u64 溢出钳 long.MaxValue，仓库口径）。</summary>
public readonly record struct GrokModelUsage(
    long InputTokens,
    long OutputTokens,
    long CachedReadTokens,
    long CacheCreationTokens,
    long ReasoningTokens,
    long TotalTokens,
    long CostUsdTicks);
