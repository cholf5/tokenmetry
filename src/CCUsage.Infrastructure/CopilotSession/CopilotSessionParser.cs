using System.Globalization;
using System.Text.Json;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// Copilot CLI session-state 逐文件解析（I-008 主线 + I-023 积分快照）。解析语义移植自
/// ../ccusage rust/adapters/copilot（parser.rs parse_session_state_file）。蓝本许可：
/// MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE）。
/// <list type="bullet">
/// <item>只消费 type 为 session.shutdown / session.usage_checkpoint 的事件（行级预过滤与蓝本
/// LinePrefilter::any 同口径）。</item>
/// <item>shutdown 的 data.modelMetrics 是按 (session, model) 的<b>累计快照</b>：resume 后每关一次
/// 进程就多一条包含历史总和的 shutdown。本解析产出各模型的<b>累计快照条目</b>（含 requests.count 与
/// 模型侧 totalNanoAiu），相邻快照差分（区间化）由 <see cref="CopilotUsageReconciler"/> 在池化后完成
/// （蓝本 reconcile_session_state_entries 同构；I-008 时期差分在单文件解析内完成，I-023 因 otel
/// 取优合并与积分对账需跨文件池化数据而统移至合并器——(session, model) 组不跨文件的等价性论证
/// 不变，见卡片 Dev Log）。</item>
/// <item>积分快照（I-023）：checkpoint 带非负 totalNanoAiu → 一条积分快照；shutdown 恒产出积分快照，
/// 总额取 data.totalNanoAiu，但任一"参与上报"的模型（有 usage 且非全零）缺模型侧 totalNanoAiu 时
/// 总额按缺失处理（蓝本 models_report_credits 过滤，credits 无法对账）。</item>
/// <item>token 语义：Copilot 把缓存读写计入 inputTokens，uncached input = inputTokens −
/// (cacheRead + cacheWrite)（解析时先算，差分作用在 uncached 累计值上——与蓝本 subtract_usage 一致）；
/// reasoningTokens 已含在 outputTokens 内，不单列（仅进无事件 id 的 dedup key）。</item>
/// <item>内部模型后缀剥离：-1m-internal / -1m（先长后短，仅一次）在分组与定价前剥掉，
/// 使 1m 上下文变体并入同组并命中基础模型价格。</item>
/// <item>零 token 快照不出条目，但全零<b>有请求数</b>条目保留（作差分基线参与后续减法，蓝本口径）。</item>
/// </list>
/// 行级容错为仓库 I-004 口径（蓝本 records() 对反序列化失败的行静默跳过）：坏 JSON 计 MalformedLines；
/// 预过滤未命中 / 非 shutdown·checkpoint 事件 / 缺总额的 checkpoint 行计 LinesSkipped；
/// shutdown·checkpoint 行缺可解析时间戳计 InvalidRecords（DSH 适配器同口径偏差）。
/// </summary>
public sealed class CopilotSessionParser
{
    public const string AgentName = "Copilot CLI";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "GitHub Copilot CLI"）。</summary>
    public const string ProjectPath = "GitHub Copilot CLI";

    private const string ShutdownEvent = "session.shutdown";
    private const string UsageCheckpointEvent = "session.usage_checkpoint";

    /// <summary>
    /// 解析一个会话目录的 events.jsonl，产出该文件的累计快照条目与积分快照（不差分）。
    /// </summary>
    public Task<CopilotSessionFileParse> ParseAsync(TextReader reader, string sourceFile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 蓝本 parse_session_state_file：session id 取 events.jsonl 一级父目录名（trim，非空）；
        // 解析不出会话目录名时整文件不产出（无计数，与蓝本返回 default 一致）。
        var sessionId = ResolveSessionId(sourceFile);
        if (string.IsNullOrEmpty(sessionId))
        {
            return Task.FromResult(EmptyParse());
        }

        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;
        var snapshots = new List<CopilotSnapshotEntry>();
        var creditSnapshots = new List<CopilotCreditSnapshot>();

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
            var isShutdown = eventType == ShutdownEvent;
            if (!isShutdown && eventType != UsageCheckpointEvent)
            {
                linesSkipped++;
                continue;
            }

            if (!element.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || timestampElement.GetString() is not { Length: > 0 } timestampText
                || !JsonlTimestamp.TryParse(timestampText, out var timestamp))
            {
                // 时间戳缺失 / 不可解析：区间与积分快照均无法归时，丢弃（仓库口径计 invalid）。
                invalidRecords++;
                continue;
            }

            element.TryGetProperty("id", out var idElement);
            var eventId = idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrEmpty(eventId))
            {
                eventId = null;
            }

            // 蓝本 CopilotSessionStateData：data 非 JSON 对象（lenient_object 失败）按 default 处理
            // ——无模型表、无会话总额；模型表内任一模型值非对象时整表按缺失（all-or-nothing）。
            var hasData = element.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            var sessionNanoAiu = hasData ? ReadNanoAiu(data, "totalNanoAiu") : null;
            JsonElement modelMetricsElement = default;
            var hasModelMetrics = hasData
                && data.TryGetProperty("modelMetrics", out modelMetricsElement)
                && modelMetricsElement.ValueKind == JsonValueKind.Object;

            if (!isShutdown)
            {
                // 蓝本：checkpoint 缺总额则没有任何可对账的东西——不产出积分快照（其余内容跳过）。
                if (sessionNanoAiu is { } checkpointTotal)
                {
                    creditSnapshots.Add(new CopilotCreditSnapshot(
                        timestamp, sessionId, IsShutdown: false, checkpointTotal,
                        BuildCreditDedupKey(sessionId, eventId, timestamp, false, checkpointTotal),
                        sourceFile));
                }
                else
                {
                    linesSkipped++;
                }

                continue;
            }

            // ---- shutdown：模型累计快照条目 + 积分快照（蓝本 modelMetrics 循环 + models_report_credits） ----
            // 蓝本：modelMetrics 表整体缺失 / 非对象 / 任一模型值非对象（lenient_object all-or-nothing
            // 失败）→ 空表——无模型条目、models_report_credits 保持 true（空表不算"未上报"），
            // shutdown 积分总额仍取会话 totalNanoAiu。
            var modelsReportCredits = true;
            if (hasModelMetrics)
            {
                foreach (var metric in modelMetricsElement.EnumerateObject())
                {
                    if (metric.Value.ValueKind != JsonValueKind.Object)
                    {
                        hasModelMetrics = false;
                        break;
                    }
                }
            }

            if (hasModelMetrics)
            {
                CollectShutdownSnapshots(
                    modelMetricsElement, sessionId, eventId, timestamp,
                    FormatRfc3339Millis(timestamp), sourceFile, snapshots, ref modelsReportCredits);
            }

            // 模型侧未全部上报积分时，shutdown 总额不可对账（蓝本 total_nano_aiu.filter(models_report_credits)）。
            var shutdownTotal = modelsReportCredits ? sessionNanoAiu : null;
            creditSnapshots.Add(new CopilotCreditSnapshot(
                timestamp, sessionId, IsShutdown: true, shutdownTotal,
                BuildCreditDedupKey(sessionId, eventId, timestamp, true, shutdownTotal),
                sourceFile));
        }

        var counters = new CopilotFileParseCounters(FilesRead: 1, linesSkipped, malformedLines, invalidRecords);
        return Task.FromResult(new CopilotSessionFileParse(snapshots, creditSnapshots, counters));
    }

    private static CopilotSessionFileParse EmptyParse()
        => new([], [], new CopilotFileParseCounters(FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0));

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
    /// usage 缺失 / 非对象 → 跳过该模型；空模型名，或全部用量桶与请求数皆 0 的条目跳过
    /// （请求数 &gt; 0 的全零条目保留，作为差分基线参与后续减法）。参与上报（通过筛选）的模型
    /// 任一缺 totalNanoAiu 时 <paramref name="modelsReportCredits"/> 置 false。
    /// </summary>
    private static void CollectShutdownSnapshots(
        JsonElement modelMetrics,
        string sessionId,
        string? eventId,
        DateTimeOffset timestamp,
        string timestampText,
        string sourceFile,
        List<CopilotSnapshotEntry> snapshots,
        ref bool modelsReportCredits)
    {
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

            // 蓝本口径：空模型名，或全部用量桶与请求数皆 0 的条目跳过（请求数 > 0 的全零条目保留，
            // 作为差分基线参与后续减法）。
            if (model.Length == 0
                || (inputTokens == 0 && outputTokens == 0 && cacheReadTokens == 0
                    && cacheWriteTokens == 0 && reasoningTokens == 0 && requestCount == 0))
            {
                continue;
            }

            var modelNanoAiu = ReadNanoAiu(metrics, "totalNanoAiu");
            modelsReportCredits &= modelNanoAiu is not null;

            // Copilot 把缓存读写计入 inputTokens：uncached input 在解析时先拆出（saturating），
            // 差分作用在 uncached 累计值上（蓝本 uncached_session_input_tokens + subtract_usage 口径）。
            var uncachedInput = SaturatingSubtract(
                inputTokens, SaturatingAdd(cacheReadTokens, cacheWriteTokens));

            snapshots.Add(new CopilotSnapshotEntry(
                sessionId,
                model,
                timestamp,
                timestampText,
                uncachedInput,
                outputTokens,
                cacheWriteTokens,
                cacheReadTokens,
                requestCount,
                modelNanoAiu ?? 0,
                BuildDedupKey(
                    eventId, sessionId, timestampText, model,
                    inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reasoningTokens, requestCount),
                sourceFile));
        }
    }

    /// <summary>
    /// dedup key（蓝本 session_state_dedup_key）：事件带非空 id 时 "shutdown:{session}:{id}:{model}"，
    /// 否则按原始累计用量字段拼装（含 reasoningTokens，按蓝本字段序）。
    /// </summary>
    private static string BuildDedupKey(
        string? eventId,
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
        if (eventId is not null)
        {
            return $"shutdown:{sessionId}:{eventId}:{model}";
        }

        return FormattableString.Invariant(
            $"shutdown:{sessionId}:{timestampText}:{model}:{inputTokens}:{outputTokens}:{cacheReadTokens}:{cacheWriteTokens}:{reasoningTokens}:{requestCount}");
    }

    /// <summary>
    /// 积分快照 dedup key（蓝本 snapshot_key）：事件带 id 时 "credits:{session}:{id}"，
    /// 否则 "credits:{session}:{is_shutdown}:{millis}:{total:?}"（total 为 Option 的 Debug 形状）。
    /// </summary>
    private static string BuildCreditDedupKey(
        string sessionId,
        string? eventId,
        DateTimeOffset timestamp,
        bool isShutdown,
        long? total)
    {
        if (eventId is not null)
        {
            return $"credits:{sessionId}:{eventId}";
        }

        var optionShape = total is { } value ? $"Some({value})" : "None";
        return FormattableString.Invariant(
            $"credits:{sessionId}:{(isShutdown ? "true" : "false")}:{timestamp.ToUnixTimeMilliseconds()}:{optionShape}");
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

    /// <summary>
    /// totalNanoAiu（蓝本 lenient_f64 + nano_aiu）：任意 JSON 数字生效（字符串 / 缺失为 null），
    /// 非有限或负值拒绝，四舍五入（half away from zero，蓝本 f64::round）后钳到 long 域。
    /// </summary>
    private static long? ReadNanoAiu(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var raw = value.GetDouble();
        if (!double.IsFinite(raw) || raw < 0)
        {
            return null;
        }

        return (long)Math.Min(Math.Round(raw, MidpointRounding.AwayFromZero), long.MaxValue);
    }

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
}
