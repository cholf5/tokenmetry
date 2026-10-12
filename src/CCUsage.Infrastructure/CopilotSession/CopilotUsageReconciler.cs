using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// Copilot 多源取优合并（蓝本 ../ccusage/rust/adapters/copilot/src/loader.rs
/// reconcile_session_state_entries + credit_gap_entries + otel 行保留过滤的逐条移植，I-023）。
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE）。纯函数：输入为
/// 各文件解析产出的池化中间记录（session-state 累计快照 + 积分快照 + otel 用量行），每次扫描对
/// 当前池整体重算（蓝本 load_entries_inner 同构），无跨扫描状态可失步。
/// <list type="bullet">
/// <item><b>累计快照差分</b>：池化 → 同 dedup key 保留时间戳最新（相等取后出现者，蓝本
/// deduplicate_session_entries；组键含 session id，组不跨文件，文件内去重 == 池化去重）→
/// 按 (session, model) 分组（键 Ordinal 排序保证确定性）→ 组内 (时间戳, 池序) 排序 →
/// 首条保留、后条对前条做 saturating 减法（含 request_count 与 nanoAiu），区间归属到后一条快照。</item>
/// <item><b>otel × shutdown 取优合并</b>（蓝本 otel_replacement_start）：对每个有 otel 调用的
/// (session, model)，比较 otel 调用数与 shutdown 请求数差分——otel 对某对的调用数多于 shutdown
/// 请求数差分（进程无 shutdown 结束的调用只存在于 otel）时，从第一个有 otel 行的 shutdown 区间起
/// 由 otel 接管（该区间及其后的 shutdown 区间不产出，otel 行全部保留）；否则 otel 只保留
/// <b>最新 shutdown 之后</b>的行（otel_after = 组内最后一条快照时间戳）。任一快照缺请求数则该对
/// 不接管（蓝本口径：无法比较，区间留给 shutdown 差分）。比较覆盖全部 shutdown，与日期范围无关
/// （桌面适配器无 since/until，蓝本"取 --until 内最新可见 shutdown"退化为"取最新 shutdown"，
/// 全量出账 == 蓝本不带日期参数口径，见卡片偏差清单）。</item>
/// <item><b>积分缺口（credit gap）</b>（蓝本 credit_gap_entries）：session 级 totalNanoAiu
/// （1 AIU = $0.01，1e9 nano-AIU/AIU）覆盖全会话而 modelMetrics 只覆盖运行中进程；有 checkpoint 的
/// 会话中，每快照"会话总额 − 已归因模型积分（各 shutdown 差分后模型 nanoAiu 之和）"持续未被
/// 后续快照解释的部分成为 cost-only <see cref="UnattributedModel"/> 条目（cost = nanoAiu / 1e11，
/// decimal 精确换算，参照 Grok ticks 先例），日期取首次持续未解释的快照时间戳。豁免：无 checkpoint
/// （旧版 Copilot resume 重置双计数）、任一 shutdown 缺总额（含模型侧未全部上报积分）、或有 otel
/// 行的会话（蓝本 leaves_credit_gaps_to_otel_when_a_session_has_otel_rows）。</item>
/// </list>
/// 产出顺序与蓝本一致：session-state 区间（按 (时间戳, dedup key)）→ otel 行（文件序）→ 积分缺口，
/// 最终按时间戳稳定排序。桌面口径偏差（相对蓝本）：零 token 区间不产出（I-008 偏差②延续）、
/// request_count / reasoning 不落统一记录（偏差⑥）、extra 分量丢弃、无 since/until 过滤（偏差⑦）。
/// </summary>
public static class CopilotUsageReconciler
{
    /// <summary>Copilot 未归因积分的模型标签（蓝本 UNATTRIBUTED_MODEL）。</summary>
    public const string UnattributedModel = "unknown";

    /// <summary>1 USD = 1e11 nano-AIU（1 AIU = $0.01、1e9 nano-AIU/AIU；蓝本 cost = nano_aiu / 1e11）。</summary>
    internal const decimal NanoAiuPerUsd = 100_000_000_000m;

    /// <summary>执行一次多源合并，产出统一记录与结果集内计数。</summary>
    public static CopilotReconciliation Reconcile(
        IReadOnlyList<CopilotSessionFileParse> sessionStateFiles,
        IReadOnlyList<CopilotOtelFileParse> otelFiles)
    {
        var snapshots = sessionStateFiles.SelectMany(file => file.Snapshots).ToList();
        var creditSnapshots = sessionStateFiles.SelectMany(file => file.CreditSnapshots).ToList();
        var otelEntries = otelFiles.SelectMany(file => file.Entries).ToList();

        var otelAfter = ReconcileSessionState(snapshots, OtelCallTimestamps(otelEntries), out var intervals, out var attributedNanoAiu);
        var otelSessions = new HashSet<string>(otelEntries.Select(entry => entry.SessionId), StringComparer.Ordinal);
        var gaps = CreditGapEntries(creditSnapshots, otelSessions, attributedNanoAiu);

        // otel 行保留过滤（蓝本 otel_entries.retain）：otel_after 缺省（该对无 shutdown）全保留，
        // 否则仅保留严格晚于 otel_after 的行。
        var retainedOtel = otelEntries
            .Where(entry => !otelAfter.TryGetValue((entry.SessionId, entry.Model), out var after)
                            || entry.Timestamp.ToUnixTimeMilliseconds() > after)
            .ToList();

        var records = new List<UsageRecord>(intervals.Count + retainedOtel.Count + gaps.Count);
        records.AddRange(intervals.Select(ToIntervalRecord));
        records.AddRange(retainedOtel.Select(ToOtelRecord));
        records.AddRange(gaps.Select(ToCreditGapRecord));
        // 稳定排序（LINQ OrderBy 稳定）：同时间戳保持"区间 → otel → 缺口"的蓝本拼接顺序。
        records = records.OrderBy(record => record.Timestamp).ToList();

        return new CopilotReconciliation(
            records,
            MissingCostRecords: records.Count(record => record.CostUsd is null),
            UnknownModelRecords: records.Count(record => record.Model is null),
            DuplicateRecords: 0);
    }

    /// <summary>
    /// 累计快照差分 + otel 接管判定（蓝本 reconcile_session_state_entries 主循环）；
    /// 返回各 (session, model) 的 otel_after（毫秒），并经 <paramref name="intervals"/> 产出
    /// shutdown 区间（已按 (时间戳, dedup key) 排序）。
    /// </summary>
    private static Dictionary<(string SessionId, string Model), long> ReconcileSessionState(
        List<CopilotSnapshotEntry> snapshots,
        Dictionary<(string SessionId, string Model), List<long>> otelCalls,
        out List<CopilotSnapshotEntry> intervals,
        out Dictionary<(string SessionId, long TimestampMillis), long> attributedNanoAiu)
    {
        // 蓝本 deduplicate_session_entries：同 key 保留时间戳最新（相等取后出现者），保持池序。
        var keptIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (!keptIndexes.TryGetValue(snapshots[index].DedupKey, out var previous)
                || snapshots[previous].Timestamp <= snapshots[index].Timestamp)
            {
                keptIndexes[snapshots[index].DedupKey] = index;
            }
        }

        var grouped = keptIndexes.Values
            .OrderBy(index => index)
            .GroupBy(index => (snapshots[index].SessionId, snapshots[index].Model))
            .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Item2, StringComparer.Ordinal);

        var otelAfter = new Dictionary<(string SessionId, string Model), long>();
        var attributed = new Dictionary<(string SessionId, long TimestampMillis), long>();
        var intervalList = new List<CopilotSnapshotEntry>();

        foreach (var group in grouped)
        {
            var pair = group.Key;
            var sorted = group
                .OrderBy(index => snapshots[index].Timestamp)
                .ThenBy(index => index)
                .Select(index => snapshots[index])
                .ToList();

            // 桌面无 --until：蓝本 latest_visible（"取 until 内最新可见 shutdown"）退化为最后一条快照。
            var latestVisiblePosition = sorted.Count - 1;
            var otelStart = otelCalls.TryGetValue(pair, out var calls)
                ? OtelReplacementStart(sorted, calls)
                : null;
            int? otelAfterPosition = otelStart is { } start
                ? (start > 0 ? start - 1 : null)
                : latestVisiblePosition;
            if (otelAfterPosition is { } position)
            {
                otelAfter[pair] = sorted[position].Timestamp.ToUnixTimeMilliseconds();
            }

            CopilotSnapshotEntry? baseline = null;
            for (var currentPosition = 0; currentPosition < sorted.Count; currentPosition++)
            {
                var current = sorted[currentPosition];
                var reconciled = baseline is null
                    ? current
                    : SubtractUsage(current, baseline);
                baseline = current;

                // 已归因积分按 (session, shutdown 毫秒) 累加差分后模型积分——在接管/产出筛选之前
                // 完成（蓝本同序：全部 shutdown 都参与积分归因）。
                var attributedKey = (pair.SessionId, current.Timestamp.ToUnixTimeMilliseconds());
                attributed.TryGetValue(attributedKey, out var attributedValue);
                attributed[attributedKey] = attributedValue + reconciled.NanoAiu;

                if (otelStart is { } takeover && currentPosition >= takeover)
                {
                    continue;
                }

                if (HasUsage(reconciled))
                {
                    intervalList.Add(reconciled);
                }
            }
        }

        intervals = intervalList
            .OrderBy(interval => interval.Timestamp)
            .ThenBy(interval => interval.DedupKey, StringComparer.Ordinal)
            .ToList();
        attributedNanoAiu = attributed;
        return otelAfter;
    }

    /// <summary>
    /// otel 接管起点（蓝本 otel_replacement_start）：Copilot 进程恢复的是会话上次 shutdown 的分解，
    /// 无 shutdown 结束的进程（被杀 / 崩溃 / 应用重启）其调用缺失于其后所有 shutdown，而 otel 仍逐调用
    /// 有行。从第一个有 otel 行的 shutdown 区间起，当 otel 调用数多于 shutdown 请求数差分时由 otel
    /// 接管，返回该区间在 <paramref name="sorted"/> 中的位置。任一快照缺请求数（无法比较）返回 null，
    /// 区间留给 shutdown 差分。比较覆盖全部 shutdown，与日期范围无关。
    /// </summary>
    private static int? OtelReplacementStart(List<CopilotSnapshotEntry> sorted, List<long> calls)
    {
        if (sorted.Any(entry => entry.RequestCount == 0))
        {
            return null;
        }

        int? start = null;
        long countedCalls = 0;
        long otelCalls = 0;
        long reportedCalls = 0;
        long previousRequests = 0;
        for (var position = 0; position < sorted.Count; position++)
        {
            var entry = sorted[position];
            // 蓝本 partition_point：时间戳 ≤ 本 shutdown 的 otel 调用数（毫秒域比较）。
            var callsThroughShutdown = CountCallsAtOrBefore(calls, entry.Timestamp.ToUnixTimeMilliseconds());
            var intervalCalls = callsThroughShutdown - countedCalls;
            countedCalls = callsThroughShutdown;
            if (start is null && intervalCalls > 0)
            {
                start = position;
            }

            if (start is not null)
            {
                otelCalls += intervalCalls;
                reportedCalls += SaturatingSubtract(entry.RequestCount, previousRequests);
            }

            previousRequests = entry.RequestCount;
        }

        return start is { } takeover && otelCalls > reportedCalls ? takeover : null;
    }

    private static long CountCallsAtOrBefore(List<long> sortedCalls, long timestampMillis)
    {
        // 二分：第一个 > timestamp 的下标即 ≤ timestamp 的个数（蓝本 partition_point 同构）。
        var low = 0;
        var high = sortedCalls.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (sortedCalls[middle] <= timestampMillis)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>otel 行时间戳按 (session, model) 池化并升序排序（蓝本 otel_call_timestamps）。</summary>
    private static Dictionary<(string SessionId, string Model), List<long>> OtelCallTimestamps(
        List<CopilotOtelEntry> entries)
    {
        var calls = new Dictionary<(string, string), List<long>>();
        foreach (var entry in entries)
        {
            var key = (entry.SessionId, entry.Model);
            if (!calls.TryGetValue(key, out var timestamps))
            {
                timestamps = [];
                calls[key] = timestamps;
            }

            timestamps.Add(entry.Timestamp.ToUnixTimeMilliseconds());
        }

        foreach (var timestamps in calls.Values)
        {
            timestamps.Sort();
        }

        return calls;
    }

    /// <summary>
    /// 积分缺口（蓝本 credit_gap_entries）：有 checkpoint 的会话中，每快照"会话总额 − 已归因模型
    /// 积分"持续未被后续快照解释的部分成为 cost-only unknown 条目。无 checkpoint（旧版 resume
    /// 重置双计数）、任一 shutdown 缺总额、或有 otel 行的会话不产出。
    /// </summary>
    private static List<(CopilotCreditSnapshot Snapshot, long NanoAiu)> CreditGapEntries(
        List<CopilotCreditSnapshot> creditSnapshots,
        HashSet<string> skippedSessions,
        Dictionary<(string SessionId, long TimestampMillis), long> attributedNanoAiu)
    {
        // 蓝本：积分快照按 dedup key first-wins 去重（同 id 重复事件只留首个），再按会话分组
        // （BTreeMap → 会话 id Ordinal 排序）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new SortedDictionary<string, List<CopilotCreditSnapshot>>(StringComparer.Ordinal);
        foreach (var snapshot in creditSnapshots)
        {
            if (seen.Add(snapshot.DedupKey))
            {
                if (!sessions.TryGetValue(snapshot.SessionId, out var list))
                {
                    list = [];
                    sessions[snapshot.SessionId] = list;
                }

                list.Add(snapshot);
            }
        }

        var gaps = new List<(CopilotCreditSnapshot, long)>();
        foreach (var (sessionId, sessionSnapshots) in sessions)
        {
            if (skippedSessions.Contains(sessionId)
                || sessionSnapshots.All(snapshot => snapshot.IsShutdown)
                || sessionSnapshots.Any(snapshot => snapshot.IsShutdown && snapshot.TotalNanoAiu is null))
            {
                continue;
            }

            // 蓝本 sort_by_key((timestamp, is_shutdown))：同刻 checkpoint（false）先于 shutdown（true）。
            var ordered = sessionSnapshots
                .OrderBy(snapshot => snapshot.Timestamp)
                .ThenBy(snapshot => snapshot.IsShutdown)
                .ToList();
            var lastShutdownPosition = -1;
            for (var index = 0; index < ordered.Count; index++)
            {
                if (ordered[index].IsShutdown)
                {
                    lastShutdownPosition = index;
                }
            }

            // last shutdown 之前的 checkpoint 已包含在该 shutdown 的总额里，不参与缺口推导。
            long sessionTotal = 0;
            long attributed = 0;
            var points = new List<(CopilotCreditSnapshot Snapshot, long Unexplained)>();
            for (var position = 0; position < ordered.Count; position++)
            {
                var snapshot = ordered[position];
                if (!snapshot.IsShutdown && lastShutdownPosition >= 0 && position < lastShutdownPosition)
                {
                    continue;
                }

                sessionTotal = Math.Max(sessionTotal, snapshot.TotalNanoAiu ?? 0);
                if (snapshot.IsShutdown)
                {
                    attributed += attributedNanoAiu.GetValueOrDefault(
                        (sessionId, snapshot.Timestamp.ToUnixTimeMilliseconds()));
                }

                points.Add((snapshot, SaturatingSubtract(sessionTotal, attributed)));
            }

            // "持续未被解释"：取每个快照及其后所有快照未解释量的最小值（后缀最小，蓝本 lasting 回扫）。
            long lasting = long.MaxValue;
            for (var index = points.Count - 1; index >= 0; index--)
            {
                lasting = Math.Min(lasting, points[index].Unexplained);
                points[index] = (points[index].Snapshot, lasting);
            }

            long reported = 0;
            foreach (var (snapshot, lastingUnexplained) in points)
            {
                var gap = SaturatingSubtract(lastingUnexplained, reported);
                reported = Math.Max(reported, lastingUnexplained);
                if (gap == 0)
                {
                    continue;
                }

                gaps.Add((snapshot, gap));
            }
        }

        return gaps;
    }

    // ---- 区间差分与筛选（蓝本 subtract_usage / has_usage） ----

    private static CopilotSnapshotEntry SubtractUsage(CopilotSnapshotEntry current, CopilotSnapshotEntry baseline)
        => current with
        {
            InputTokens = SaturatingSubtract(current.InputTokens, baseline.InputTokens),
            OutputTokens = SaturatingSubtract(current.OutputTokens, baseline.OutputTokens),
            CacheCreationTokens = SaturatingSubtract(current.CacheCreationTokens, baseline.CacheCreationTokens),
            CacheReadTokens = SaturatingSubtract(current.CacheReadTokens, baseline.CacheReadTokens),
            RequestCount = SaturatingSubtract(current.RequestCount, baseline.RequestCount),
            NanoAiu = SaturatingSubtract(current.NanoAiu, baseline.NanoAiu),
        };

    /// <summary>
    /// 桌面口径（I-008 偏差②）：统一记录无请求数字段，零 token 区间不出记录；蓝本 has_usage 还接受
    /// request_count &gt; 0 的零 token 区间（CLI messageCount 展示用）。
    /// </summary>
    private static bool HasUsage(CopilotSnapshotEntry entry)
        => entry.InputTokens > 0
           || entry.OutputTokens > 0
           || entry.CacheCreationTokens > 0
           || entry.CacheReadTokens > 0;

    // ---- 统一记录落位 ----

    private static UsageRecord ToIntervalRecord(CopilotSnapshotEntry interval)
        => new()
        {
            Timestamp = interval.Timestamp,
            Agent = CopilotSessionParser.AgentName,
            ProjectPath = CopilotSessionParser.ProjectPath,
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
            SourceFile = interval.SourceFile,
        };

    private static UsageRecord ToOtelRecord(CopilotOtelEntry entry)
        => new()
        {
            Timestamp = entry.Timestamp,
            Agent = CopilotSessionParser.AgentName,
            ProjectPath = CopilotSessionParser.ProjectPath,
            SessionId = entry.SessionId,
            Model = entry.Model,
            CostUsd = null,
            InputTokens = entry.InputTokens,
            OutputTokens = entry.OutputTokens,
            CacheCreationTokens = entry.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = entry.CacheReadTokens,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = entry.SourceFile,
        };

    private static UsageRecord ToCreditGapRecord((CopilotCreditSnapshot Snapshot, long NanoAiu) gap)
        => new()
        {
            Timestamp = gap.Snapshot.Timestamp,
            Agent = CopilotSessionParser.AgentName,
            ProjectPath = CopilotSessionParser.ProjectPath,
            SessionId = gap.Snapshot.SessionId,
            Model = UnattributedModel,
            CostUsd = gap.NanoAiu / NanoAiuPerUsd,
            InputTokens = 0,
            OutputTokens = 0,
            CacheCreationTokens = 0,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = 0,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = gap.Snapshot.SourceFile,
        };

    // 防御性饱和运算：操作数均为非负计数（ReadTokens / NumberValue 口径），负数分支不可达，
    // long.MaxValue 仅溢出兜底。
    private static long SaturatingSubtract(long left, long right)
        => left > right ? left - right : 0;
}

/// <summary>一次多源合并的产出：统一记录 + 结果集内诊断计数（口径与共享扫描诊断一致）。</summary>
public sealed record CopilotReconciliation(
    IReadOnlyList<UsageRecord> Records,
    int MissingCostRecords,
    int UnknownModelRecords,
    int DuplicateRecords);
