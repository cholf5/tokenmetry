using System.Text.Json;
using System.Text.Json.Serialization;
using TokenPulse.Core.Models;
using TokenPulse.Infrastructure.Jsonl;

namespace TokenPulse.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude Code JSONL 单行解析器，把一行 assistant usage 事件解析为统一 <see cref="UsageRecord"/>。
/// 规则逐条移植自 ccusage-tauri claude.rs（usage 标记、agent progress 包装、字段判空、
/// semver 前缀校验、模型归一化、cache_creation 5m/1h 细分等），不要凭记忆猜测日志格式。
/// </summary>
public class ClaudeCodeJsonlLineParser
{
    /// <summary>与 claude.rs USAGE_MARKER 一致：只有包含该片段的行才进入 JSON 解析。</summary>
    public const string UsageMarker = "\"usage\":{";

    /// <summary>与 claude.rs AGENT_NAME 一致。</summary>
    public const string AgentName = "Claude Code";

    private const string WrapperUsagePointer = "/data/message/message/usage";

    // 与 claude.rs has_unsupported_nulls 一致：这几个字段显式为 null 的记录整条拒绝。
    private static readonly string[] UnsupportedNullPointers =
    [
        "/message/usage/cache_read_input_tokens",
        "/message/usage/cache_creation_input_tokens",
        "/data/message/message/usage/cache_read_input_tokens",
        "/data/message/message/usage/cache_creation_input_tokens",
    ];

    /// <summary>
    /// 解析一行已通过 usage 标记检查的 JSON。返回 null 表示该记录无效（调用方计入 invalid 计数），
    /// JSON 结构错误由调用方在 <see cref="JsonDocument.Parse"/> 处理（malformed 计数）。
    /// </summary>
    public UsageRecord? Parse(JsonElement root, string fileSessionId, string projectPath, string sourceFile)
    {
        // 非 JSON 对象行与 claude.rs 一样按无效记录处理（指针查找与 DTO 反序列化都会失败）。
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (HasUnsupportedNulls(root))
        {
            return null;
        }

        if (TryGetPointer(root, WrapperUsagePointer, out _))
        {
            var wrapper = TryDeserialize<AgentProgressEntry>(root);
            if (wrapper is null)
            {
                return null;
            }

            return Normalize(wrapper.ToRawEntry(), fileSessionId, projectPath, sourceFile);
        }

        var entry = TryDeserialize<RawUsageEntry>(root);
        return entry is null ? null : Normalize(entry, fileSessionId, projectPath, sourceFile);
    }

    /// <summary>
    /// RFC 6901 JSON Pointer 查找，语义与 serde_json Value::pointer 一致：
    /// 路径存在即返回 true（值为 null 也算存在）；中间节点不是对象/数组则返回 false。
    /// </summary>
    private static bool TryGetPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = default;
        if (!pointer.StartsWith('/'))
        {
            return false;
        }

        var current = root;
        foreach (var rawToken in pointer[1..].Split('/'))
        {
            var token = rawToken.Replace("~1", "/").Replace("~0", "~");
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    if (!current.TryGetProperty(token, out var child))
                    {
                        return false;
                    }

                    current = child;
                    break;
                }

                case JsonValueKind.Array:
                {
                    if (!int.TryParse(token, out var index) || index < 0 || index >= current.GetArrayLength())
                    {
                        return false;
                    }

                    current = current[index];
                    break;
                }

                default:
                    return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>
    /// 从会话文件路径推导 (sessionId, projectPath)，与 claude.rs extract_session_parts 一致：
    /// projects/&lt;project&gt;/&lt;session&gt;.jsonl 为扁平结构；projects/&lt;project&gt;/&lt;session&gt;/subagents/&lt;file&gt;.jsonl
    /// 归属父会话；其余嵌套结构取倒数第二段为会话 id。
    /// </summary>
    public static (string SessionId, string ProjectPath) ExtractSessionParts(string path)
    {
        // 显式 char[]：`Split('/', '\\', options)` 会重载解析为 Split(char, int, StringSplitOptions)，
        // 第二个分隔符被隐式转成 int 当作最大段数（I-005 修正：Windows 反斜杠路径此前从未被拆分）。
        var parts = path.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        var projectsIndex = Array.IndexOf(parts, "projects");
        var relative = projectsIndex >= 0 ? parts[(projectsIndex + 1)..] : parts;

        var fileSessionId = relative.Length > 0
            && relative[^1].EndsWith(".jsonl", StringComparison.Ordinal)
            && relative[^1].Length > ".jsonl".Length
            ? relative[^1][..^".jsonl".Length]
            : null;

        if (relative.Length == 2 && fileSessionId is not null)
        {
            return (fileSessionId, relative[0]);
        }

        if (relative.Length >= 4 && relative[^2] == "subagents")
        {
            var sessionDirectory = string.Join(Path.DirectorySeparatorChar, relative[..^3]);
            return (relative[^3], NonEmptyProjectPath(sessionDirectory));
        }

        var sessionId = relative.Length switch
        {
            0 => "unknown",
            1 => relative[0],
            _ => relative[^2],
        };
        var projectPath = relative.Length > 2
            ? string.Join(Path.DirectorySeparatorChar, relative[..^2])
            : "Unknown Project";
        return (sessionId, NonEmptyProjectPath(projectPath));
    }

    private static UsageRecord? Normalize(RawUsageEntry entry, string fileSessionId, string projectPath, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(entry.Timestamp) || entry.Timestamp is not { } timestampText)
        {
            return null;
        }

        if (entry.Version is { } version && !LooksLikeSemverPrefix(version))
        {
            return null;
        }

        // 与 claude.rs 的 is_some_and(|s| s.trim().is_empty()) 一致：
        // 只有"显式空字符串"才整条拒绝，JSON null 是合法值（sessionId 回退文件推导值）。
        if (IsEmptyString(entry.SessionId) || IsEmptyString(entry.RequestId))
        {
            return null;
        }

        var message = entry.Message;
        if (message is null || message.Usage is null)
        {
            return null;
        }

        if (IsEmptyString(message.Id) || IsEmptyString(message.Model))
        {
            return null;
        }

        var usage = message.Usage;
        // claude.rs 对应字段为 u64：缺失、null、负数、非整数都整条拒绝。
        if (usage.InputTokens is not { } inputTokens || inputTokens < 0)
        {
            return null;
        }

        if (usage.OutputTokens is not { } outputTokens || outputTokens < 0)
        {
            return null;
        }

        var speed = ParseSpeed(usage.Speed);
        if (speed == RawSpeed.Invalid)
        {
            return null;
        }

        // claude.rs 保留原始 timestamp 字符串；这里统一解析为 DateTimeOffset 供 facts/定价直接消费，
        // 无法解析的时间戳无法参与按日聚合，按无效记录处理（Rust 端下游同样会丢弃这类记录）。
        // 解析规则（InvariantCulture、未知时区按 UTC）与 Codex 适配器共用 JsonlTimestamp。
        if (!JsonlTimestamp.TryParse(timestampText, out var timestamp))
        {
            return null;
        }

        long cacheCreationTokens;
        long ephemeral5m;
        long ephemeral1h;
        if (usage.CacheCreation is { } cacheCreation)
        {
            ephemeral5m = cacheCreation.Ephemeral5mInputTokens;
            ephemeral1h = cacheCreation.Ephemeral1hInputTokens;
            if (ephemeral5m < 0 || ephemeral1h < 0)
            {
                return null;
            }

            cacheCreationTokens = ephemeral5m + ephemeral1h;
        }
        else
        {
            cacheCreationTokens = usage.CacheCreationInputTokens ?? 0;
            ephemeral5m = 0;
            ephemeral1h = 0;
        }

        if (cacheCreationTokens < 0 || (usage.CacheReadInputTokens ?? 0) < 0)
        {
            return null;
        }

        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = IsBlank(projectPath) ? "Unknown Project" : projectPath,
            // 空 sessionId 已在上方拒绝，null（缺失）回退到文件推导的会话 id。
            SessionId = entry.SessionId ?? fileSessionId,
            Model = NormalizeModel(message.Model, speed == RawSpeed.Fast),
            CostUsd = entry.CostUsd,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheCreationTokens = cacheCreationTokens,
            CacheCreationEphemeral5mTokens = ephemeral5m,
            CacheCreationEphemeral1hTokens = ephemeral1h,
            CacheReadTokens = usage.CacheReadInputTokens ?? 0,
            IsSidechain = entry.IsSidechain ?? false,
            MessageId = message.Id,
            RequestId = entry.RequestId,
            SourceFile = sourceFile,
        };
    }

    private static bool HasUnsupportedNulls(JsonElement root)
    {
        foreach (var pointer in UnsupportedNullPointers)
        {
            if (TryGetPointer(root, pointer, out var value) && value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
        }

        return false;
    }

    private static T? TryDeserialize<T>(JsonElement root)
        where T : class
    {
        try
        {
            // 不开 PropertyNameCaseInsensitive：与 serde 一致按精确 JSON 名绑定，
            // 大小写变体（"SessionID" 等）视为未知字段忽略，而不是被误绑定。
            return root.Deserialize<T>();
        }
        catch (JsonException)
        {
            // 与 claude.rs 一致：DTO 反序列化失败（缺字段/类型不符/null 不符）按无效记录处理。
            return null;
        }
    }

    /// <summary>与 claude.rs looks_like_semver_prefix 一致：major.minor.patch 前缀，patch 首字符须为数字。</summary>
    private static bool LooksLikeSemverPrefix(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        var major = parts[0];
        var minor = parts[1];
        var patch = parts[2];
        if (major.Length == 0 || minor.Length == 0 || patch.Length == 0)
        {
            return false;
        }

        if (!major.All(char.IsAsciiDigit) || !minor.All(char.IsAsciiDigit))
        {
            return false;
        }

        return char.IsAsciiDigit(patch[0]);
    }

    /// <summary>与 claude.rs normalize_model 一致：去首尾空白、&lt;synthetic&gt; 归一为 null、fast speed 追加 -fast 后缀。</summary>
    private static string? NormalizeModel(string? model, bool isFast)
    {
        if (model is null)
        {
            return null;
        }

        var trimmed = model.Trim();
        if (trimmed == "<synthetic>")
        {
            return null;
        }

        return isFast ? trimmed + "-fast" : trimmed;
    }

    /// <summary>与 claude.rs Speed 枚举一致：仅识别小写 "fast" / "standard"，其它取值整条拒绝。</summary>
    private static RawSpeed ParseSpeed(string? speed) => speed switch
    {
        null => RawSpeed.None,
        "fast" => RawSpeed.Fast,
        "standard" => RawSpeed.Standard,
        _ => RawSpeed.Invalid,
    };

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>与 claude.rs 的 is_some_and(|s| s.trim().is_empty()) 对齐：null 合法，空/空白字符串非法。</summary>
    private static bool IsEmptyString(string? value) => value is not null && string.IsNullOrWhiteSpace(value);

    private static string NonEmptyProjectPath(string projectPath)
        => IsBlank(projectPath) ? "Unknown Project" : projectPath;

    private enum RawSpeed
    {
        None,
        Standard,
        Fast,
        Invalid,
    }

    // ---- 与 Claude 日志真实结构对应的内部 DTO ----
    // serde 规则的等价物：每个属性标注精确 JSON 名（大小写敏感绑定）、缺省值、null 拒绝、required 字段。

    internal sealed class RawUsageEntry
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("message")]
        public RawUsageMessage? Message { get; set; }

        [JsonPropertyName("costUSD")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        // claude.rs 为 Option<bool>：字符串等错误类型整条拒绝；包装行固定为 None。
        [JsonPropertyName("isApiErrorMessage")]
        public bool? IsApiErrorMessage { get; set; }

        [JsonPropertyName("isSidechain")]
        public bool? IsSidechain { get; set; }
    }

    internal sealed class RawUsageMessage
    {
        [JsonPropertyName("usage")]
        public RawTokenUsage? Usage { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    internal sealed class RawTokenUsage
    {
        [JsonPropertyName("input_tokens")]
        public long? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public long? OutputTokens { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public long? CacheCreationInputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
        public long? CacheReadInputTokens { get; set; }

        [JsonPropertyName("speed")]
        public string? Speed { get; set; }

        [JsonPropertyName("cache_creation")]
        public RawCacheCreation? CacheCreation { get; set; }
    }

    internal sealed class RawCacheCreation
    {
        [JsonPropertyName("ephemeral_5m_input_tokens")]
        public long Ephemeral5mInputTokens { get; set; }

        [JsonPropertyName("ephemeral_1h_input_tokens")]
        public long Ephemeral1hInputTokens { get; set; }
    }

    internal sealed class AgentProgressEntry
    {
        [JsonPropertyName("data")]
        public AgentProgressData? Data { get; set; }

        public RawUsageEntry ToRawEntry() => new()
        {
            SessionId = null,
            Timestamp = Data?.Message?.Timestamp,
            Version = null,
            Message = Data?.Message?.Message,
            CostUsd = Data?.Message?.CostUsd,
            RequestId = Data?.Message?.RequestId,
            IsApiErrorMessage = null,
            IsSidechain = Data?.Message?.IsSidechain,
        };
    }

    internal sealed class AgentProgressData
    {
        [JsonPropertyName("message")]
        public AgentProgressMessage? Message { get; set; }
    }

    internal sealed class AgentProgressMessage
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        [JsonPropertyName("isSidechain")]
        public bool? IsSidechain { get; set; }

        [JsonPropertyName("costUSD")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("message")]
        public RawUsageMessage? Message { get; set; }
    }
}
