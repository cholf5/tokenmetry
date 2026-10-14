using TokenPulse.Infrastructure.Antigravity;
using Microsoft.Data.Sqlite;

namespace TokenPulse.Infrastructure.Tests.Antigravity;

/// <summary>
/// I-021 测试支持：protobuf blob 编码器与合成会话库构造，逐字移植蓝本
/// ../ccusage/rust/adapters/antigravity/src/parser.rs 的 #[cfg(test)] test_support
/// （encode_varint / field_varint / field_bytes / model_usage_blob / retry_info_blob /
/// timestamp_blob / metadata_blob / step_metadata_blob / trajectory_metadata_blob /
/// create_database）。全部合成数据，无真实用户数据。
/// </summary>
public static class AntigravityTestSupport
{
    public sealed record UsageFixture
    {
        public ulong? ModelId { get; init; }

        public long InputTokens { get; init; }

        public long TotalOutputTokens { get; init; }

        public long CacheCreationTokens { get; init; }

        public long CacheReadTokens { get; init; }

        public long ReasoningTokens { get; init; }

        public long VisibleOutputTokens { get; init; }

        public ulong? Provider { get; init; }

        public string? MessageId { get; init; }

        public string? ResponseId { get; init; }

        public string? ProviderAssignedMessageId { get; init; }
    }

    public static byte[] EncodeVarint(ulong value)
    {
        var output = new List<byte>();
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
        return [.. output];
    }

    public static byte[] FieldVarint(ulong number, ulong value)
        => [.. EncodeVarint(number << 3), .. EncodeVarint(value)];

    public static byte[] FieldBytes(ulong number, byte[] value)
        => [.. EncodeVarint((number << 3) | 2), .. EncodeVarint((ulong)value.Length), .. value];

    public static byte[] FieldBytes(ulong number, string value)
        => FieldBytes(number, System.Text.Encoding.UTF8.GetBytes(value));

    public static byte[] ModelUsageBlob(UsageFixture usage)
    {
        var output = new List<byte>();
        if (usage.ModelId is { } modelId)
        {
            output.AddRange(FieldVarint(1, modelId));
        }

        foreach (var (field, value) in new[]
                 {
                     (2ul, usage.InputTokens),
                     (3ul, usage.TotalOutputTokens),
                     (4ul, usage.CacheCreationTokens),
                     (5ul, usage.CacheReadTokens),
                     (9ul, usage.ReasoningTokens),
                     (10ul, usage.VisibleOutputTokens),
                 })
        {
            if (value > 0)
            {
                output.AddRange(FieldVarint(field, (ulong)value));
            }
        }

        if (usage.Provider is { } provider)
        {
            output.AddRange(FieldVarint(6, provider));
        }

        if (usage.MessageId is { } messageId)
        {
            output.AddRange(FieldBytes(7, messageId));
        }

        if (usage.ResponseId is { } responseId)
        {
            output.AddRange(FieldBytes(11, responseId));
        }

        if (usage.ProviderAssignedMessageId is { } providerAssignedMessageId)
        {
            output.AddRange(FieldBytes(12, providerAssignedMessageId));
        }

        return [.. output];
    }

    public static byte[] RetryInfoBlob(UsageFixture usage)
        => FieldBytes(2, ModelUsageBlob(usage));

    public static byte[] TimestampBlob(ulong seconds, ulong nanos)
        => [.. FieldVarint(1, seconds), .. FieldVarint(2, nanos)];

    /// <summary>gen_metadata 行（蓝本 metadata_blob）：chat_model(field 1) 内嵌 usage(4) /
    /// generation_info(9→4) / retries(17) / model 文本(19)。</summary>
    public static byte[] MetadataBlob(string? model, UsageFixture? usage, (ulong Seconds, ulong Nanos)? timestamp, UsageFixture[] retries)
    {
        var chatModel = new List<byte>();
        if (usage is { } usageValue)
        {
            if (usageValue.ModelId is { } modelId)
            {
                chatModel.AddRange(FieldVarint(3, modelId));
            }

            chatModel.AddRange(FieldBytes(4, ModelUsageBlob(usageValue)));
        }

        if (timestamp is { } timestampValue)
        {
            var timestampMessage = TimestampBlob(timestampValue.Seconds, timestampValue.Nanos);
            var generationInfo = FieldBytes(4, timestampMessage);
            chatModel.AddRange(FieldBytes(9, generationInfo));
        }

        foreach (var retry in retries)
        {
            chatModel.AddRange(FieldBytes(17, RetryInfoBlob(retry)));
        }

        if (model is not null)
        {
            chatModel.AddRange(FieldBytes(19, model));
        }

        return FieldBytes(1, [.. chatModel]);
    }

    /// <summary>steps.metadata 行（蓝本 step_metadata_blob）：timestamp(8) / usage(9) /
    /// model_info(24: model_id 1、provider 7、model 12) / retries(28)。</summary>
    public static byte[] StepMetadataBlob(string? model, UsageFixture? usage, (ulong Seconds, ulong Nanos)? timestamp, UsageFixture[] retries, ulong? provider)
    {
        var metadata = new List<byte>();
        if (timestamp is { } timestampValue)
        {
            metadata.AddRange(FieldBytes(8, TimestampBlob(timestampValue.Seconds, timestampValue.Nanos)));
        }

        if (usage is { } usageValue)
        {
            metadata.AddRange(FieldBytes(9, ModelUsageBlob(usageValue)));
        }

        if (model is not null || provider is not null)
        {
            var modelInfo = new List<byte>();
            if (usage is { } usageForModelInfo && usageForModelInfo.ModelId is { } modelId)
            {
                modelInfo.AddRange(FieldVarint(1, modelId));
            }

            if (provider is { } providerValue)
            {
                modelInfo.AddRange(FieldVarint(7, providerValue));
            }

            if (model is not null)
            {
                modelInfo.AddRange(FieldBytes(12, model));
            }

            metadata.AddRange(FieldBytes(24, [.. modelInfo]));
        }

        foreach (var retry in retries)
        {
            metadata.AddRange(FieldBytes(28, RetryInfoBlob(retry)));
        }

        return [.. metadata];
    }

    /// <summary>trajectory_metadata_blob 行（蓝本 trajectory_metadata_blob）：timestamp(2)。</summary>
    public static byte[] TrajectoryMetadataBlob(ulong seconds, ulong nanos)
        => FieldBytes(2, TimestampBlob(seconds, nanos));

    /// <summary>合成会话库（蓝本 create_database）：gen_metadata(idx, data) / steps(idx, metadata) /
    /// trajectory_metadata_blob(data)。</summary>
    public static void CreateDatabase(
        string path,
        (long Idx, byte[] Data)[] generationRows,
        (long Idx, byte[] Metadata)[] stepRows,
        byte[][] trajectoryRows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL);");
        Execute(connection, "CREATE TABLE steps (idx INTEGER PRIMARY KEY, metadata BLOB);");
        Execute(connection, "CREATE TABLE trajectory_metadata_blob (data BLOB);");
        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES ($idx, $data)";
            var idxParameter = statement.CreateParameter();
            idxParameter.ParameterName = "$idx";
            statement.Parameters.Add(idxParameter);
            var dataParameter = statement.CreateParameter();
            dataParameter.ParameterName = "$data";
            statement.Parameters.Add(dataParameter);
            foreach (var (idx, data) in generationRows)
            {
                idxParameter.Value = idx;
                dataParameter.Value = data;
                statement.ExecuteNonQuery();
            }
        }

        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = "INSERT INTO steps (idx, metadata) VALUES ($idx, $metadata)";
            var idxParameter = statement.CreateParameter();
            idxParameter.ParameterName = "$idx";
            statement.Parameters.Add(idxParameter);
            var metadataParameter = statement.CreateParameter();
            metadataParameter.ParameterName = "$metadata";
            statement.Parameters.Add(metadataParameter);
            foreach (var (idx, metadata) in stepRows)
            {
                idxParameter.Value = idx;
                metadataParameter.Value = metadata;
                statement.ExecuteNonQuery();
            }
        }

        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = "INSERT INTO trajectory_metadata_blob (data) VALUES ($data)";
            var dataParameter = statement.CreateParameter();
            dataParameter.ParameterName = "$data";
            statement.Parameters.Add(dataParameter);
            foreach (var data in trajectoryRows)
            {
                dataParameter.Value = data;
                statement.ExecuteNonQuery();
            }
        }
    }

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
