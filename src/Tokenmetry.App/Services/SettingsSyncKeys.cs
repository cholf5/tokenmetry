using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// 值语义不在这里校验（主题/语言的未知值由消费处既有回落链兜住）——中继只搬运，不做业务判断。
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

                // 上限由既有服务定义（6 条）；整表替换（S7/S2）。
                settings.CustomTimePresets = presets.Take(6).ToList();
                return true;

            default:
                return false;
        }
    }
}
