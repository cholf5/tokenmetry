using TokenPulse.Infrastructure.JsonSource;
using TokenPulse.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace TokenPulse.Infrastructure.Antigravity;

/// <summary>
/// Antigravity 会话库行解析（I-021，逐条移植蓝本 ../ccusage/rust/adapters/antigravity/src/parser.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 单库按代序（idx ASC）读 gen_metadata（必需表）与 steps（可选表）的 protobuf blob
/// （<see cref="AntigravityProtoReader"/> 最小手写解码，spike 结论：手写 wire 0/1/2/5 解码即可，
/// 无需 protobuf 库），trajectory_metadata_blob（可选表）提供时间戳回退；usage/retry 事件带身份键
/// （response/provider/message）与时间戳/消息 id 优先级 rank，供跨库身份去重
/// （<see cref="MergeUsageEvent"/> / <see cref="DeduplicateEvents"/>，蓝本 parser.rs + loader.rs 同构）。
/// token 桶换算（蓝本 append_usage_event 逐字保真，u64 saturating 语义）：
/// total_output = max(total_output, visible + reasoning)；output = max(visible, total_output − reasoning)；
/// reasoning = max(reasoning, total_output − output)（统一记录无 extra 字段，reasoning 不落记录——
/// 见卡片偏差清单）。时间戳回退链：metadata timestamp(rank 3) → 身份键回查 →
/// trajectory blob(rank 1) → 文件 mtime(rank 0)。模型命名：model 文本（19/21/12/8 号字段）→
/// model_id 映射 → 上文/代序模型状态 → "gemini-internal-model" 缺省；归一化表逐字移植。
/// </summary>
public static class AntigravityParser
{
    public const string DefaultModel = "gemini-internal-model";

    public const ulong ApiProviderGoogleVertex = 3;
    public const ulong ApiProviderGoogleGemini = 24;
    public const ulong ApiProviderGoogleEvergreen = 30;

    /// <summary>单库解析产物：事件集（按发现序，未去重）与非 token-bearing usage 的跳过计数。</summary>
    public sealed class ParseOutcome
    {
        public required List<UsageEvent> Events { get; init; }

        public int NonTokenBearingUsages { get; init; }
    }

    public sealed class UsageEvent
    {
        public long Timestamp { get; set; }

        public required string SessionId { get; init; }

        public required string SourceFile { get; init; }

        public string Model { get; set; } = DefaultModel;

        public ulong? Provider { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheCreationTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long ReasoningTokens { get; set; }

        public long TotalOutputTokens { get; set; }

        public long TotalTokens { get; set; }

        public string? MessageId { get; set; }

        public List<string> Identities { get; set; } = [];

        public byte TimestampRank { get; set; }

        public byte MessageIdRank { get; set; }
    }

    private sealed class GeneratorMetadata
    {
        public string? Model { get; init; }

        public ulong? ModelId { get; init; }

        public ModelUsage? Usage { get; init; }

        public List<ModelUsage> RetryUsages { get; init; } = [];

        public long? Timestamp { get; init; }
    }

    private sealed class StepMetadata
    {
        public string? Model { get; init; }

        public ulong? ModelId { get; init; }

        public ulong? Provider { get; init; }

        public ModelUsage? Usage { get; init; }

        public List<ModelUsage> RetryUsages { get; init; } = [];

        public long? Timestamp { get; init; }
    }

    private sealed class ModelUsage
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

        public bool IsTokenBearing()
            => InputTokens > 0 || TotalOutputTokens > 0 || CacheCreationTokens > 0
               || CacheReadTokens > 0 || ReasoningTokens > 0 || VisibleOutputTokens > 0;

        public List<string> IdentityKeys()
        {
            var keys = new List<string>(3);
            if (!string.IsNullOrEmpty(ResponseId))
            {
                keys.Add($"response:{ResponseId}");
            }

            if (!string.IsNullOrEmpty(ProviderAssignedMessageId))
            {
                keys.Add($"provider:{ProviderAssignedMessageId}");
            }

            if (!string.IsNullOrEmpty(MessageId))
            {
                keys.Add($"message:{MessageId}");
            }

            return keys;
        }

        public (string? MessageId, byte Rank) PreferredMessageId()
        {
            if (!string.IsNullOrEmpty(ResponseId))
            {
                return (ResponseId, 3);
            }

            if (!string.IsNullOrEmpty(ProviderAssignedMessageId))
            {
                return (ProviderAssignedMessageId, 2);
            }

            return (MessageId, 1);
        }
    }

    /// <summary>事件上下文（蓝本 EventContext；时间戳带 rank）。</summary>
    private readonly record struct EventContext(
        string? Model,
        ulong? Provider,
        (long Timestamp, byte Rank)? Timestamp,
        long? TrajectoryTimestamp,
        long FallbackTimestamp,
        string SessionId,
        string SourceFile);

    /// <summary>
    /// 解析单个会话库（蓝本 parse_sqlite_file）：空快照（page_count == 0）返回 null（未初始化库
    /// 按蓝本 debug 跳过）；打开 / 查询 / blob 解析失败抛异常，由适配器按"该库本轮 0 条 + 问题标记"
    /// 显式降级（蓝本为整次加载失败——桌面管道 fail-loud 程度差异见卡片偏差清单）。
    /// </summary>
    public static ParseOutcome? ParseSqliteFile(string path)
    {
        var fallbackTimestamp = SourceFileCollector.TryGetModifiedTimestamp(path)?.ToUnixTimeMilliseconds() ?? 0;
        using var connection = SqliteReadonly.Open(path);
        // 蓝本 BEGIN DEFERRED TRANSACTION：单连接内一致快照，连接释放即回滚结束。
        // C# 用托管事务（Microsoft.Data.Sqlite BeginTransaction 默认即 DEFERRED）——裸 SQL BEGIN
        // 不被托管连接跟踪，连接池归还时不回滚，第二次打开报"cannot start a transaction within
        // a transaction"（复现实证）；托管事务 Dispose 干净归还连接池，语义与蓝本等价。
        using var snapshot = connection.BeginTransaction();
        if (DatabaseSnapshotIsEmpty(connection))
        {
            return null;
        }

        var sessionId = Path.GetFileNameWithoutExtension(path);
        if (sessionId.Length == 0)
        {
            sessionId = "unknown";
        }

        var trajectoryTimestamp = ReadTrajectoryTimestamp(connection);
        var generationRows = ReadGenerationRows(connection);
        var stepRows = ReadStepRows(connection);

        // 蓝本 generation_model：代序倒序首个可归一化模型（model 文本优先、model_id 映射次之）。
        string? generationModel = null;
        for (var index = generationRows.Count - 1; index >= 0; index--)
        {
            var metadata = generationRows[index].Metadata;
            // 三元链与蓝本 .and_then(normalize).or_else(model_id 链) 形式差异（model 存在但归一为
            // null 时不回退 model_id）不可达：field_text 保证 trim 非空，非空输入归一恒非 null。
            var candidate = metadata.Model is { } generationModelText
                ? NormalizeAntigravityModel(generationModelText)
                : metadata.ModelId is { } generationModelId
                    ? NormalizeAntigravityModel(ModelNameFromId(generationModelId))
                    : null;
            if (candidate is not null)
            {
                generationModel = candidate;
                break;
            }
        }

        var events = new List<UsageEvent>();
        var identityTimestamps = new Dictionary<string, (long Timestamp, byte Rank)>(StringComparer.Ordinal);
        var nonTokenBearing = 0;

        foreach (var (_, metadata) in stepRows)
        {
            // 同上：Model 存在但归一为 null 的形式差异不可达（field_text 保证 trim 非空）。
            var model = metadata.Model is { } stepModelText ? NormalizeAntigravityModel(stepModelText)
                : metadata.ModelId is { } stepModelId ? NormalizeAntigravityModel(ModelNameFromId(stepModelId))
                : generationModel;
            (long, byte)? timestamp = metadata.Timestamp is { } stepTimestamp ? (stepTimestamp, (byte)3) : null;
            if (metadata.Usage is { } usage)
            {
                nonTokenBearing += AppendUsageEvent(events, identityTimestamps, usage, new EventContext(
                    model, metadata.Provider, timestamp, trajectoryTimestamp, fallbackTimestamp, sessionId, path));
            }

            foreach (var retryUsage in metadata.RetryUsages)
            {
                nonTokenBearing += AppendUsageEvent(events, identityTimestamps, retryUsage, new EventContext(
                    model, metadata.Provider, timestamp, trajectoryTimestamp, fallbackTimestamp, sessionId, path));
            }
        }

        // 蓝本 generation 循环：current_model 状态逐行推进（None 不覆盖，Some 更新）。
        string? currentModel = null;
        foreach (var (_, metadata) in generationRows)
        {
            // 同上：Model 存在但归一为 null 的形式差异不可达（field_text 保证 trim 非空）。
            var rowModel = metadata.Model is { } modelText ? NormalizeAntigravityModel(modelText)
                : metadata.ModelId is { } modelId ? NormalizeAntigravityModel(ModelNameFromId(modelId))
                : metadata.Usage?.ModelId is { } usageModelId
                    ? NormalizeAntigravityModel(ModelNameFromId(usageModelId))
                    : null;
            if (rowModel is not null)
            {
                currentModel = rowModel;
            }

            (long, byte)? timestamp = metadata.Timestamp is { } generationTimestamp
                ? (generationTimestamp, (byte)3)
                : null;
            if (metadata.Usage is { } usage)
            {
                nonTokenBearing += AppendUsageEvent(events, identityTimestamps, usage, new EventContext(
                    currentModel, null, timestamp, trajectoryTimestamp, fallbackTimestamp, sessionId, path));
            }

            foreach (var retryUsage in metadata.RetryUsages)
            {
                nonTokenBearing += AppendUsageEvent(events, identityTimestamps, retryUsage, new EventContext(
                    currentModel, null, timestamp, trajectoryTimestamp, fallbackTimestamp, sessionId, path));
            }
        }

        return new ParseOutcome { Events = events, NonTokenBearingUsages = nonTokenBearing };
    }

    private static bool DatabaseSnapshotIsEmpty(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_count";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new SqliteException("Antigravity database page count is unavailable", 1);
        }

        return reader.GetInt64(0) == 0;
    }

    /// <summary>gen_metadata 行（必需表）：SELECT idx, data ORDER BY idx ASC；表缺失/列不符 →
    /// SqliteException 向上抛（蓝本同拒绝面：查询失败使整库不可用）。</summary>
    private static List<(long Idx, GeneratorMetadata Metadata)> ReadGenerationRows(SqliteConnection connection)
    {
        var rows = new List<(long, GeneratorMetadata)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var idx = reader.GetInt64(0);
            var blob = ReadBlob(reader, 1);
            rows.Add((idx, ParseGeneratorMetadata(blob)));
        }

        return rows;
    }

    /// <summary>steps 行（可选表）：表不存在返回空（蓝本 table_exists 同式）；存在则固定列查询
    /// （schema 不符 → SqliteException 向上抛，蓝本 propagates_present_optional_table_schema_errors
    /// 同拒绝面）。</summary>
    private static List<(long Idx, StepMetadata Metadata)> ReadStepRows(SqliteConnection connection)
    {
        if (!SqliteTableSchema.TableExists(connection, "steps"))
        {
            return [];
        }

        var rows = new List<(long, StepMetadata)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL ORDER BY idx ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var idx = reader.GetInt64(0);
            var blob = ReadBlob(reader, 1);
            rows.Add((idx, ParseStepMetadata(blob)));
        }

        return rows;
    }

    /// <summary>trajectory 时间戳（可选表）：按 rowid ASC 逐行解析，首个有效候选生效
    /// （蓝本 timestamp.is_none() 才写入——前序行无候选时后续行仍可补上）。</summary>
    private static long? ReadTrajectoryTimestamp(SqliteConnection connection)
    {
        if (!SqliteTableSchema.TableExists(connection, "trajectory_metadata_blob"))
        {
            return null;
        }

        long? timestamp = null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM trajectory_metadata_blob ORDER BY rowid ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var blob = ReadBlob(reader, 0);
            var candidate = AntigravityProtoReader.FieldBytes(AntigravityProtoReader.DecodeFields(blob), 2) is { } timestampMessage
                ? AntigravityProtoReader.ParseTimestampMessage(timestampMessage)
                : null;
            if (timestamp is null)
            {
                timestamp = candidate;
            }
        }

        return timestamp;
    }

    /// <summary>blob 列读取：NULL / 非 BLOB 值向上抛（蓝本 read::&lt;Vec&lt;u8&gt;&gt; 严格类型同拒绝面）。</summary>
    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidCastException("Antigravity metadata blob is NULL");
        }

        return reader.GetValue(ordinal) is byte[] blob
            ? blob
            : throw new InvalidCastException("Antigravity metadata blob is not a BLOB value");
    }

    /// <summary>
    /// 事件追加（蓝本 append_usage_event 逐字移植）：非 token-bearing usage 静默跳过并计数
    /// （返回 1，仓库 I-004 显式化口径）；时间戳链 metadata(rank 3) → 身份键回查 →
    /// trajectory(rank 1) → mtime(rank 0)；身份键时间戳更新规则：rank 更高或同 rank 更早者胜。
    /// </summary>
    private static int AppendUsageEvent(
        List<UsageEvent> events,
        Dictionary<string, (long Timestamp, byte Rank)> identityTimestamps,
        ModelUsage usage,
        EventContext context)
    {
        if (!usage.IsTokenBearing())
        {
            return 1;
        }

        long timestamp;
        byte timestampRank;
        if (context.Timestamp is { } contextTimestamp)
        {
            timestamp = contextTimestamp.Timestamp;
            timestampRank = contextTimestamp.Rank;
        }
        else
        {
            (long, byte)? identityMatch = null;
            foreach (var identity in usage.IdentityKeys())
            {
                if (identityTimestamps.TryGetValue(identity, out var identityFound))
                {
                    identityMatch = identityFound;
                    break;
                }
            }

            if (identityMatch is { } identityHit)
            {
                timestamp = identityHit.Item1;
                timestampRank = identityHit.Item2;
            }
            else if (context.TrajectoryTimestamp is { } trajectoryTimestamp)
            {
                timestamp = trajectoryTimestamp;
                timestampRank = 1;
            }
            else
            {
                timestamp = context.FallbackTimestamp;
                timestampRank = 0;
            }
        }

        var totalOutputTokens = Math.Max(
            usage.TotalOutputTokens,
            SaturatingAdd(usage.VisibleOutputTokens, usage.ReasoningTokens));
        var outputTokens = Math.Max(
            usage.VisibleOutputTokens,
            SaturatingSub(totalOutputTokens, usage.ReasoningTokens));
        var reasoningTokens = Math.Max(
            usage.ReasoningTokens,
            SaturatingSub(totalOutputTokens, outputTokens));
        // 蓝本 model 链：usage.model_id 映射归一 → context.model 归一 → DEFAULT_MODEL 兜底。
        string? model = null;
        if (usage.ModelId is { } usageModelId)
        {
            model = NormalizeAntigravityModel(ModelNameFromId(usageModelId));
        }

        if (model is null && context.Model is { } contextModel)
        {
            model = NormalizeAntigravityModel(contextModel);
        }

        model ??= DefaultModel;
        var provider = usage.Provider ?? context.Provider;
        var (messageId, messageIdRank) = usage.PreferredMessageId();
        var identities = usage.IdentityKeys();
        foreach (var identity in identities)
        {
            var shouldUpdate = !identityTimestamps.TryGetValue(identity, out var existing)
                || timestampRank > existing.Rank
                || (timestampRank == existing.Rank && timestamp < existing.Timestamp);
            if (shouldUpdate)
            {
                identityTimestamps[identity] = (timestamp, timestampRank);
            }
        }

        events.Add(new UsageEvent
        {
            Timestamp = timestamp,
            SessionId = context.SessionId,
            SourceFile = context.SourceFile,
            Model = model,
            Provider = provider,
            InputTokens = usage.InputTokens,
            OutputTokens = outputTokens,
            CacheCreationTokens = usage.CacheCreationTokens,
            CacheReadTokens = usage.CacheReadTokens,
            ReasoningTokens = reasoningTokens,
            TotalOutputTokens = totalOutputTokens,
            TotalTokens = SaturatingAdd(SaturatingAdd(SaturatingAdd(
                usage.InputTokens, usage.CacheCreationTokens), usage.CacheReadTokens), totalOutputTokens),
            MessageId = messageId,
            Identities = identities,
            TimestampRank = timestampRank,
            MessageIdRank = messageIdRank,
        });
        return 0;
    }

    /// <summary>重复事件合并（蓝本 merge_usage_event 逐字保真）：token 桶取 max、total 重算、
    /// 缺省模型让位于具名模型、provider 补缺、时间戳高 rank / 同 rank 更早者胜、消息 id 高 rank 胜、
    /// 身份键并集。</summary>
    public static void MergeUsageEvent(UsageEvent target, UsageEvent duplicate)
    {
        target.InputTokens = Math.Max(target.InputTokens, duplicate.InputTokens);
        target.OutputTokens = Math.Max(target.OutputTokens, duplicate.OutputTokens);
        target.CacheCreationTokens = Math.Max(target.CacheCreationTokens, duplicate.CacheCreationTokens);
        target.CacheReadTokens = Math.Max(target.CacheReadTokens, duplicate.CacheReadTokens);
        target.ReasoningTokens = Math.Max(target.ReasoningTokens, duplicate.ReasoningTokens);
        target.TotalOutputTokens = Math.Max(
            Math.Max(target.TotalOutputTokens, duplicate.TotalOutputTokens),
            SaturatingAdd(target.OutputTokens, target.ReasoningTokens));
        target.TotalTokens = SaturatingAdd(SaturatingAdd(SaturatingAdd(
            target.InputTokens, target.CacheCreationTokens), target.CacheReadTokens), target.TotalOutputTokens);
        if (target.Model == DefaultModel && duplicate.Model != DefaultModel)
        {
            target.Model = duplicate.Model;
        }

        target.Provider ??= duplicate.Provider;
        var useDuplicateTimestamp = duplicate.TimestampRank > target.TimestampRank
            || (duplicate.TimestampRank == target.TimestampRank && duplicate.Timestamp < target.Timestamp);
        if (useDuplicateTimestamp)
        {
            target.Timestamp = duplicate.Timestamp;
            target.TimestampRank = duplicate.TimestampRank;
        }

        if (duplicate.MessageIdRank > target.MessageIdRank)
        {
            target.MessageId = duplicate.MessageId;
            target.MessageIdRank = duplicate.MessageIdRank;
        }

        foreach (var identity in duplicate.Identities)
        {
            if (!target.Identities.Contains(identity))
            {
                target.Identities.Add(identity);
            }
        }
    }

    /// <summary>
    /// 身份键去重（蓝本 loader.rs deduplicate_events 逐字移植）：共享任一身份键的事件合并到首个
    /// 槽位（合并链式传递），无身份键的事件独立成槽；保持发现序。
    /// </summary>
    public static List<UsageEvent> DeduplicateEvents(IEnumerable<UsageEvent> events)
    {
        var slots = new List<UsageEvent?>();
        var identityIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var @event in events)
        {
            var matchingIndexes = new List<int>();
            foreach (var identity in @event.Identities)
            {
                if (identityIndexes.TryGetValue(identity, out var index))
                {
                    matchingIndexes.Add(index);
                }
            }

            matchingIndexes.Sort();
            var deduped = new List<int>();
            foreach (var index in matchingIndexes)
            {
                if (deduped.Count == 0 || deduped[^1] != index)
                {
                    deduped.Add(index);
                }
            }

            if (deduped.Count == 0)
            {
                var targetIndex = slots.Count;
                foreach (var identity in @event.Identities)
                {
                    identityIndexes[identity] = targetIndex;
                }

                slots.Add(@event);
                continue;
            }

            var targetIndexFirst = deduped[0];
            var target = slots[targetIndexFirst] ?? throw new InvalidOperationException("deduplication target must exist");
            foreach (var duplicateIndex in deduped.Skip(1))
            {
                if (slots[duplicateIndex] is { } duplicate)
                {
                    MergeUsageEvent(target, duplicate);
                    slots[duplicateIndex] = null;
                }
            }

            MergeUsageEvent(target, @event);
            foreach (var identity in target.Identities)
            {
                identityIndexes[identity] = targetIndexFirst;
            }
        }

        return slots.Where(slot => slot is not null).Select(slot => slot!).ToList();
    }

    // ---- blob 解码（蓝本 parse_generator_metadata / parse_step_metadata / parse_model_info /
    // parse_retry_info / parse_model_usage / parse_generation_info_timestamp /
    // parse_trajectory_timestamp 逐字段移植；字段号见各处注释） ----

    private static GeneratorMetadata ParseGeneratorMetadata(byte[] blob)
    {
        var root = AntigravityProtoReader.DecodeFields(blob);
        var chatModel = AntigravityProtoReader.FieldBytes(root, 1)
            ?? throw new AntigravityProtoReader.ProtoDecodeException("missing chat model field 1");
        var chatModelFields = AntigravityProtoReader.DecodeFields(chatModel);
        var usage = AntigravityProtoReader.FieldBytes(chatModelFields, 4) is { } usageBlob
            ? ParseModelUsage(usageBlob)
            : null;
        var retryUsages = AntigravityProtoReader.FieldBytesAll(chatModelFields, 17)
            .Select(retry => ParseRetryInfo(retry))
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!)
            .ToList();
        var timestamp = AntigravityProtoReader.FieldBytes(chatModelFields, 9) is { } generationInfoBlob
            ? ParseGenerationInfoTimestamp(generationInfoBlob)
            : null;
        var model = AntigravityProtoReader.FieldText(chatModelFields, 19)
            ?? AntigravityProtoReader.FieldText(chatModelFields, 21);
        return new GeneratorMetadata
        {
            Model = model,
            ModelId = NonZero(AntigravityProtoReader.FieldVarint(chatModelFields, 3)),
            Usage = usage,
            RetryUsages = retryUsages,
            Timestamp = timestamp,
        };
    }

    private static StepMetadata ParseStepMetadata(byte[] blob)
    {
        var fields = AntigravityProtoReader.DecodeFields(blob);
        var usage = AntigravityProtoReader.FieldBytes(fields, 9) is { } usageBlob
            ? ParseModelUsage(usageBlob)
            : null;
        var retryUsages = AntigravityProtoReader.FieldBytesAll(fields, 28)
            .Select(retry => ParseRetryInfo(retry))
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!)
            .ToList();
        var modelInfo = AntigravityProtoReader.FieldBytes(fields, 24) is { } modelInfoBlob
            ? ParseModelInfo(modelInfoBlob)
            : new ModelInfo();
        // 时间戳：8 号字段优先，1 号回退（蓝本 field_bytes(8).or_else(field_bytes(1))）。
        var timestampBlob = AntigravityProtoReader.FieldBytes(fields, 8)
            ?? AntigravityProtoReader.FieldBytes(fields, 1);
        var timestamp = timestampBlob is { } resolved
            ? AntigravityProtoReader.ParseTimestampMessage(resolved)
            : null;
        return new StepMetadata
        {
            Model = modelInfo.Model,
            ModelId = modelInfo.ModelId,
            Provider = modelInfo.Provider,
            Usage = usage,
            RetryUsages = retryUsages,
            Timestamp = timestamp,
        };
    }

    private sealed class ModelInfo
    {
        public string? Model { get; init; }

        public ulong? ModelId { get; init; }

        public ulong? Provider { get; init; }
    }

    private static ModelInfo ParseModelInfo(byte[] blob)
    {
        var fields = AntigravityProtoReader.DecodeFields(blob);
        return new ModelInfo
        {
            Model = AntigravityProtoReader.FieldText(fields, 12)
                ?? AntigravityProtoReader.FieldText(fields, 8),
            ModelId = NonZero(AntigravityProtoReader.FieldVarint(fields, 1)),
            Provider = NonZero(AntigravityProtoReader.FieldVarint(fields, 7)),
        };
    }

    private static ModelUsage? ParseRetryInfo(byte[] blob)
    {
        var fields = AntigravityProtoReader.DecodeFields(blob);
        return AntigravityProtoReader.FieldBytes(fields, 2) is { } usageBlob ? ParseModelUsage(usageBlob) : null;
    }

    private static ModelUsage ParseModelUsage(byte[] blob)
    {
        var fields = AntigravityProtoReader.DecodeFields(blob);
        return new ModelUsage
        {
            ModelId = NonZero(AntigravityProtoReader.FieldVarint(fields, 1)),
            InputTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 2) ?? 0),
            TotalOutputTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 3) ?? 0),
            CacheCreationTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 4) ?? 0),
            CacheReadTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 5) ?? 0),
            ReasoningTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 9) ?? 0),
            VisibleOutputTokens = ClampToLong(AntigravityProtoReader.FieldVarint(fields, 10) ?? 0),
            Provider = NonZero(AntigravityProtoReader.FieldVarint(fields, 6)),
            MessageId = AntigravityProtoReader.FieldText(fields, 7),
            ResponseId = AntigravityProtoReader.FieldText(fields, 11),
            ProviderAssignedMessageId = AntigravityProtoReader.FieldText(fields, 12),
        };
    }

    private static long? ParseGenerationInfoTimestamp(byte[] blob)
    {
        var generationInfo = AntigravityProtoReader.DecodeFields(blob);
        return AntigravityProtoReader.FieldBytes(generationInfo, 4) is { } timestampMessage
            ? AntigravityProtoReader.ParseTimestampMessage(timestampMessage)
            : null;
    }

    private static ulong? NonZero(ulong? value) => value is { } raw && raw != 0 ? raw : null;

    /// <summary>u64 → long 读取层钳制（I-017 偏差⑥同款：现实值远小于边界）。</summary>
    private static long ClampToLong(ulong value)
        => value > (ulong)long.MaxValue ? long.MaxValue : unchecked((long)value);

    private static long SaturatingAdd(long left, long right) => AntigravityProtoReader.SaturatingAdd(left, right);

    /// <summary>u64 saturating_sub（非负 long 域：不足减为 0，蓝本 u64 语义）。</summary>
    private static long SaturatingSub(long left, long right) => left > right ? left - right : 0;

    // ---- 模型命名与归一化（蓝本 model_name_from_id / normalize_antigravity_model 逐字移植） ----

    /// <summary>google 系 provider 枚举（蓝本 API_PROVIDER_*：计价候选补 google 前缀组的范围）。</summary>
    public static readonly ulong[] GoogleProviders = [ApiProviderGoogleVertex, ApiProviderGoogleGemini, ApiProviderGoogleEvergreen];

    /// <summary>google 前缀组（蓝本 PROVIDER_PREFIXES）。</summary>
    public static readonly string[] ProviderPrefixes = ["google", "gemini", "vertex_ai", "openrouter/google"];

    /// <summary>
    /// gemini flash effort 变体的计价别名（蓝本 gemini_flash_pricing_alias 逐字移植，AG-1）：
    /// gemini-3.x-flash-{high,medium,low} → 基础名 gemini-3.x-flash；其余 null。
    /// </summary>
    public static string? GeminiFlashPricingAlias(string model)
    {
        return model switch
        {
            "gemini-3.8-flash-high" or "gemini-3.8-flash-medium" or "gemini-3.8-flash-low" => "gemini-3.8-flash",
            "gemini-3.7-flash-high" or "gemini-3.7-flash-medium" or "gemini-3.7-flash-low" => "gemini-3.7-flash",
            "gemini-3.6-flash-high" or "gemini-3.6-flash-medium" or "gemini-3.6-flash-low" => "gemini-3.6-flash",
            _ => null,
        };
    }

    /// <summary>
    /// 计价模型候选（蓝本 model_candidates 逐字移植，AG-1）：[模型名] +（flash effort 变体时）[基础名]，
    /// 每个名字在 provider ∈ {3=vertex_ai, 24=gemini, 30=evergreen} 时再展开 google/ · gemini/ ·
    /// vertex_ai/ · openrouter/google/ 前缀变体（先裸名后前缀组、逐名展开），去重保序。
    /// 匹配门槛（蓝本 calculate_antigravity_cost）：flash 变体（exact_lookup）走 exact-with-fallback
    /// 逐候选精确（桌面口径 <see cref="PricingCandidateWalk.ExactOnlyAcrossCandidates"/>）；
    /// 其余逐候选完整匹配（exact + 家族兜底）。
    /// </summary>
    public static IReadOnlyList<string> BuildModelCandidates(string model, ulong? provider)
    {
        var useGooglePrefixes = provider is { } value && Array.IndexOf(GoogleProviders, value) >= 0;
        var models = new List<string> { model };
        if (GeminiFlashPricingAlias(model) is { } alias)
        {
            models.Add(alias);
        }

        var candidates = new List<string>();
        foreach (var candidateModel in models)
        {
            candidates.Add(candidateModel);
            if (useGooglePrefixes)
            {
                foreach (var prefix in ProviderPrefixes)
                {
                    candidates.Add($"{prefix}/{candidateModel}");
                }
            }
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    public static string ModelNameFromId(ulong modelId)
    {
        return modelId switch
        {
            246 => "gemini-2.5-pro",
            312 => "gemini-2.5-flash",
            313 or 329 => "gemini-2.5-flash-thinking",
            330 => "gemini-2.5-flash-lite",
            281 or 282 => "claude-4-sonnet",
            290 or 291 => "claude-4-opus",
            333 or 334 => "claude-4.5-sonnet",
            340 or 341 => "claude-4.5-haiku",
            342 => "model_openai_gpt_oss_120b_medium",
            1318 => "gemini-3.8-flash-high",
            1319 => "gemini-3.8-flash-medium",
            1320 => "gemini-3.8-flash-low",
            1298 => "gemini-3.7-flash-high",
            1299 => "gemini-3.7-flash-medium",
            1300 => "gemini-3.7-flash-low",
            1071 => "gemini-3.6-flash-high",
            1072 => "gemini-3.6-flash-medium",
            1073 => "gemini-3.6-flash-low",
            >= 1_000 => $"model_placeholder_m{modelId - 1_000}",
            _ => $"antigravity-model-{modelId}",
        };
    }

    public static string? NormalizeAntigravityModel(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();
        var effortVariant = lower switch
        {
            "gemini 3.8 flash (high)" => "gemini-3.8-flash-high",
            "gemini 3.8 flash (medium)" => "gemini-3.8-flash-medium",
            "gemini 3.8 flash (low)" => "gemini-3.8-flash-low",
            "gemini 3.7 flash (high)" => "gemini-3.7-flash-high",
            "gemini 3.7 flash (medium)" => "gemini-3.7-flash-medium",
            "gemini 3.7 flash (low)" => "gemini-3.7-flash-low",
            "gemini 3.6 flash (high)" => "gemini-3.6-flash-high",
            "gemini 3.6 flash (medium)" => "gemini-3.6-flash-medium",
            "gemini 3.6 flash (low)" => "gemini-3.6-flash-low",
            _ => null,
        };
        if (effortVariant is not null)
        {
            return effortVariant;
        }

        var parenIndex = lower.IndexOf('(');
        var baseName = parenIndex < 0 ? lower : lower[..parenIndex].Trim();
        var normalized = baseName switch
        {
            "gemini 3.8 flash" or "gemini 3.8 flash thinking" => "gemini-3.8-flash",
            "gemini 3.7 flash" or "gemini 3.7 flash thinking" => "gemini-3.7-flash",
            "gemini 3.7 pro" or "gemini 3.7 pro thinking" => "gemini-3.7-pro",
            "gemini 3.6 flash" or "gemini 3 flash" => "gemini-3.6-flash",
            "gemini 3.6 pro" => "gemini-3.6-pro",
            "gemini 3 pro" or "gemini 3 pro thinking" => "gemini-3-pro",
            "gemini 2.5 flash" => "gemini-2.5-flash",
            "gemini 2.5 pro" => "gemini-2.5-pro",
            "gemini 2.0 flash" or "gemini 2 flash" => "gemini-2.0-flash",
            "gemini 2.0 pro" => "gemini-2.0-pro",
            "gemini 1.5 flash" => "gemini-1.5-flash",
            "gemini 1.5 pro" => "gemini-1.5-pro",
            "model_placeholder_m318" => "gemini-3.8-flash-high",
            "model_placeholder_m319" => "gemini-3.8-flash-medium",
            "model_placeholder_m320" => "gemini-3.8-flash-low",
            "model_placeholder_m298" => "gemini-3.7-flash-high",
            "model_placeholder_m299" => "gemini-3.7-flash-medium",
            "model_placeholder_m300" => "gemini-3.7-flash-low",
            "model_placeholder_m71" => "gemini-3.6-flash-high",
            "model_placeholder_m72" => "gemini-3.6-flash-medium",
            "model_placeholder_m73" => "gemini-3.6-flash-low",
            "model_placeholder_m26" => "claude-opus-4-6",
            "model_placeholder_m35" => "claude-sonnet-4-6",
            "model_placeholder_m36" or "model_placeholder_m37" or "model_placeholder_m16" => "gemini-3.1-pro",
            "model_placeholder_m18" or "model_placeholder_m84" or "model_placeholder_m47" => "gemini-3-flash-preview",
            "model_placeholder_m132" or "model_placeholder_m133" => "gemini-3.5-flash-high",
            "model_placeholder_m187" => "gemini-3.5-flash-extra-low",
            "model_placeholder_m20" => "gemini-3.5-flash-medium",
            "model_openai_gpt_oss_120b_medium" => "gpt-oss-120b-medium",
            "gemini-pro-default" or "gemini-pro-agent" => "gemini-3.1-pro",
            "gemini-3-flash-agent" or "gemini-3-flash-agent-a" or "gemini-3-flash-agent-b"
                or "gemini-3-flash-a" or "gemini-3-flash-b" => "gemini-3.5-flash-high",
            "gemini-3-flash-c" or "gemini-3-flash" => "gemini-3-flash-preview",
            "gemini-3.5-flash-low" => "gemini-3.5-flash-medium",
            "gemini-3.1-pro-high" or "gemini-3.1-pro-low" => "gemini-3.1-pro",
            "gemini-3-pro-high" or "gemini-3-pro-low" => "gemini-3-pro",
            "claude 3.7 sonnet" or "claude 3.7 sonnet thinking" => "claude-3-7-sonnet",
            "claude 3.5 sonnet" => "claude-3-5-sonnet",
            "claude 3.5 haiku" => "claude-3-5-haiku",
            "claude 3 opus" => "claude-3-opus",
            _ => null,
        };
        if (normalized is not null)
        {
            return normalized;
        }

        var converted = baseName.Replace(' ', '-');
        if (converted.StartsWith("gemini-", StringComparison.Ordinal)
            || converted.StartsWith("claude-", StringComparison.Ordinal)
            || converted.StartsWith("gpt-", StringComparison.Ordinal))
        {
            return converted;
        }

        return trimmed;
    }
}
