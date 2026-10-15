using System.Text;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.SqliteSource;

namespace Tokenmetry.Infrastructure.Droid;

/// <summary>
/// Droid 会话快照解析（蓝本 ../ccusage/rust/adapters/droid/src/parser.rs 逐条移植，I-015；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。单会话单文件（&lt;session&gt;.settings.json）：
/// <list type="bullet">
/// <item>tokenUsage（对象）：四桶 inputTokens/outputTokens/cacheCreationTokens/cacheReadTokens（整数
/// lenient_u64 口径）+ thinkingTokens + totalTokens，经 apply_total_token_fallback（缺口补入 output
/// 或 thinking）；五桶全零 → 整文件不产出。</item>
/// <item>model 链：settings.model（去空白非空）→ normalize_droid_model_name（剥 "custom:" 前缀、剥
/// [..] 方括号段（深度感知）、去首尾空白与尾 '-'、ASCII 小写、'.'/空白/'-' 折叠为 '-'）→ 空则回退
/// provider 默认模型；无 settings.model 时读同目录 &lt;stem&gt;.jsonl 侧车（前 500 行、行内 "Model:" 后
/// 截断至 '"'/'\\'/'[' 之前）归一化，再回退 provider 默认（anthropic→claude-unknown、openai→gpt-unknown、
/// google→gemini-unknown、xai→grok-unknown、其余→unknown）。</item>
/// <item>provider：providerLock 归一化（小写、'-'→'_'；claude/anthropic→anthropic、openai、
/// google/google_ai/gemini/vertex/vertex_ai→google、xai/x_ai/grok→xai）；unknown 时按模型名推断
/// （claude/opus/sonnet/haiku→anthropic、gpt-*/-gpt-/chatgpt/o&lt;数字&gt;→openai、gemini→google、
/// grok→xai）。provider 仅服务于蓝本计价候选（桌面走裸模型名，I-024 欠账）与默认模型选择。</item>
/// <item>时间戳：providerLockTimestamp（去空白非空且严格 RFC3339 可解析）优先，否则文件 mtime
/// （蓝本 pricing_timestamp 语义随 mtime 分支为 None——桌面无按记录计价时间戳，见偏差①）。</item>
/// <item>session id = 文件名剥 ".settings.json" 后缀。</item>
/// </list>
/// thinkingTokens 并入统一记录 OutputTokens（蓝本 cost_usage 的 output+reasoning 同式，偏差②）。
/// 行级容错为仓库 I-004 口径（蓝本静默/Err 处显式计数）：JSON 不可解析计 MalformedLines；顶层非对象、
/// 无 tokenUsage/五桶全零、时间戳不可得计 InvalidRecords / LinesSkipped。
/// </summary>
public sealed class DroidSettingsParser
{
    public const string AgentName = "Droid";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Droid"）。</summary>
    public const string ProjectPath = "Droid";

    /// <summary>侧车 JSONL 的模型行采样上限（蓝本 extract_model_from_sidecar_jsonl take(500)）。</summary>
    private const int SidecarLineLimit = 500;

    /// <summary>解析单个会话快照；读取失败不计 FilesRead、计 LinesSkipped（扫描器同口径）。</summary>
    public DroidFileOutcome ParseSettingsFile(string sourceFile)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DroidFileOutcome.Empty(linesSkipped: 1);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // 蓝本 parse 失败返回 Err → loader debug_log 后丢弃（仓库口径计 malformed，显式计数偏差）。
            return new DroidFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 1, InvalidRecords: 0);
        }

        using (document)
        {
            var settings = document.RootElement;
            if (settings.ValueKind != JsonValueKind.Object)
            {
                // 蓝本 value.as_object() 失败 → Ok(None)（显式计数偏差）。
                return new DroidFileOutcome([], FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 1);
            }

            var usage = ParseTokenUsage(TryGetProperty(settings, "tokenUsage"));
            if (usage is null)
            {
                // 无 tokenUsage / 五桶全零：蓝本 Ok(None) 静默（显式计数偏差）。
                return new DroidFileOutcome([], FilesRead: 1, LinesSkipped: 1, MalformedLines: 0, InvalidRecords: 0);
            }

            var provider = NormalizeDroidProvider(LenientJson.GetNonEmptyString(settings, "providerLock"));
            var modelField = LenientJson.GetNonEmptyString(settings, "model");
            var model = modelField is not null
                ? NormalizeDroidModelName(modelField)
                : ExtractModelFromSidecarJsonl(sourceFile) ?? DefaultModelFromProvider(provider);
            if (model.Length == 0)
            {
                model = DefaultModelFromProvider(provider);
            }

            if (provider == "unknown")
            {
                provider = InferDroidProviderFromModel(model);
            }

            if (!TryResolveTimestamp(settings, sourceFile, out var timestamp))
            {
                // 无 providerLockTimestamp 且 mtime 不可得：蓝本 Ok(None)（显式计数偏差）。
                return new DroidFileOutcome([], FilesRead: 1, LinesSkipped: 1, MalformedLines: 0, InvalidRecords: 0);
            }

            var sessionId = SessionIdFromFileName(sourceFile);
            var record = MapRecord(usage.Value, model, sessionId, timestamp, sourceFile, provider);
            return new DroidFileOutcome([record], FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0);
        }
    }

    /// <summary>tokenUsage 解析（蓝本 parse_token_usage）：四桶 + thinking，total 兜底后五桶全零为 null。</summary>
    internal static (RawTokenUsage Usage, long ThinkingTokens)? ParseTokenUsage(JsonElement? value)
    {
        if (value is not { } usage || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rawUsage = new RawTokenUsage(
            InputTokens: LenientJson.GetU64(usage, "inputTokens"),
            OutputTokens: LenientJson.GetU64(usage, "outputTokens"),
            CacheCreationTokens: LenientJson.GetU64(usage, "cacheCreationTokens"),
            CacheReadTokens: LenientJson.GetU64(usage, "cacheReadTokens"));
        var thinkingTokens = LenientJson.GetU64(usage, "thinkingTokens");
        var totalTokens = LenientJson.GetU64(usage, "totalTokens");
        var (resolvedUsage, resolvedThinking) = TokenUsageFallback.Apply(rawUsage, thinkingTokens, totalTokens);

        var sum = resolvedUsage.TotalUsageTokens().SaturatingAddClamped(resolvedThinking);
        return sum > 0 ? (resolvedUsage, resolvedThinking) : null;
    }

    /// <summary>
    /// 模型名归一化（蓝本 normalize_droid_model_name）：剥 "custom:" 前缀 → 深度感知剥 [..] 方括号段 →
    /// 去首尾空白与尾部 '-' → ASCII 小写 → '.'/空白/'-' 折叠为单 '-' → 去首尾 '-'。
    /// </summary>
    public static string NormalizeDroidModelName(string model)
    {
        var raw = model.StartsWith("custom:", StringComparison.Ordinal) ? model["custom:".Length..] : model;
        var withoutBrackets = new StringBuilder(raw.Length);
        var bracketDepth = 0u;
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = bracketDepth == 0 ? 0 : bracketDepth - 1;
                    break;
                default:
                    if (bracketDepth == 0)
                    {
                        withoutBrackets.Append(ch);
                    }

                    break;
            }
        }

        var lower = ToAsciiLowercase(withoutBrackets.ToString().Trim().TrimEnd('-'));
        var normalized = new StringBuilder(lower.Length);
        var previousDash = false;
        foreach (var ch in lower)
        {
            var next = ch == '.' || char.IsWhiteSpace(ch) || ch == '-' ? '-' : ch;
            if (next == '-')
            {
                if (!previousDash)
                {
                    normalized.Append('-');
                    previousDash = true;
                }
            }
            else
            {
                normalized.Append(next);
                previousDash = false;
            }
        }

        return normalized.ToString().Trim('-');
    }

    /// <summary>providerLock 归一化（蓝本 normalize_droid_provider）：未知值保留（小写、'-'→'_'）。</summary>
    internal static string NormalizeDroidProvider(string? value)
    {
        if (value is null)
        {
            return "unknown";
        }

        var normalized = ToAsciiLowercase(value.Trim()).Replace('-', '_');
        return normalized switch
        {
            "" => "unknown",
            "claude" or "anthropic" => "anthropic",
            "openai" => "openai",
            "google" or "google_ai" or "gemini" or "vertex" or "vertex_ai" => "google",
            "xai" or "x_ai" or "grok" => "xai",
            _ => normalized,
        };
    }

    /// <summary>按模型名推断 provider（蓝本 infer_droid_provider_from_model）。</summary>
    internal static string InferDroidProviderFromModel(string model)
    {
        if (model.Contains("claude") || model.Contains("opus")
            || model.Contains("sonnet") || model.Contains("haiku"))
        {
            return "anthropic";
        }

        var openAi = model.StartsWith("gpt-", StringComparison.Ordinal)
                     || model.Contains("-gpt-")
                     || model.Contains("chatgpt")
                     || (model.StartsWith('o') && model.Length > 1 && char.IsAsciiDigit(model[1]));
        if (openAi)
        {
            return "openai";
        }

        if (model.Contains("gemini"))
        {
            return "google";
        }

        return model.Contains("grok") ? "xai" : "unknown";
    }

    /// <summary>provider 默认模型（蓝本 default_model_from_provider）。</summary>
    internal static string DefaultModelFromProvider(string provider)
        => provider switch
        {
            "anthropic" => "claude-unknown",
            "openai" => "gpt-unknown",
            "google" => "gemini-unknown",
            "xai" => "grok-unknown",
            _ => "unknown",
        };

    /// <summary>
    /// 侧车 JSONL 模型回退（蓝本 extract_model_from_sidecar_jsonl）：同目录 &lt;stem&gt;.jsonl 前 500 行内
    /// "Model:" 之后、截断至 '"'/'\\'/'[' 之前的文本归一化；不可得返回 null。
    /// </summary>
    private static string? ExtractModelFromSidecarJsonl(string settingsPath)
    {
        var fileName = Path.GetFileName(settingsPath);
        if (!fileName.EndsWith(DroidLogLocator.SettingsFileSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = fileName[..^DroidLogLocator.SettingsFileSuffix.Length];
        var sidecar = Path.Combine(Path.GetDirectoryName(settingsPath) ?? string.Empty, $"{prefix}.jsonl");
        string content;
        try
        {
            content = File.ReadAllText(sidecar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var rawLine in content.Split('\n').Take(SidecarLineLimit))
        {
            if (ExtractModelFromLine(rawLine.TrimEnd('\r')) is { } model)
            {
                return model;
            }
        }

        return null;
    }

    /// <summary>单行模型提取（蓝本 extract_droid_model_from_line）。</summary>
    private static string? ExtractModelFromLine(string line)
    {
        var separator = line.IndexOf("Model:", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var tail = line[(separator + "Model:".Length)..];
        var cut = tail.IndexOfAny(['"', '\\', '[']);
        var raw = (cut >= 0 ? tail[..cut] : tail).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var normalized = NormalizeDroidModelName(raw);
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>时间戳（蓝本 settings_timestamp）：providerLockTimestamp 可解析优先，否则文件 mtime。</summary>
    private static bool TryResolveTimestamp(JsonElement settings, string path, out DateTimeOffset timestamp)
    {
        var timestampText = LenientJson.GetNonEmptyString(settings, "providerLockTimestamp");
        if (timestampText is not null && StrictRfc3339Timestamp.TryParse(timestampText, out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        var modified = SourceFileCollector.TryGetModifiedTimestamp(path);
        if (modified is not null)
        {
            timestamp = modified.Value;
            return true;
        }

        timestamp = default;
        return false;
    }

    /// <summary>session id（蓝本 file_name().strip_suffix(".settings.json")）：不可得回退 "unknown"。</summary>
    private static string SessionIdFromFileName(string sourceFile)
    {
        var fileName = Path.GetFileName(sourceFile);
        return fileName.EndsWith(DroidLogLocator.SettingsFileSuffix, StringComparison.Ordinal)
            ? fileName[..^DroidLogLocator.SettingsFileSuffix.Length]
            : "unknown";
    }

    /// <summary>统一记录映射：thinkingTokens 并入 OutputTokens（蓝本 cost_usage 同式）；MessageId 置 null
    /// （蓝本 data.message.id 为 "droid:&lt;session&gt;" 合成标识，桌面统一记录无对应语义，Copilot 同款直通）。</summary>
    /// <param name="provider">I-036 三元组的 provider：settings 的 providerLock 归一化值
    /// （缺省时由模型名推导——与模型默认值同一套推导，观测与计价两侧同源）。</param>
    private static UsageRecord MapRecord(
        (RawTokenUsage Usage, long ThinkingTokens) usage,
        string model,
        string sessionId,
        DateTimeOffset timestamp,
        string sourceFile,
        string provider)
    {
        return new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = sessionId,
            Model = model,
            Provider = provider == "unknown" ? string.Empty : provider,
            CostUsd = null,
            InputTokens = usage.Usage.InputTokens,
            OutputTokens = usage.Usage.OutputTokens.SaturatingAddClamped(usage.ThinkingTokens),
            CacheCreationTokens = usage.Usage.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = usage.Usage.CacheReadTokens,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = sourceFile,
        };
    }

    /// <summary>ASCII 小写（蓝本 to_ascii_lowercase：非 ASCII 字符原样保留，与 ToLowerInvariant 不同）。</summary>
    private static string ToAsciiLowercase(string value)
    {
        var buffer = new char[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            buffer[index] = ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch;
        }

        return new string(buffer);
    }

    private static JsonElement? TryGetProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
}

/// <summary>单会话快照解析产出：记录 + 诊断计数（DuplicateRecords 由适配器层 latest-wins 统计）。</summary>
public sealed record DroidFileOutcome(
    List<UsageRecord> Records,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords)
{
    public static DroidFileOutcome Empty(int linesSkipped)
        => new([], FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0);
}
