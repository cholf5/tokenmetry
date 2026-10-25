using System;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 测试内按稳定 Id 取内置时间档（D-020）：旧 <c>TimeRangeOption.Options[N]</c> 下标定位随
/// 内置档增删而漂移（新清单插入 昨天/本月/上月），按 Id 定位才能让用例意图与档位绑定而非与顺序绑定。
/// </summary>
internal static class TestTimeRanges
{
    public static TimeRangeOption ById(string id) =>
        TimeRangeOption.FindBuiltIn(id) ?? throw new InvalidOperationException($"未知内置档 Id：{id}");

    public static TimeRangeOption Today => ById("Today");
    public static TimeRangeOption Last7 => ById("Last7Days");
    public static TimeRangeOption Last30 => ById("Last30Days");
    public static TimeRangeOption Last90 => ById("Last90Days");
    public static TimeRangeOption All => ById("All");
    public static TimeRangeOption Custom => ById("Custom");
}
