namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 用量记录准入校验。返回的错误串是面向插件作者的可读短句，字段名与 PluginUsageRecord
/// 的 camelCase wire 名一致；空列表表示合法。规则见
/// docs/plans/2026-11-23-custom-agent-plugin-design.md §5.3。
/// </summary>
public static class PluginRecordValidator
{
    // 设计 §5.3「明确拒绝的输入」要求拒绝超出合理长度上限的字段，避免异常日志把
    // 无限长字符串带进 facts/UI。
    private const int RecordIdMaxLength = 512;
    private const int SessionIdMaxLength = 512;
    private const int ModelMaxLength = 512;
    private const int ProjectDisplayNameMaxLength = 512;
    private const int ProviderMaxLength = 128;
    private const int ProjectPathMaxLength = 4096;

    public static IReadOnlyList<string> Validate(PluginUsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(record.RecordId))
        {
            errors.Add("recordId is required");
        }

        if (record.Timestamp == default)
        {
            errors.Add("timestamp is required");
        }

        if (string.IsNullOrWhiteSpace(record.SessionId))
        {
            errors.Add("sessionId is required");
        }

        if (string.IsNullOrWhiteSpace(record.ProjectPath))
        {
            errors.Add("projectPath is required");
        }

        if (string.IsNullOrWhiteSpace(record.Model))
        {
            errors.Add("model is required");
        }

        CheckMaxLength(errors, record.RecordId, RecordIdMaxLength, "recordId");
        CheckMaxLength(errors, record.SessionId, SessionIdMaxLength, "sessionId");
        CheckMaxLength(errors, record.ProjectPath, ProjectPathMaxLength, "projectPath");
        CheckMaxLength(errors, record.ProjectDisplayName, ProjectDisplayNameMaxLength, "projectDisplayName");
        CheckMaxLength(errors, record.Model, ModelMaxLength, "model");
        CheckMaxLength(errors, record.Provider, ProviderMaxLength, "provider");

        if (record.InputTokens < 0)
        {
            errors.Add("inputTokens must be >= 0");
        }

        if (record.OutputTokens < 0)
        {
            errors.Add("outputTokens must be >= 0");
        }

        if (record.CacheCreationTokens < 0)
        {
            errors.Add("cacheCreationTokens must be >= 0");
        }

        if (record.CacheCreationEphemeral5mTokens < 0)
        {
            errors.Add("cacheCreationEphemeral5mTokens must be >= 0");
        }

        if (record.CacheCreationEphemeral1hTokens < 0)
        {
            errors.Add("cacheCreationEphemeral1hTokens must be >= 0");
        }

        if (record.CacheReadTokens < 0)
        {
            errors.Add("cacheReadTokens must be >= 0");
        }

        var totalTokens = record.InputTokens
            + record.OutputTokens
            + record.CacheCreationTokens
            + record.CacheReadTokens;

        if (totalTokens <= 0)
        {
            errors.Add("totalTokens must be > 0");
        }

        if (record.CostUsd is { } cost && cost < 0)
        {
            errors.Add("costUsd must be >= 0 or null");
        }

        // 缓存写入细分只要有一项非零，总数就必须恰好等于两者之和：PricingEngine 依赖该
        // 不变式，否则未细分部分会被静默漏计。
        if ((record.CacheCreationEphemeral5mTokens > 0 || record.CacheCreationEphemeral1hTokens > 0)
            && record.CacheCreationTokens
                != record.CacheCreationEphemeral5mTokens + record.CacheCreationEphemeral1hTokens)
        {
            errors.Add(
                "cacheCreationTokens must equal cacheCreationEphemeral5mTokens + cacheCreationEphemeral1hTokens");
        }

        return errors;
    }

    private static void CheckMaxLength(List<string> errors, string? value, int maxLength, string fieldName)
    {
        if (value is not null && value.Length > maxLength)
        {
            errors.Add($"{fieldName} must be at most {maxLength} characters");
        }
    }
}
