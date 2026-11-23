using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.App.Services;

/// <summary>
/// 应用设置同步的 key 表（settings-sync 设计 §3，唯一出处）：哪些字段过线、如何编码。
/// 纯偏好才进表——设备级设置（自启/关窗/刷新/日志/调试/同步凭据）与计价语义（模型目录/别名/价格）
/// 永不在这里出现。value 统一 TEXT：字符串原样、页边距 invariant 数字、预设清单 JSON 数组。
/// 未知 key 两端都忽略（前向兼容，不做迁移）。
/// </summary>
public static class SettingsSyncKeys
{
    public const string Theme = "theme";
    public const string ThemePalette = "themePalette";
    public const string Language = "language";
    public const string SharePosterLayout = "sharePosterLayout";
    public const string SharePosterTheme = "sharePosterTheme";
    public const string SharePosterProjectMask = "sharePosterProjectMask";
    public const string SharePosterStyle = "sharePosterStyle";
    public const string SharePosterMargin = "sharePosterMargin";
    public const string CustomTimePresets = "customTimePresets";

    /// <summary>过线键全集（顺序稳定，便于测试与日志）。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Theme,
        ThemePalette,
        Language,
        SharePosterLayout,
        SharePosterTheme,
        SharePosterProjectMask,
        SharePosterStyle,
        SharePosterMargin,
        CustomTimePresets,
    ];

    /// <summary>读当前值（null = 该字段本次无值/不可编码，跳过）。</summary>
    public static string? Read(AppSettings settings, string key) => key switch
    {
        Theme => settings.Theme,
        ThemePalette => settings.ThemePalette,
        Language => settings.Language,
        SharePosterLayout => settings.SharePosterLayout,
        SharePosterTheme => settings.SharePosterTheme,
        SharePosterProjectMask => settings.SharePosterProjectMask,
        SharePosterStyle => settings.SharePosterStyle,
        SharePosterMargin => settings.SharePosterMargin.ToString("R", CultureInfo.InvariantCulture),
        CustomTimePresets => JsonSerializer.Serialize(
            settings.CustomTimePresets ?? new List<RelativeRangePresetSetting>(),
            SyncJsonContext.Default.ListRelativeRangePresetSetting),
        _ => null,
    };

    /// <summary>
    /// 写回一个远端值。编码坏了（非法数字/坏 JSON）如实拒绝、保留本机值；未知 key 返回 false。
    /// 主题/语言等字符串档的未知值仍交给消费处既有回落链；预设清单是唯一的复合值，远端是不可信输入，
    /// 必须按与添加预设相同的封闭词表逐条过滤——否则 null 元素/越界天数会在启动装载时抛 NRE
    /// 或放大物化窗口（审查 #1）。
    /// </summary>
    public static bool TryWrite(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case Theme:
            case ThemePalette:
            case Language:
            case SharePosterLayout:
            case SharePosterTheme:
            case SharePosterProjectMask:
            case SharePosterStyle:
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                switch (key)
                {
                    case Theme: settings.Theme = value; break;
                    case ThemePalette: settings.ThemePalette = value; break;
                    case Language: settings.Language = value; break;
                    case SharePosterLayout: settings.SharePosterLayout = value; break;
                    case SharePosterTheme: settings.SharePosterTheme = value; break;
                    case SharePosterProjectMask: settings.SharePosterProjectMask = value; break;
                    case SharePosterStyle: settings.SharePosterStyle = value; break;
                }

                return true;

            case SharePosterMargin:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin)
                    || double.IsNaN(margin)
                    || double.IsInfinity(margin))
                {
                    return false;
                }

                settings.SharePosterMargin = Math.Clamp(
                    margin, SharePosterRequest.MinMargin, SharePosterRequest.MaxMargin);
                return true;

            case CustomTimePresets:
                List<RelativeRangePresetSetting>? presets;
                try
                {
                    presets = JsonSerializer.Deserialize(
                        value, SyncJsonContext.Default.ListRelativeRangePresetSetting);
                }
                catch (JsonException)
                {
                    return false;
                }

                if (presets is null)
                {
                    return false;
                }

                // 与 TimeRangePresetService.TryAdd 同一封闭词表：Id 前缀 + 已知 Kind + 域内 + 去重 + 上限。
                // 坏条目逐条丢弃（中继只搬运，但消费处（启动装载）不做防御）；整值一条都站不住时
                // 如实拒绝，不拿空清单覆盖本机预设。
                var accepted = new List<RelativeRangePresetSetting>();
                var seen = new HashSet<(RelativeRangeKind Kind, int Offset, int Days)>();
                foreach (var preset in presets)
                {
                    if (preset is null
                        || string.IsNullOrEmpty(preset.Id)
                        || !preset.Id.StartsWith(TimeRangeOption.PresetPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var kind = preset.Kind switch
                    {
                        nameof(RelativeRangeKind.RollingDays) => RelativeRangeKind.RollingDays,
                        nameof(RelativeRangeKind.SingleDay) => RelativeRangeKind.SingleDay,
                        nameof(RelativeRangeKind.Week) => RelativeRangeKind.Week,
                        nameof(RelativeRangeKind.Month) => RelativeRangeKind.Month,
                        _ => (RelativeRangeKind?)null,
                    };
                    if (kind is not { } parsed)
                    {
                        continue;
                    }

                    var spec = new RelativeRangeSpec(parsed, preset.Offset, preset.Days);
                    if (!spec.IsValid || !seen.Add(spec.DedupKey))
                    {
                        continue;
                    }

                    accepted.Add(new RelativeRangePresetSetting
                    {
                        Id = preset.Id,
                        Kind = parsed.ToString(),
                        Offset = spec.Offset,
                        Days = spec.Days,
                    });
                    if (accepted.Count == TimeRangePresetService.MaxPresets)
                    {
                        break;
                    }
                }

                if (accepted.Count == 0 && presets.Count > 0)
                {
                    return false;
                }

                settings.CustomTimePresets = accepted;
                return true;

            default:
                return false;
        }
    }
}
