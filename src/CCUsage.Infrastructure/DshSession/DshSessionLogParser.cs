using System.Globalization;
using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.DshSession;

/// <summary>
/// DSH 会话日志逐文件解析（I-007）。fold 语义逐条移植 ../dsh-usage-stats/lib/usage.js 的
/// applyUsageDelta（MIT License，Copyright (c) 2026 dsh-usage-stats contributors）：
/// <list type="bullet">
/// <item>usage 样本两种载体：assistant/chunk（data.chunk.type === "usage"，usage 在 data.chunk.usage）
/// 与 assistant/message（usage 在 data.usage），样本键为 "&lt;turn&gt;:&lt;step&gt;"；</item>
/// <item>同 (turn, step) 重复样本是替换不是累加——蓝本的 lastSample 游标只对"紧接着的上一个样本"
/// 生效（减旧加新、重归属到后样本的事件日期），本实现以"待提交样本"承载同一状态：
/// 新样本键与待提交样本相同时直接覆盖，不同时先提交旧样本再换新；</item>
/// <item>模型归因：样本事件先查自身 data.message.source.{provider,model}，缺失回退最近
/// request/header 的 data.header.config.{provider,model}（仅 request/header 推进该游标，
/// assistant/message 不推进）；无信息落 "unknown/unknown"。记录模型键为 "provider/model"
/// （同模型不同 provider 分开统计）；</item>
/// <item>全零样本（warmup 请求）静默过滤不产出记录（蓝本渲染时过滤，对聚合等价，
/// 不计 InvalidRecords 以免活跃会话被误判 Partial）。</item>
/// </list>
/// 由于每代日志重写完整历史且适配器只读最高版本，fold 状态只在单文件解析内存活即可精确对齐蓝本；
/// 跨扫描的增量由共享引擎的文件记账承担（变更文件整文件重解析）。
/// 行级容错（仓库 I-004 口径，偏离蓝本"解析失败弃全文件"）：坏 JSON 计 MalformedLines 并继续；
/// 非 JSON 对象 / 缺 type / 缺 seq 的行计 LinesSkipped。
/// 记录成本（CostUsd）在此落定：官方 deepseek 路由样本按 <see cref="DeepSeekPricingCatalog"/> 的
/// 历史规则（生效区间 + Asia/Shanghai peak/offPeak）计价；不可计价（非 deepseek 路由、模型不在目录、
/// 正桶缺单价）记 0——不置 null 是为防止 PricingEngine 泛化匹配把 ark 等路由的同名模型错配成
/// 其他 provider 的价格（快照 zai 段确有 glm-5.3-flash 条目），0 的聚合语义与"缺失"一致
/// （I-006 自定义 provider 行同口径）。
/// </summary>
public sealed class DshSessionLogParser
{
    public const string AgentName = "DSH";

    private const string UnknownRouteModel = "unknown/unknown";

    private readonly DeepSeekPricingCatalog _pricingCatalog;

    public DshSessionLogParser(DeepSeekPricingCatalog? pricingCatalog = null)
    {
        _pricingCatalog = pricingCatalog ?? DeepSeekPricingCatalog.Instance;
    }

    /// <summary>解析一个会话日志（TextReader 覆盖整份日志，zstd 已由流层逐帧解压）。</summary>
    public Task<JsonlFileParseOutcome> ParseAsync(
        TextReader reader,
        string sourceFile,
        string fallbackProjectPath,
        string sessionDirectoryName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var records = new List<UsageRecord>();
        var sessionHeaderSeen = false;
        var sessionId = sessionDirectoryName;
        var projectPath = fallbackProjectPath;
        var currentModel = default(string?);
        SampleState? pending = null;
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
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

            var type = typeElement.GetString()!;
            if (type == "session" && !sessionHeaderSeen)
            {
                sessionHeaderSeen = true;
                // 会话 id 取 session 头行 id（verify-raw 同口径，缺失回退目录名）；
                // cwd 为会话项目路径的真实来源（projectKey 目录编码有损，仅作回退）。
                if (element.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && idElement.GetString() is { Length: > 0 } headerId)
                {
                    sessionId = headerId;
                }

                if (element.TryGetProperty("cwd", out var cwdElement)
                    && cwdElement.ValueKind == JsonValueKind.String
                    && cwdElement.GetString() is { Length: > 0 } cwd)
                {
                    projectPath = cwd;
                }

                linesSkipped++;
                continue;
            }

            // 蓝本 readSessionLog 只折叠"对象 + type:string + seq:number"的事件。
            if (!element.TryGetProperty("seq", out var seqElement) || seqElement.ValueKind != JsonValueKind.Number)
            {
                linesSkipped++;
                continue;
            }

            if (type == "request/header")
            {
                if (TryRouteOf(element) is { } headerRoute)
                {
                    currentModel = $"{headerRoute.Provider}/{headerRoute.Model}";
                }

                linesSkipped++;
                continue;
            }

            if (TryExtractSample(element) is not { } sample)
            {
                linesSkipped++;
                continue;
            }

            // 同 (turn, step) 邻近重复：替换（新样本覆盖待提交样本，重归属到新样本的事件日期）。
            if (pending is { } previous && previous.Key != sample.Key)
            {
                invalidRecords += Emit(records, previous, projectPath, sessionId, sourceFile);
            }

            // 蓝本 modelOf/routeModelOf：样本自带 source 归因优先，否则回退 request/header 游标，
            // 再否则 unknown/unknown；路由与模型键分别解析（routeFromModelKey 同口径）。
            var route = sample.Route ?? RouteFromModelKey(currentModel);
            var model = sample.Route is { } sampleRoute
                ? $"{sampleRoute.Provider}/{sampleRoute.Model}"
                : currentModel ?? UnknownRouteModel;
            pending = new SampleState(
                sample.Key,
                route,
                model,
                sample.Time,
                sample.Input,
                sample.Output,
                sample.CacheRead,
                sample.CacheWrite);
        }

        if (pending is { } last)
        {
            invalidRecords += Emit(records, last, projectPath, sessionId, sourceFile);
        }

        return Task.FromResult(new JsonlFileParseOutcome(
            records,
            FilesRead: 1,
            linesSkipped,
            malformedLines,
            invalidRecords,
            Interrupted: false));
    }

    /// <summary>提交一个存活样本；全零（warmup）静默过滤，事件时间缺失不可归日计 invalid。</summary>
    private int Emit(List<UsageRecord> records, SampleState sample, string projectPath, string sessionId, string sourceFile)
    {
        var total = sample.Input + sample.Output + sample.CacheRead + sample.CacheWrite;
        if (total == 0)
        {
            return 0;
        }

        if (sample.Time is not { } timeMilliseconds)
        {
            return 1;
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMilliseconds);
        records.Add(new UsageRecord
        {
            Timestamp = timestamp,
            Agent = AgentName,
            ProjectPath = projectPath,
            SessionId = sessionId,
            Model = sample.Model,
            CostUsd = EstimateCost(sample, timestamp),
            InputTokens = sample.Input,
            OutputTokens = sample.Output,
            CacheCreationTokens = sample.CacheWrite,
            CacheCreationEphemeral5mTokens = 0,
            CacheCreationEphemeral1hTokens = 0,
            CacheReadTokens = sample.CacheRead,
            IsSidechain = false,
            MessageId = null,
            RequestId = null,
            SourceFile = sourceFile,
        });
        return 0;
    }

    /// <summary>官方 deepseek 路由按历史规则计价；不可计价记 0（防泛化匹配错配其他 provider 价格）。</summary>
    private decimal? EstimateCost(SampleState sample, DateTimeOffset timestamp)
        => _pricingCatalog.TryEstimate(
            sample.Model,
            timestamp,
            sample.Input,
            sample.Output,
            sample.CacheRead,
            sample.CacheWrite,
            out var cost)
            ? cost
            : 0m;

    /// <summary>抽取事件携带的 usage 样本（两种载体）；无样本 / 结构不符 / 缺事件时间返回 null。</summary>
    private static ExtractedSample? TryExtractSample(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var type = typeElement.GetString();
        if (type is not ("assistant/chunk" or "assistant/message"))
        {
            return null;
        }

        if (!element.TryGetProperty("time", out var timeElement)
            || timeElement.ValueKind != JsonValueKind.Number
            || !timeElement.TryGetInt64(out var time)
            || !element.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement usageElement;
        if (type == "assistant/chunk")
        {
            if (!data.TryGetProperty("chunk", out var chunk)
                || chunk.ValueKind != JsonValueKind.Object
                || !chunk.TryGetProperty("type", out var chunkType)
                || chunkType.ValueKind != JsonValueKind.String
                || chunkType.GetString() != "usage"
                || !chunk.TryGetProperty("usage", out usageElement))
            {
                return null;
            }
        }
        else if (!data.TryGetProperty("usage", out usageElement))
        {
            return null;
        }

        if (usageElement.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("turn", out var turnElement)
            || !data.TryGetProperty("step", out var stepElement))
        {
            return null;
        }

        return new ExtractedSample(
            Key: $"{ScalarKey(turnElement)}:{ScalarKey(stepElement)}",
            Time: time,
            Route: TryRouteOf(element),
            Input: ReadBucket(usageElement, "inputTokens"),
            Output: ReadBucket(usageElement, "outputTokens"),
            CacheRead: ReadBucket(usageElement, "cacheReadTokens"),
            CacheWrite: ReadBucket(usageElement, "cacheWriteTokens"));
    }

    /// <summary>(turn, step) 键片段：数字取整数文本，字符串取原文，其余取原始 JSON 文本。</summary>
    private static string ScalarKey(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : element.GetRawText(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => element.GetRawText(),
        };

    private static long ReadBucket(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var tokens)
                ? tokens
                : 0;

    /// <summary>
    /// 事件的 provider/model 归因（蓝本 routeModelOf）：先 data.message.source，后 data.header.config；
    /// model 须为 string，provider 缺省 "unknown"。
    /// </summary>
    private static ModelRoute? TryRouteOf(JsonElement element)
    {
        if (!element.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (data.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("source", out var source)
            && source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("model", out var sourceModel)
            && sourceModel.ValueKind == JsonValueKind.String)
        {
            return RouteFrom(source, sourceModel);
        }

        if (data.TryGetProperty("header", out var header)
            && header.ValueKind == JsonValueKind.Object
            && header.TryGetProperty("config", out var config)
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("model", out var configModel)
            && configModel.ValueKind == JsonValueKind.String)
        {
            return RouteFrom(config, configModel);
        }

        return null;
    }

    private static ModelRoute RouteFrom(JsonElement routeElement, JsonElement modelElement)
    {
        var provider = routeElement.TryGetProperty("provider", out var providerElement)
                       && providerElement.ValueKind == JsonValueKind.String
                       ? providerElement.GetString()
                       : null;
        return new ModelRoute(
            string.IsNullOrEmpty(provider) ? "unknown" : provider,
            modelElement.GetString()!);
    }

    /// <summary>把 "provider/model" 模型键拆回路由（蓝本 routeFromModelKey）；不合法落 unknown/unknown。</summary>
    private static ModelRoute RouteFromModelKey(string? key)
    {
        if (key is not null)
        {
            var slash = key.IndexOf('/');
            if (slash > 0 && slash < key.Length - 1)
            {
                return new ModelRoute(key[..slash], key[(slash + 1)..]);
            }
        }

        return new ModelRoute("unknown", "unknown");
    }

    private sealed record ModelRoute(string Provider, string Model);

    /// <summary>一次抽取出的 usage 样本（替换语义判定前的原始形态）。</summary>
    private sealed record ExtractedSample(
        string Key,
        long Time,
        ModelRoute? Route,
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite);

    /// <summary>待提交 / 最近样本的 fold 状态（蓝本 lastSample 游标）。</summary>
    private sealed record SampleState(
        string Key,
        ModelRoute Route,
        string Model,
        long? Time,
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite);
}
