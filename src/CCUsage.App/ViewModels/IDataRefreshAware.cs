using System.Threading.Tasks;

namespace CCUsage.App.ViewModels;

/// <summary>
/// 页面数据刷新感知：后台采集刷新轮次（定时器 / 启动首刷 / 顶栏手动刷新 / 设置页重扫，共用
/// 同一单飞槽）完成后，外壳回调当前页面的 <see cref="OnDataRefreshedAsync"/> 重查数据——否则
/// 页面只在导航 / 全局筛选变化时查询，facts 已更新而可见页面停留在旧数据。
/// 与 <see cref="INavigationAware"/> 相对：那个管"导航进入刷新"，本接口管"数据轮次刷新"。
/// 实现方 = 有真实数据面的页面 VM（构造即查询，LoadAsync 序号防护保证并发重查时旧响应丢弃）；
/// 占位页 / 设置页不实现（设置页数据源表由重扫路径与 OnNavigatedTo 重建）。
/// </summary>
public interface IDataRefreshAware
{
    /// <summary>后台刷新轮次完成后重查本页数据（UI 线程调用）。实现应快速触发既有加载路径。</summary>
    Task OnDataRefreshedAsync();
}
