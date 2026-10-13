namespace CCUsage.App.ViewModels;

/// <summary>
/// 页面导航感知（S-002）：外壳每次导航到某页后调用其 <see cref="OnNavigatedTo"/>。
/// 用于 DI 单例生命周期、不随导航重建的页面（设置页）——重进时刷新数据面（数据源表），
/// 等价于每次新建页面的"构造时查询"语义；随导航新建的页面无需实现（构造即查询）。
/// </summary>
public interface INavigationAware
{
    /// <summary>导航进入本页后触发（UI 线程）。实现应快速返回，重活自行转后台。</summary>
    void OnNavigatedTo();
}
