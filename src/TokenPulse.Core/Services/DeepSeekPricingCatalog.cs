using System.Globalization;
using System.Text.Json;

namespace TokenPulse.Core.Services;

/// <summary>一条 DeepSeek 规则的 USD 单价（USD / 百万 tokens）；null = 无可靠单价（fail-closed）。</summary>
public sealed class DeepSeekUnitPrices
{
    public decimal? Input { get; init; }

    public decimal? Output { get; init; }

    public decimal? CacheRead { get; init; }

    /// <summary>内置目录的 flat 与 time-band 规则均未提供 cacheWrite 单价（蓝本 unitPrices 默认 null）：
    /// cacheWrite 桶有 token 时整体 fail-closed。</summary>
    public decimal? CacheWrite { get; init; }
}

/// <summary>DeepSeek 高峰时段表：时区内的星期集合 + 半开区间 [start, end) 分钟窗，其余为 offPeak。</summary>
public sealed class DeepSeekSchedule
{
    public required string TimezoneId { get; init; }

    /// <summary>0=Sunday … 6=Saturday（时区内判定，与蓝本 weekday 语义一致）。</summary>
    public required IReadOnlyList<int> PeakDays { get; init; }

    /// <summary>高峰分钟窗（当日内半开区间，如 09:00–12:00 → 540–720）。</summary>
    public required IReadOnlyList<(int StartMinute, int EndMinute)> PeakWindows { get; init; }

    /// <summary>解析时区（IANA id）；解析失败返回 null（规则整体不可用，fail-closed）。</summary>
    public TimeZoneInfo? ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // 少数平台缺 IANA 数据库时按固定偏移兜底：Asia/Shanghai 自 1991 年起为固定 UTC+8。
            return string.Equals(TimezoneId, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase)
                ? TimeZoneInfo.CreateCustomTimeZone("Asia/Shanghai", TimeSpan.FromHours(8), "Asia/Shanghai", "Asia/Shanghai")
                : null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>一条历史定价规则：精确模型 + 生效区间 [EffectiveFrom, EffectiveTo) + flat 或 peak/offPeak 单价。</summary>
public sealed class DeepSeekPricingRule
{
    public required string Id { get; init; }

    public required string Model { get; init; }

    public DateTimeOffset? EffectiveFrom { get; init; }

    public DateTimeOffset? EffectiveTo { get; init; }

    public DeepSeekUnitPrices? Flat { get; init; }

    public DeepSeekUnitPrices? Peak { get; init; }

    public DeepSeekUnitPrices? OffPeak { get; init; }

    public DeepSeekSchedule? Schedule { get; init; }
}

/// <summary>
/// DeepSeek 官方 USD 定价目录（I-007）：数据与时段规则移植自 ../dsh-usage-stats/lib/pricing.js
/// （MIT License，Copyright (c) 2026 dsh-usage-stats contributors），存放于内置价格快照
/// model-pricing-snapshot.json 的 "deepseek" 段（pricingRules 数组；该段无 models 键，
/// <see cref="ModelsDevPricingParser"/> 不会把它解析成 flat 条目）。
/// 语义对齐蓝本 estimateTokenCost：按样本事件时间匹配历史规则（绝不"当前价 × 累计 token"）、
/// Asia/Shanghai 高峰/平峰时段、四桶分价、任一正桶无可靠单价时整体 fail-closed（返回 false）。
/// 模型键口径为 DSH 的 "provider/model"：provider 限定 deepseek / deepseek-official（官方计费路由，
/// 蓝本 isOfficialDeepSeekIdentity 的 canonical-id 分支；按 baseURL 识别的官方路由在统一记录中不可表达）。
/// </summary>
public sealed class DeepSeekPricingCatalog
{
    /// <summary>官方计费路由 id（蓝本 isOfficialDeepSeekIdentity 的 canonical 路由）。</summary>
    private static readonly HashSet<string> OfficialRouteIds = new(StringComparer.Ordinal) { "deepseek", "deepseek-official" };

    private const decimal TokensPerMillion = 1_000_000m;

    private readonly Lazy<IReadOnlyList<DeepSeekPricingRule>> _rules;

    public DeepSeekPricingCatalog(Func<IReadOnlyList<DeepSeekPricingRule>>? ruleLoader = null)
    {
        _rules = new Lazy<IReadOnlyList<DeepSeekPricingRule>>(ruleLoader ?? LoadRulesFromSnapshot);
    }

    /// <summary>内置目录（快照 "deepseek" 段）；加载失败为空目录（全部请求 fail-closed）。</summary>
    public static DeepSeekPricingCatalog Instance { get; } = new();

    public IReadOnlyList<DeepSeekPricingRule> Rules => _rules.Value;

    /// <summary>
    /// 估算一条样本的成本（USD）。模型键 "provider/model"（provider 限官方 deepseek 路由），
    /// 时间戳为样本事件时间；无覆盖规则、规则歧义、时段不可判定或正桶缺单价时返回 false（fail-closed）。
    /// </summary>
    public bool TryEstimate(
        string? model,
        DateTimeOffset timestamp,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        out decimal costUsd)
    {
        costUsd = 0m;
        if (model is null || TrySplitRoute(model, out var routeModel) == false)
        {
            return false;
        }

        DeepSeekPricingRule? matched = null;
        foreach (var rule in _rules.Value)
        {
            if (!string.Equals(rule.Model, routeModel, StringComparison.Ordinal)
                || (rule.EffectiveFrom is { } from && timestamp < from)
                || (rule.EffectiveTo is { } to && timestamp >= to))
            {
                continue;
            }

            if (matched is not null)
            {
                // 生效区间重叠 = 目录错误（蓝本直接抛错）；此处 fail-closed。
                return false;
            }

            matched = rule;
        }

        if (matched is null)
        {
            return false;
        }

        DeepSeekUnitPrices? prices;
        if (matched.Schedule is { } schedule)
        {
            var timeZone = schedule.ResolveTimeZone();
            if (timeZone is null)
            {
                return false;
            }

            prices = ResolveTariff(matched, schedule, timeZone, timestamp);
        }
        else
        {
            prices = matched.Flat;
        }

        if (prices is null)
        {
            return false;
        }

        decimal total = 0m;
        if (!TryComponent(inputTokens, prices.Input, ref total)
            || !TryComponent(outputTokens, prices.Output, ref total)
            || !TryComponent(cacheReadTokens, prices.CacheRead, ref total)
            || !TryComponent(cacheWriteTokens, prices.CacheWrite, ref total))
        {
            return false;
        }

        costUsd = total;
        return true;
    }

    /// <summary>拆出 "provider/model" 的 model 部分；provider 须为官方 deepseek 路由。</summary>
    private static bool TrySplitRoute(string model, out string routeModel)
    {
        routeModel = string.Empty;
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1)
        {
            return false;
        }

        var provider = model[..separator];
        if (!OfficialRouteIds.Contains(provider))
        {
            return false;
        }

        routeModel = model[(separator + 1)..];
        return routeModel.Length > 0;
    }

    private static DeepSeekUnitPrices? ResolveTariff(
        DeepSeekPricingRule rule,
        DeepSeekSchedule schedule,
        TimeZoneInfo timeZone,
        DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp.ToUniversalTime(), timeZone);
        // DayOfWeek Sunday=0 … Saturday=6，与蓝本 WEEKDAY_NUMBER 一致。
        var weekday = (int)local.DayOfWeek;
        var minute = local.Hour * 60 + local.Minute;
        var isPeak = schedule.PeakDays.Contains(weekday)
                     && schedule.PeakWindows.Any(window => minute >= window.StartMinute && minute < window.EndMinute);
        // 高峰价缺失按 fail-closed 处理（返回 null，由调用方整体放弃计价）。
        return isPeak ? rule.Peak : rule.OffPeak;
    }

    private static bool TryComponent(long tokens, decimal? pricePerMillion, ref decimal total)
    {
        if (tokens == 0)
        {
            return true;
        }

        if (pricePerMillion is not { } price)
        {
            return false;
        }

        total += tokens * price / TokensPerMillion;
        return true;
    }

    /// <summary>从内置快照的 "deepseek" 段加载规则；任何解析问题跳过对应规则（快照损坏不致崩溃）。</summary>
    private static IReadOnlyList<DeepSeekPricingRule> LoadRulesFromSnapshot()
    {
        var rules = new List<DeepSeekPricingRule>();
        try
        {
            if (BuiltinPricingSnapshot.LoadSnapshotJson() is not { } json)
            {
                return rules;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("deepseek", out var section)
                || section.ValueKind != JsonValueKind.Object
                || !section.TryGetProperty("pricingRules", out var ruleArray)
                || ruleArray.ValueKind != JsonValueKind.Array)
            {
                return rules;
            }

            foreach (var element in ruleArray.EnumerateArray())
            {
                if (TryParseRule(element) is { } rule)
                {
                    rules.Add(rule);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // 快照损坏 / deepseek 段形状不符：返回已解析部分（可能为空），全部请求 fail-closed。
        }

        return rules;
    }

    private static DeepSeekPricingRule? TryParseRule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(element, "id");
        var model = ReadString(element, "model");
        if (id is null || model is null)
        {
            return null;
        }

        DeepSeekUnitPrices? flat = null;
        DeepSeekUnitPrices? peak = null;
        DeepSeekUnitPrices? offPeak = null;
        DeepSeekSchedule? schedule = null;
        if (element.TryGetProperty("prices", out var flatElement))
        {
            flat = ParseUnitPrices(flatElement);
        }

        if (element.TryGetProperty("peak", out var peakElement))
        {
            peak = ParseUnitPrices(peakElement);
        }

        if (element.TryGetProperty("offPeak", out var offPeakElement))
        {
            offPeak = ParseUnitPrices(offPeakElement);
        }

        if (element.TryGetProperty("schedule", out var scheduleElement)
            && scheduleElement.ValueKind == JsonValueKind.Object)
        {
            schedule = ParseSchedule(scheduleElement);
            if (schedule is null && (peak is not null || offPeak is not null))
            {
                // 时段表不可解析的 time-band 规则整体不可用，避免误按 flat/缺省价计价。
                return null;
            }
        }

        return new DeepSeekPricingRule
        {
            Id = id,
            Model = model,
            EffectiveFrom = ParseTimestamp(ReadString(element, "effectiveFrom")),
            EffectiveTo = ParseTimestamp(ReadString(element, "effectiveTo")),
            Flat = flat,
            Peak = peak,
            OffPeak = offPeak,
            Schedule = schedule,
        };
    }

    private static DeepSeekUnitPrices? ParseUnitPrices(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new DeepSeekUnitPrices
        {
            Input = ReadPrice(element, "input"),
            Output = ReadPrice(element, "output"),
            CacheRead = ReadPrice(element, "cacheRead"),
            CacheWrite = ReadPrice(element, "cacheWrite"),
        };
    }

    private static decimal? ReadPrice(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!value.TryGetDecimal(out var price) || price < 0)
        {
            return null;
        }

        return price;
    }

    private static DeepSeekSchedule? ParseSchedule(JsonElement element)
    {
        var timezone = ReadString(element, "timezone");
        if (timezone is null)
        {
            return null;
        }

        var peakDays = new List<int>();
        if (element.TryGetProperty("peakDays", out var days) && days.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in days.EnumerateArray())
            {
                if (day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var weekday)
                    && weekday is >= 0 and <= 6)
                {
                    peakDays.Add(weekday);
                }
            }
        }

        var windows = new List<(int StartMinute, int EndMinute)>();
        if (element.TryGetProperty("peakWindows", out var windowArray) && windowArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var window in windowArray.EnumerateArray())
            {
                if (window.ValueKind != JsonValueKind.Array || window.GetArrayLength() != 2)
                {
                    continue;
                }

                var start = ParseClock(window[0]);
                var end = ParseClock(window[1]);
                if (start is { } startMinute && end is { } endMinute && startMinute < endMinute)
                {
                    windows.Add((startMinute, endMinute));
                }
            }
        }

        if (peakDays.Count == 0 || windows.Count == 0)
        {
            return null;
        }

        return new DeepSeekSchedule
        {
            TimezoneId = timezone,
            PeakDays = peakDays,
            PeakWindows = windows,
        };
    }

    private static int? ParseClock(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var clock = element.GetString();
        if (clock is null
            || clock.Length != 5
            || clock[2] != ':'
            || !int.TryParse(clock.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(clock.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        if (hour > 23 || minute > 59)
        {
            return null;
        }

        return hour * 60 + minute;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
        => value is null ? null : DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
