using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Contract;

public class PluginRecordValidatorTests
{
    private static PluginUsageRecord ValidRecord() => new()
    {
        RecordId = "r1",
        Timestamp = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
        SessionId = "s1",
        ProjectPath = "/work/foo",
        Model = "foo-large",
        InputTokens = 10,
        OutputTokens = 5,
    };

    [Fact]
    public void Valid_record_has_no_errors() =>
        Assert.Empty(PluginRecordValidator.Validate(ValidRecord()));

    [Fact]
    public void Missing_session_id_is_rejected() =>
        Assert.Contains(PluginRecordValidator.Validate(ValidRecord() with { SessionId = "" }),
            error => error.Contains("sessionId", StringComparison.Ordinal));

    [Fact]
    public void Zero_total_tokens_is_rejected() =>
        Assert.Contains(
            PluginRecordValidator.Validate(ValidRecord() with { InputTokens = 0, OutputTokens = 0 }),
            error => error.Contains("totalTokens", StringComparison.Ordinal));

    [Fact]
    public void Cache_split_mismatch_is_rejected() =>
        Assert.Contains(
            PluginRecordValidator.Validate(ValidRecord() with
            {
                CacheCreationTokens = 1000,
                CacheCreationEphemeral5mTokens = 100,
                CacheCreationEphemeral1hTokens = 0,
            }),
            error => error.Contains("cacheCreation", StringComparison.Ordinal));

    [Fact]
    public void Negative_cost_is_rejected() =>
        Assert.Contains(PluginRecordValidator.Validate(ValidRecord() with { CostUsd = -1m }),
            error => error.Contains("costUsd", StringComparison.Ordinal));
}
