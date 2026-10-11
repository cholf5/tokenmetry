using System.Globalization;
using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// Copilot CLI session-state 逐文件解析（I-008）。解析与差分语义移植自
/// ../ccusage rust/adapters/copilot（parser.rs parse_session_state_file + loader.rs
/// reconcile_session_state_entries 的 session-state 主线）。蓝本许可：
/// MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE）。
/// <list type="bullet">
/// <item>只消费 type 为 session.shutdown / session.usage_checkpoint 的事件（行级预过滤与蓝本
/// LinePrefilter::any 同口径）；checkpoint 行在本卡不产出记录（totalNanoAiu 积分对账为进阶范围，
/// 见卡片实现边界），计 LinesSkipped。</item>
/// <item>shutdown 的 data.modelMetrics 是按 (session, model) 的<b>累计快照</b>：resume 后每关一次
/// 进程就多一条包含历史总和的 shutdown。解析产出各模型的累计条目后做相邻快照差分——组内按
/// (时间戳, 出现序) 排序，首条保留、后条对前条做 saturating 减法，resume 不重复出账、总量守恒，
/// 区间归属到后一条快照的事件日期（CLI 同口径）。</item>
/// <item>差分在单文件内完成且与蓝本全量池化等价：分组键含 session id（= events.jsonl 的一级父目录名，
/// 一个根目录内会话目录唯一），(session, model) 组不会跨文件，故文件内差分 == 蓝本"池化→去重→分组→
/// 差分"的结果；跨扫描增量由共享引擎的文件记账承担（变更文件整文件重解析），无跨扫描差分状态。</item>
/// <item>token 语义：Copilot 把缓存读写计入 inputTokens，uncached input = inputTokens −
/// (cacheRead + cacheWrite)（解析时先算，差分作用在 uncached 累计值上——与蓝本 subtract_usage 一致）；
/// reasoningTokens 已含在 outputTokens 内，不单列。</item>
/// <item>内部模型后缀剥离：-1m-internal / -1m（先长后短，仅一次）在分组与定价前剥掉，
/// 使 1m 上下文变体并入同组并命中基础模型价格。</item>
/// <item>同文件去重（蓝本 deduplicate_session_entries）：同 dedup key 保留时间戳最新的一条
/// （相等取后出现者），防重复 shutdown 事件双计。</item>
/// <item>零 token 区间不产出记录（蓝本 has_usage 还接受 request_count&gt;0 的零 token 区间用于
/// messageCount 展示；统一记录无请求数字段，零 token 记录对聚合零贡献——口径偏差见卡片 Dev Log）。</item>
/// </list>
/// 行级容错为仓库 I-004 口径（蓝本 records() 对反序列化失败的行静默跳过）：坏 JSON 计 MalformedLines；
/// 预过滤未命中 / 非 shutdown·checkpoint 事件 / checkpoint 行计 LinesSkipped；
/// shutdown·checkpoint 行缺可解析时间戳计 InvalidRecords（DSH 适配器同口径偏差）。
/// 记录 CostUsd 恒 null：成本由定价引擎按区间快照时间戳计算（CLI cost-at-timestamp 同口径）；
/// MessageId/RequestId 恒 null（共享去重器直通，去重已在文件内按蓝本 dedup key 完成）。
/// </summary>
public sealed class CopilotSessionParser
{
    public const string AgentName = "Copilot CLI";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "GitHub Copilot CLI"）。</summary>
    public const string ProjectPath = "GitHub Copilot CLI";

    private const string ShutdownEvent = "session.shutdown";
    private const string UsageCheckpointEvent = "session.usage_checkpoint";

    /// <summary>解析一个会话目录的 events.jsonl，产出该文件的区间用量记录。</summary>
    public Task<JsonlFileParseOutcome> ParseAsync(TextReader reader, string sourceFile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 蓝本 parse_session_state_file：session id 取 events.jsonl 一级父目录名（trim，非空）；
        // 解析不出会话目录名时整文件不产出（无计数，与蓝本返回 default 一致）。
        var sessionId = ResolveSessionId(sourceFile);
        if (string.IsNullOrEmpty(sessionId))
        {
            return Task.FromResult(new JsonlFileParseOutcome(
                Records: [], FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0,
                Interrupted: false));
        }

        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;
        var snapshots = new List<CumulativeSnapshot>();

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // 蓝本 LinePrefilter::any(["session.shutdown", "session.usage_checkpoint"])：
            // 预过滤未命中的行不进入 JSON 解析（events.jsonl 主体是其他事件类型）。
            if (!trimmed.Contains(ShutdownEvent, StringComparison.Ordinal)
                && !trimmed.Contains(UsageCheckpointEvent, StringComparison.Ordinal))
            {
                linesSkipped++;
                continue;
            }

            JsonElement element;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                malformedLines++;
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                linesSkipped++;
                continue;
            }

            var eventType = typeElement.GetString();
            if (eventType == UsageCheckpointEvent)
            {
                // usage_checkpoint 只服务积分（totalNanoAiu）对账——本卡实现边界，行不产出记录。
                linesSkipped++;
                continue;
            }

            if (eventType != ShutdownEvent)
            {
                linesSkipped++;
                continue;
            }

            if (!element.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || timestampElement.GetString() is not { Length: > 0 } timestampText
                || !JsonlTimestamp.TryParse(timestampText, out var timestamp))
            {
                // 时间戳缺失 / 不可解析：区间无法归日，丢弃（仓库口径计 invalid）。
                invalidRecords++;
                continue;
            }

            CollectShutdownSnapshots(
                element,
                sessionId,
                timestamp,
                FormatRfc3339Millis(timestamp),
                snapshots);
        }

        var records = ReconcileIntervals(snapshots, sourceFile);
        return Task.FromResult(new JsonlFileParseOutcome(
            records,
            FilesRead: 1,
            linesSkipped,
            malformedLines,
            invalidRecords,
            Interrupted: false));
    }

    private static string? ResolveSessionId(string sourceFile)
    {
        var directory = Path.GetDirectoryName(sourceFile);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var sessionName = Path.GetFileName(directory)?.Trim();
        return string.IsNullOrEmpty(sessionName) ? null : sessionName;
    }

    /// <summary>
    /// 抽取一条 shutdown 的各模型累计快照（蓝本 parse_session_state_file 的 modelMetrics 循环）。
    /// modelMetrics 整体缺失 / 非对象 → 无快照；任一模型值非对象 → 整表按缺失处理
    /// （蓝本 lenient_object 对 BTreeMap 反序列化失败整体返回 None 的 all-or-nothing 口径）。
    /// </summary>
    private static void CollectShutdownSnapshots(
        JsonElement element,
        string sessionId,
        DateTimeOffset timestamp,
        string timestampText,
        List<CumulativeSnapshot> snapshots)
    {
        if (!element.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("modelMetrics", out var modelMetrics)
            || modelMetrics.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var metric in modelMetrics.EnumerateObject())
        {
            if (metric.Value.ValueKind != JsonValueKind.Object)
            {
                // 任一模型值不是对象：蓝本 lenient_object 让整张表反序列化失败（无快照产出）。
                return;
            }
        }

        foreach (var metric in modelMetrics.EnumerateObject())
        {
            var model = NormalizeModel(metric.Name);
            var metrics = metric.Value;

            if (!metrics.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var inputTokens = ReadTokens(usage, "inputTokens");
            var outputTokens = ReadTokens(usage, "outputTokens");
            var cacheReadTokens = ReadTokens(usage, "cacheReadTokens");
            var cacheWriteTokens = ReadTokens(usage, "cacheWriteTokens");
            var reasoningTokens = ReadTokens(usage, "reasoningTokens");
            var requestCount = ReadRequestCount(metrics);

            // 蓝本口径：空模型名，或全部用量桶与请求数皆 0 的条目跳过（请求数 &gt; 0 的全零条目保留，
            // 作为差分基线参与后续减法）。
            if (model.Length == 0
                || (inputTokens == 0 && outputTokens == 0 && cacheReadTokens == 0
                    && cacheWriteTokens == 0 && reasoningTokens == 0 && requestCount == 0))
            {
                continue;
            }

            // Copilot 把缓存读写计入 inputTokens：uncached input 在解析时先拆出（saturating），
            // 差分作用在 uncached 累计值上（蓝本 uncached_session_input_tokens + subtract_usage 口径）。
            var uncachedInput = SaturatingSubtract(
                inputTokens, SaturatingAdd(cacheReadTokens, cacheWriteTokens));

            snapshots.Add(new CumulativeSnapshot(
                sessionId,
                model,
                timestamp,
                timestampText,
                uncachedInput,
                outputTokens,
                cacheWriteTokens,
                cacheReadTokens,
                requestCount,
                BuildDedupKey(
                    element, sessionId, timestampText, model,
                    inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, requestCount)));
        }
    }

    /// <summary>
    /// 相邻快照差分（蓝本 deduplicate_session_entries → reconcile_session_state_entries）：
    /// 同 key 保留最新时间戳（相等取后出现者）→ 按 (session, model) 分组 → 组内按 (时间戳, 出现序)
    /// 排序 → 首条保留、后条对前条做 saturating 减法 → 有 token 的区间产出，按 (时间戳, dedup key) 排序。
    /// </summary>
    private static List<UsageRecord> ReconcileIntervals(List<CumulativeSnapshot> snapshots, string sourceFile)
    {
        // 同 key 去重：保序保留每 key 最新的一条（时间戳相等时取后出现者，蓝本 <= 判定）。
        var keptIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (!keptIndexes.TryGetValue(snapshot.DedupKey, out var previous)
                || snapshots[previous].Timestamp <= snapshot.Timestamp)
            {
                keptIndexes[snapshot.DedupKey] = index;
            }
        }

        var grouped = keptIndexes.Values
            .OrderBy(index => index)
            .GroupBy(index => (snapshots[index].SessionId, snapshots[index].Model))
            .OrderBy(group => group.Key.SessionId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal);

        var intervals = new List<IntervalUsage>();
        foreach (var group in grouped)
        {
            var sorted = group
                .OrderBy(index => snapshots[index].Timestamp)
                .ThenBy(index => index)
                .Select(index => snapshots[index])
                .ToList();

            CumulativeSnapshot? baseline = null;
            foreach (var snapshot in sorted)
            {
                var interval = baseline is null
                    ? new IntervalUsage(snapshot.Timestamp, snapshot.SessionId, snapshot.Model, snapshot.DedupKey,
                        snapshot.InputTokens, snapshot.OutputTokens,
                        snapshot.CacheCreationTokens, snapshot.CacheReadTokens)
                    : new IntervalUsage(snapshot.Timestamp, snapshot.SessionId, snapshot.Model, snapshot.DedupKey,
                        SaturatingSubtract(snapshot.InputTokens, baseline.InputTokens),
                        SaturatingSubtract(snapshot.OutputTokens, baseline.OutputTokens),
                        SaturatingSubtract(snapshot.CacheCreationTokens, baseline.CacheCreationTokens),
                        SaturatingSubtract(snapshot.CacheReadTokens, baseline.CacheReadTokens));
                baseline = snapshot;

                if (interval.InputTokens > 0 || interval.OutputTokens > 0
                    || interval.CacheCreationTokens > 0 || interval.CacheReadTokens > 0)
                {
                    intervals.Add(interval);
                }
            }
        }

        return intervals
            .OrderBy(interval => interval.Timestamp)
            .ThenBy(interval => interval.DedupKey, StringComparer.Ordinal)
            .Select(interval => new UsageRecord
            {
                Timestamp = interval.Timestamp,
                Agent = AgentName,
                ProjectPath = ProjectPath,
                SessionId = interval.SessionId,
                Model = interval.Model,
                CostUsd = null,
                InputTokens = interval.InputTokens,
                OutputTokens = interval.OutputTokens,
                CacheCreationTokens = interval.CacheCreationTokens,
                CacheCreationEphemeral5mTokens = 0,
                CacheCreationEphemeral1hTokens = 0,
                CacheReadTokens = interval.CacheReadTokens,
                IsSidechain = false,
                MessageId = null,
                RequestId = null,
                SourceFile = sourceFile,
            })
            .ToList();
    }

    /// <summary>
    /// dedup key（蓝本 session_state_dedup_key）：事件带 id 时 "shutdown:{session}:{id}:{model}"，
    /// 否则按原始累计用量字段拼装（含 reasoningTokens，按蓝本字段序）。
    /// </summary>
    private static string BuildDedupKey(
        JsonElement element,
        string sessionId,
        string timestampText,
        string model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long reasoningTokens,
        long requestCount)
    {
        if (element.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && idElement.GetString() is { Length: > 0 } eventId)
        {
            return $"shutdown:{sessionId}:{eventId}:{model}";
        }

        return FormattableString.Invariant(
            $"shutdown:{sessionId}:{timestampText}:{model}:{inputTokens}:{outputTokens}:{cacheReadTokens}:{cacheWriteTokens}:{reasoningTokens}:{requestCount}");
    }

    /// <summary>内部模型后缀剥离（蓝本 normalize_copilot_model）：先 -1m-internal 后 -1m，仅一次。</summary>
    public static string NormalizeModel(string model)
    {
        var trimmed = model.Trim();
        if (trimmed.EndsWith("-1m-internal", StringComparison.Ordinal))
        {
            return trimmed[..^"-1m-internal".Length];
        }

        if (trimmed.EndsWith("-1m", StringComparison.Ordinal))
        {
            return trimmed[..^"-1m".Length];
        }

        return trimmed;
    }

    /// <summary>lenient_u64 同口径：仅非负整数 JSON number 生效，其余（浮点 / 字符串 / 负数 / 缺失）为 0。</summary>
    private static long ReadTokens(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetUInt64(out var tokens)
              ? (long)Math.Min(tokens, long.MaxValue)
              : 0;

    /// <summary>requests.count（蓝本 requests.lenient_object + count lenient_u64）。</summary>
    private static long ReadRequestCount(JsonElement metrics)
        => metrics.TryGetProperty("requests", out var requests)
           && requests.ValueKind == JsonValueKind.Object
           ? ReadTokens(requests, "count")
           : 0;

    // 防御性饱和运算：唯一调用点 uncached 拆分的两操作数均经 ReadTokens 解析（仅非负整数字面量
    // 生效，其余为 0），恒 ≥ 0 且不超过 long.MaxValue——left >= 0 / right >= 0 的分支按当前调用图
    // 不可达，long.MaxValue 仅是溢出兜底，避免对负数回退出语义怪异的值。
    private static long SaturatingAdd(long left, long right)
        => left >= 0 && right >= 0 && left <= long.MaxValue - right ? left + right : long.MaxValue;

    private static long SaturatingSubtract(long left, long right)
        => left > right ? left - right : 0;

    private static string FormatRfc3339Millis(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>一条 (session, model) 的累计快照（inputTokens 已拆出缓存读写）。</summary>
    private sealed record CumulativeSnapshot(
        string SessionId,
        string Model,
        DateTimeOffset Timestamp,
        string TimestampText,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        long RequestCount,
        string DedupKey);

    /// <summary>差分后的区间用量（归属到后一条快照时间戳）。</summary>
    private sealed record IntervalUsage(
        DateTimeOffset Timestamp,
        string SessionId,
        string Model,
        string DedupKey,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens);
}
