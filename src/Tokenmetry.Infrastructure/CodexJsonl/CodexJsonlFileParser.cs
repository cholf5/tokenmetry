using System.Text.Json;
using System.Text.Json.Serialization;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.CodexJsonl;

/// <summary>
/// Codex 会话 JSONL 单文件解析器：从 session_meta / turn_context / event_msg(token_count) 行抽取
/// usage 事件，规则逐条移植自 ccusage-tauri codex.rs（cheap 预过滤标记、累计总额差分出逐 turn 增量、
/// 模型回退链 payload → info → turn_context → gpt-5、cached 上限截断等）。
/// 与 Claude 行解析器的分叉点：Codex 的 token_count 是逐 turn 累计快照（必须跨行记状态），
/// 且蓝本 serde 单次 from_str 同时完成 JSON 语法与 DTO 类型校验 —— 类型不符的行与坏 JSON 同计
/// malformed（Claude 口径为 DTO 失败计 invalid）。
/// 产出统一 <see cref="UsageRecord"/>（agent="Codex"）；Codex 日志无内建成本，CostUsd 恒为 null，
/// 由定价引擎（I-003）按快照价计算。
/// </summary>
public partial class CodexJsonlFileParser
{
    /// <summary>与 codex.rs AGENT_NAME 一致。</summary>
    public const string AgentName = "Codex";

    /// <summary>与 codex.rs FALLBACK_MODEL 一致：payload/info/turn_context 均无模型名时回退。</summary>
    public const string FallbackModel = "gpt-5";

    // 与 codex.rs 一致：只含这些片段的行才进入 JSON 解析（廉价预过滤，其余计 lines_skipped）。
    public const string TokenCountMarker = "token_count";
    public const string TurnContextMarker = "turn_context";
    public const string SessionMetaMarker = "session_meta";

    /// <summary>Codex 在会话内切换服务档位的事件类型（`payload.type`）。</summary>
    public const string ThreadSettingsType = "thread_settings_applied";

    /// <summary>
    /// 解析一个会话文件。<paramref name="sourceDir"/> 为该文件所属数据源目录（sessions/、
    /// archived_sessions/ 或 home 回退目录），会话 id 由相对该目录的路径推导。
    /// </summary>
    public async Task<JsonlFileParseOutcome> ParseAsync(TextReader reader, string sourceDir, string path, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;

        var sessionId = DeriveSessionId(sourceDir, path);
        var state = new SessionState();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.Contains(TokenCountMarker, StringComparison.Ordinal)
                && !line.Contains(TurnContextMarker, StringComparison.Ordinal)
                && !line.Contains(SessionMetaMarker, StringComparison.Ordinal)
                && !line.Contains(ThreadSettingsType, StringComparison.Ordinal))
            {
                linesSkipped++;
                continue;
            }

            SessionLineDto? entry;
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
                // 与 codex.rs 一致：serde_json::from_str 单次完成语法与 DTO 校验，类型不符同计 malformed。
                try
                {
                    entry = document.RootElement.Deserialize(CodexJsonContext.Default.SessionLineDto);
                }
                catch (JsonException)
                {
                    malformedLines++;
                    continue;
                }
            }

            if (entry is null)
            {
                // "null" 根节点：System.Text.Json 返回 null，serde 视为非法类型 → 同计 malformed。
                malformedLines++;
                continue;
            }

            switch (entry.Type)
            {
                case "session_meta":
                    state.ProjectPath ??= NonEmpty(entry.Payload?.Cwd);
                    break;

                case "turn_context":
                    state.ProjectPath ??= NonEmpty(entry.Payload?.Cwd);
                    if (NonEmpty(entry.Payload?.Model) is { } payloadModel)
                    {
                        state.CurrentModel = payloadModel;
                    }
                    else if (NonEmpty(entry.Payload?.ModelName) is { } payloadModelName)
                    {
                        state.CurrentModel = payloadModelName;
                    }

                    break;

                case "event_msg":
                    HandleTokenCount(state, entry, sessionId, path, records, ref invalidRecords);
                    break;
            }
        }

        return new JsonlFileParseOutcome(records, FilesRead: 1, linesSkipped, malformedLines, invalidRecords, Interrupted: false);
    }

    private static void HandleTokenCount(
        SessionState state,
        SessionLineDto entry,
        string sessionId,
        string path,
        List<UsageRecord> records,
        ref int invalidRecords)
    {
        var payload = entry.Payload;
        if (payload is null)
        {
            return;
        }

        if (payload.Type == ThreadSettingsType)
        {
            // 蓝本 parser.rs：事件里完全没有 service_tier 时“什么也没说”，旧档位保持；
            // 有值但取值不认识时说明档位变了，按未知档位清空（不继承旧值）。
            if (!string.IsNullOrEmpty(payload.ThreadSettings?.ServiceTier))
            {
                state.Speed = ParseSpeedTier(payload.ThreadSettings.ServiceTier);
            }

            return;
        }

        if (payload.Type != "token_count")
        {
            return;
        }

        var info = payload.Info;
        if (info is null)
        {
            return;
        }

        var timestampText = entry.Timestamp?.Trim();
        if (string.IsNullOrEmpty(timestampText))
        {
            // 与 codex.rs 一致：缺/空时间戳静默跳过，不计数。
            return;
        }

        // 蓝本保留原始时间戳字符串、由下游解析；统一记录模型要求 DateTimeOffset（与 Claude 适配器口径
        // 一致），无法解析的时间戳无法参与按日聚合，按无效记录处理。
        if (!JsonlTimestamp.TryParse(timestampText, out var timestamp))
        {
            invalidRecords++;
            return;
        }

        var totalUsage = info.TotalTokenUsage?.Resolved();
        // 与发布版 ccusage 的 cumulative_advanced 守卫一致：仅当累计总额推进（或缺失）时才采信
        // last_token_usage。Codex CLI 每个 turn 会连发两条 token_count（第二条为重复快照），
        // 无守卫时逐 turn 双重记账（真实数据实测 ~2×）；累计未推进时退回差分（为零即被全零检查跳过）。
        var cumulativeAdvanced = totalUsage is null
            || state.PreviousTotals is not { } previous
            || !SameUsage(totalUsage.Value, previous);
        Usage? usage;
        if (cumulativeAdvanced)
        {
            usage = info.LastTokenUsage is { } last
                ? last.Resolved()
                : totalUsage is { } total ? SubtractUsage(total, state.PreviousTotals) : null;
        }
        else
        {
            usage = totalUsage is { } total ? SubtractUsage(total, state.PreviousTotals) : null;
        }

        if (totalUsage is not null)
        {
            state.PreviousTotals = totalUsage;
        }

        if (usage is null)
        {
            return;
        }

        var current = usage.Value;
        // 与 codex.rs 一致：四个分量全零的事件静默跳过（token_count 可能因 info=null 等原因为空转）。
        if (current.Input == 0 && current.Cached == 0 && current.Output == 0 && current.Reasoning == 0)
        {
            return;
        }

        var parsedModel = NonEmpty(payload.Model)
            ?? NonEmpty(payload.ModelName)
            ?? NonEmpty(info.Model)
            ?? NonEmpty(info.ModelName);
        string model;
        if (parsedModel is { } parsed)
        {
            state.CurrentModel = parsed;
            model = parsed;
        }
        else
        {
            model = state.CurrentModel ?? FallbackModel;
        }

        // 与 codex.rs 一致：cached 不可能超过 input（数据异常时截断），非缓存输入按差值计。
        var input = current.Input;
        var cached = Math.Min(current.Cached, input);
        if (input > long.MaxValue || current.Output > long.MaxValue || cached > long.MaxValue)
        {
            // u64 超 long 范围的异常值无法装入统一记录，按无效记录处理（蓝本为 u64 直通）。
            invalidRecords++;
            return;
        }

        if (current.Total == 0)
        {
            // 与 codex.rs 一致：total==0 计入 invalid 但记录仍产出
            //（仅 cached>0 的退化事件可达：total==0 蕴含 input/output/reasoning 全零）。
            invalidRecords++;
        }

        records.Add(new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = string.IsNullOrWhiteSpace(state.ProjectPath) ? "Unknown Project" : state.ProjectPath,
            SessionId = sessionId,
            Model = model,
            // Codex 日志无内建成本，交由定价引擎（I-003）按快照价计算（蓝本内联计价由引擎承担）。
            CostUsd = null,
            // 统一模型口径：InputTokens 为非缓存输入（input - cached），缓存部分计入 CacheReadTokens，
            // 二者之和即 Codex 的全量 input（已含缓存），TotalTokens 与蓝本 total_tokens 一致。
            InputTokens = (long)(input - cached),
            // Codex 的 output_tokens 已含 reasoning 输出（蓝本仅将其作信息字段，不计价）。
            OutputTokens = (long)current.Output,
            CacheReadTokens = (long)cached,
            Speed = state.Speed ?? SpeedTier.Standard,
            SourceFile = path,
        });
    }

    /// <summary>与 codex.rs codex_session_id 一致：相对源目录的路径去掉 .jsonl 扩展名，以 "/" 连接。</summary>
    private static string DeriveSessionId(string sourceDir, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(sourceDir, path);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
        {
            relative = path;
        }

        if (relative.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            relative = relative[..^".jsonl".Length];
        }

        relative = relative.Replace('\\', '/');
        return relative.Length == 0 ? "unknown" : relative;
    }

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>与 codex parser.rs `codex_service_tier` 一致：default/standard → Standard，
    /// fast/priority → Fast，flex → Flex，其它取值 → null（未知档位）。</summary>
    private static SpeedTier? ParseSpeedTier(string value) => value switch
    {
        "default" or "standard" => SpeedTier.Standard,
        "fast" or "priority" => SpeedTier.Fast,
        "flex" => SpeedTier.Flex,
        _ => null,
    };

    private static Usage SubtractUsage(Usage current, Usage? previous)
    {
        var previousUsage = previous ?? default;
        return new Usage(
            SaturatingSub(current.Input, previousUsage.Input),
            SaturatingSub(current.Cached, previousUsage.Cached),
            SaturatingSub(current.Output, previousUsage.Output),
            SaturatingSub(current.Reasoning, previousUsage.Reasoning),
            SaturatingSub(current.Total, previousUsage.Total));
    }

    private static ulong SaturatingSub(ulong value, ulong subtrahend) => value >= subtrahend ? value - subtrahend : 0;

    private static bool SameUsage(Usage left, Usage right) =>
        left.Input == right.Input && left.Cached == right.Cached && left.Output == right.Output
        && left.Reasoning == right.Reasoning && left.Total == right.Total;

    /// <summary>与 codex.rs 的 Usage 结构一致（逐 turn 的分量与累计总额）。</summary>
    internal readonly record struct Usage(ulong Input, ulong Cached, ulong Output, ulong Reasoning, ulong Total);

    private sealed class SessionState
    {
        public Usage? PreviousTotals { get; set; }

        public string? CurrentModel { get; set; }

        public string? ProjectPath { get; set; }

        /// <summary>当前服务档位；null = 日志尚未给出（按 Standard 计价）。</summary>
        public SpeedTier? Speed { get; set; }
    }

    // ---- 与 Codex 日志真实结构对应的内部 DTO ----
    // serde 规则的等价物：每个属性标注精确 JSON 名（大小写敏感绑定），Option 字段可缺失/为 null；
    // token 分量用 ulong? 对应 serde u64（负数、浮点、字符串在反序列化时抛 JsonException → malformed）。

    // JSON 源生成上下文（Native AOT 适配，P-004）：反射式 Deserialize<T> 在 AOT 下不可用（IL3050）。
    // Default 口径 = JsonSerializerOptions.Default，与原反射口径一致（精确 JSON 名、大小写敏感绑定；
    // 类型不符抛 JsonException 与 serde from_str 同判 malformed）。
    [JsonSerializable(typeof(SessionLineDto))]
    private sealed partial class CodexJsonContext : JsonSerializerContext
    {
    }

    internal sealed class SessionLineDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("payload")]
        public PayloadDto? Payload { get; set; }
    }

    internal sealed class PayloadDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("info")]
        public InfoDto? Info { get; set; }

        [JsonPropertyName("cwd")]
        public string? Cwd { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("model_name")]
        public string? ModelName { get; set; }

        [JsonPropertyName("thread_settings")]
        public ThreadSettingsDto? ThreadSettings { get; set; }
    }

    internal sealed class ThreadSettingsDto
    {
        [JsonPropertyName("service_tier")]
        public string? ServiceTier { get; set; }
    }

    internal sealed class InfoDto
    {
        [JsonPropertyName("last_token_usage")]
        public RawUsageDto? LastTokenUsage { get; set; }

        [JsonPropertyName("total_token_usage")]
        public RawUsageDto? TotalTokenUsage { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("model_name")]
        public string? ModelName { get; set; }
    }

    internal sealed class RawUsageDto
    {
        [JsonPropertyName("input_tokens")]
        public ulong? InputTokens { get; set; }

        [JsonPropertyName("cached_input_tokens")]
        public ulong? CachedInputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public ulong? OutputTokens { get; set; }

        [JsonPropertyName("reasoning_output_tokens")]
        public ulong? ReasoningOutputTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public ulong? TotalTokens { get; set; }

        /// <summary>与 codex.rs RawUsage::resolved 一致：total 缺失或非正时回退为 input+output+reasoning。</summary>
        public Usage Resolved()
        {
            var input = InputTokens ?? 0;
            var output = OutputTokens ?? 0;
            var reasoning = ReasoningOutputTokens ?? 0;
            var sum = input + output + reasoning;
            var total = TotalTokens is { } value && (value > 0 || sum == 0) ? value : sum;
            return new Usage(input, CachedInputTokens ?? 0, output, reasoning, total);
        }
    }
}
