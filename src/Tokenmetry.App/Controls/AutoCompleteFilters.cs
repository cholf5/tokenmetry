using System;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// AutoCompleteBox 的候选过滤谓词（TextFilter 属性，类型 = AutoCompleteFilterPredicate&lt;string&gt;）。
/// 走代码侧谓词而非 FilterMode 字符串：ContainsOrdinalCaseInsensitive 档在 XamlIl 编译期不可解析
/// （AVLN2000/AVLN3000，StartsWith / ContainsOrdinal 档可解析），谓词一次性拿到完整的大小写不敏感
/// 包含语义，不受该怪癖影响；经 x:Static 暴露为静态属性（x:Static 不支持方法组）。
/// 模型聚合卡的候选框（原始名 / 规范名）共用；空搜索词返回 false（下拉不冒出全量清单）。
/// </summary>
public static class AutoCompleteFilters
{
    public static AutoCompleteFilterPredicate<string> CaseInsensitiveContains { get; } =
        (search, item) => !string.IsNullOrEmpty(search)
            && item?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
}
