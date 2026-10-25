namespace Tokenmetry.Core.Models;

/// <summary>
/// 用户自定义相对时间范围预设（D-020）的持久化投影：Kind/Offset/Days 纯数据字段，
/// 语义解释与标签生成在 App 层（Tokenmetry.App.Services.RelativeRangeSpec）——枚举与
/// Translations 取串是 UI 概念，Core 不依赖 UI。Kind 合法值 "RollingDays" | "SingleDay" |
/// "Week" | "Month"（旧 settings.json 手改出未知值时装载处如实跳过该条目，不抛）。
/// Id 为稳定标识（"preset:&lt;guid&gt;"），AppSettings.TimeRange 的选中持久化指向它；
/// 预设被删除后指向它的选中值由解析处如实回落默认档。
/// </summary>
public class RelativeRangePresetSetting
{
    /// <summary>稳定标识，格式 "preset:&lt;guid&gt;"（生成后不变，删除即消失）。</summary>
    public string Id { get; set; } = "";

    /// <summary>词表种类（"RollingDays" / "SingleDay" / "Week" / "Month"）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>周期偏移：0 = 当前周期，-1 = 上一周期（RollingDays 恒 0）。</summary>
    public int Offset { get; set; }

    /// <summary>滚动天数（仅 RollingDays 消费，合法域 2..365；其余 Kind 忽略）。</summary>
    public int Days { get; set; } = 7;
}
