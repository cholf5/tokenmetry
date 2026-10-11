using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.QwenCode;

/// <summary>
/// Qwen Code 单文件解析（蓝本 ../ccusage/rust/adapters/qwen/src/parser.rs 逐条移植，I-011；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。每个根目录 projects/ 下的聊天文件
/// 逐行解析为统一 <see cref="UsageRecord"/>：
/// <list type="bullet">
/// <item>行预过滤（蓝本 LinePrefilter）：不含 <c>"usageMetadata"</c> 片段的行直接跳过（Qwen 每条
/// 可用行都带该键，serde 结构体其余字段全部忽略）。</item>
/// <item>字段语义（蓝本 QwenLine / QwenUsageMetadata，serde camelCase 精确绑定）：type / sessionId /
/// timestamp / model 为 non_empty_string（非字符串或空白为 None，<b>保留值去首尾空白</b>）；
/// usageMetadata 为严格 Option&lt;结构体&gt;——缺失或 null 视为 None（serde Option 语义），
/// 非对象（标量/数组等）整个行反序列化失败；五个计数键（promptTokenCount / candidatesTokenCount /
/// thoughtsTokenCount / cachedContentTokenCount / totalTokenCount）均为 lenient_u64（仅整数生效，
/// 浮点/字符串/null/负数/缺失 → 0）。</item>
/// <item>记录条件：type 恰为 "assistant"（去空白后比较）且有 usageMetadata；token 口径
/// input←prompt、output←candidates、cache_read←cachedContent；apply_total_token_fallback 兜底
/// （thoughts 作 extra、total 为目标：output 为 0 补 output、否则缺口并入 extra）；四桶与 extra
/// 全零的行不产出；extra（reasoning/total 余量）并入统一记录 OutputTokens（蓝本 billable_usage
/// 的 output+extra 同式，总量与成本守恒）。</item>
/// <item>时间戳：行内 timestamp 经严格 RFC3339（蓝本 parse_ts_timestamp）校验——可解析即用原值；
/// 缺失 / 非字符串 / 不可解析回退<b>文件 mtime（毫秒截断）</b>，mtime 不可得回退当前时间（蓝本
/// SystemTime::now 同口径）。</item>
/// <item>会话 / 项目：sessionId 缺失回退 <c>&lt;project&gt;-&lt;文件名主干&gt;</c>；项目名从路径
/// components 的 [projects, project, chats, file] 滑窗提取，未命中为 "unknown"；模型缺失为
/// "unknown"（蓝本 DEFAULT_QWEN_MODEL，记录模型恒非空）。</item>
/// </list>
/// 成本落点：统一记录 CostUsd 恒 null 交定价引擎（蓝本 calculate_qwen_cost 的 qwen/、alibaba/
/// 前缀候选计价属 I-024 端到端欠账，见卡片偏差①）。行级容错为仓库 I-004 口径（蓝本静默跳过处
/// 显式计数：无标记行 → LinesSkipped、坏 JSON → MalformedLines、结构/语义拒绝 → InvalidRecords）。
/// </summary>
public sealed class QwenLogParser
{
    public const string AgentName = "Qwen Code";

    /// <summary>兜底模型名（蓝本 DEFAULT_QWEN_MODEL）。</summary>
    public const string DefaultModel = "unknown";

    /// <summary>与蓝本 LinePrefilter::all(&amp;[b#""usageMetadata""#]) 一致：只有包含该片段的行才进入 JSON 解析。</summary>
    public const string UsageMetadataMarker = "\"usageMetadata\"";

    /// <summary>
    /// 解析单个聊天文件（共享扫描引擎的逐文件委托）。打不开 / 读取中途失败交由
    /// <see cref="IncrementalJsonlScanner"/> 处理（计 LinesSkipped、不落账、下次重试）。
    /// </summary>
    public async Task<JsonlFileParseOutcome> ParseFileAsync(string sourceFile, TextReader reader, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;

        var fallbackTimestamp = GetFileTimestamp(sourceFile);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.Contains(UsageMetadataMarker, StringComparison.Ordinal))
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
                var record = ParseLine(document.RootElement, sourceFile, fallbackTimestamp);
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
    internal UsageRecord? ParseLine(JsonElement root, string sourceFile, DateTimeOffset fallbackTimestamp)
    {
        // serde 单次 from_slice::<QwenLine>：顶层非对象与 usageMetadata 非对象（非 null）同为
        // 结构性反序列化失败（Claude/Droid 口径计 InvalidRecords）。
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = LenientJson.GetNonEmptyString(root, "type");
        if (type != "assistant")
        {
            return null;
        }

        // Option<QwenUsageMetadata>（严格 Option，非 lenient_object）：null/缺失 → None（该行
        // 无 usage 块、拒绝）；标量/数组 → serde 结构体反序列化失败（整行拒绝）。
        if (!root.TryGetProperty("usageMetadata", out var usageElement))
        {
            return null;
        }

        if (usageElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = LenientJson.GetU64(usageElement, "promptTokenCount");
        var outputTokens = LenientJson.GetU64(usageElement, "candidatesTokenCount");
        var thoughtsTokens = LenientJson.GetU64(usageElement, "thoughtsTokenCount");
        var cacheReadTokens = LenientJson.GetU64(usageElement, "cachedContentTokenCount");
        var totalTokens = LenientJson.GetU64(usageElement, "totalTokenCount");

        var displayUsage = new RawTokenUsage(inputTokens, outputTokens, 0, cacheReadTokens);
        var (usage, extraTotalTokens) = TokenUsageFallback.Apply(displayUsage, thoughtsTokens, totalTokens);
        if (usage.InputTokens == 0 && usage.OutputTokens == 0
            && usage.CacheReadTokens == 0 && extraTotalTokens == 0)
        {
            return null;
        }

        // 行内 timestamp 经严格 RFC3339 校验通过才采用；否则整体回退 mtime（蓝本把
        // format_rfc3339_millis(fallback) 再 parse 一遍，等价于直接使用 fallback 时刻）。
        var timestampText = LenientJson.GetNonEmptyString(root, "timestamp");
        var timestamp = timestampText is not null && StrictRfc3339Timestamp.TryParse(timestampText, out var parsed)
            ? parsed
            : fallbackTimestamp;

        var project = QwenLogLocator.ProjectFromPath(sourceFile) ?? "unknown";
        var sessionId = LenientJson.GetNonEmptyString(root, "sessionId")
                        ?? $"{project}-{Path.GetFileNameWithoutExtension(sourceFile)}";
        var model = LenientJson.GetNonEmptyString(root, "model") ?? DefaultModel;

        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = project,
            SessionId = sessionId,
            Model = model,
            CostUsd = null,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens.SaturatingAddClamped(extraTotalTokens),
            CacheCreationTokens = 0,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = usage.CacheReadTokens,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = sourceFile,
        };
    }

    /// <summary>文件 mtime（蓝本 file_timestamp）：毫秒截断；不可得回退当前时间（SystemTime::now 同口径）。</summary>
    internal static DateTimeOffset GetFileTimestamp(string sourceFile)
    {
        try
        {
            var info = new FileInfo(sourceFile);
            if (info.Exists)
            {
                return TruncateToMillis(info.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落到下方 now 回退（蓝本 metadata 失败分支）。
        }

        return TruncateToMillis(DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset TruncateToMillis(DateTimeOffset value)
        => DateTimeOffset.UnixEpoch.AddMilliseconds(value.ToUnixTimeMilliseconds());
}
