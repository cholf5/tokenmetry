using System;
using System.Threading;
using System.Threading.Tasks;
using CCUsage.App.ViewModels;

namespace CCUsage.App.Services;

/// <summary>
/// 后台刷新动作的运行时接线（S-002 审查修复 P0）：BackgroundRefreshService 的刷新动作 =
/// DashboardViewModel 采集刷新（App 启动后的 facts 直解析线，行为不变）+ 外壳"最近更新"时间戳
/// 回写，二者在 App 启动序列才就绪，经 <see cref="Attach"/> 注入本接线对象。动作在未接线时
/// <b>如实抛错</b>——服务层捕获后以失败结果上报，绝不空转假成功。
/// <para>成败口径（S-002 审查修复 P2-1）：dashboard.RefreshAsync 内部吞异常并置 Status="error"
/// （既有页面语义，不改），接线层经其可观测状态上报本轮是否出错；"no data"（扫描完成但零记录）
/// 是成功的扫描，不算失败。</para>
/// </summary>
public sealed class BackgroundRefreshWiring
{
    private DashboardViewModel? _dashboard;
    private MainWindowViewModel? _shell;

    /// <summary>App 启动序列在 dashboard 与外壳 VM 都就绪后一次性接线（两参必须同时给出）。</summary>
    public void Attach(DashboardViewModel dashboard, MainWindowViewModel shell)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>执行一轮刷新；返回本轮是否成功（true = 采集线完成且未报错）。
    /// 整轮在<b>线程池</b>执行（A-5）：启动路径的触发点在 UI 线程（App.OnFrameworkInitializationCompleted
    /// 的 fire-and-forget），而 Infrastructure 采集链各 await 未配 ConfigureAwait(false)——捕获的
    /// UI 上下文会把 19 源扫描的续体逐段排回 UI 线程造成冷启动/重扫期间 UI 卡顿。VM 属性回写
    /// 本就在后台线程发生（定时器路径既有语义，顶栏回写经 Dispatcher marshal），此置不引入
    /// 新的线程语义。</summary>
    /// <exception cref="InvalidOperationException">未接线（App 启动序列未完成 Attach）时<b>同步</b>抛出，
    /// 由 BackgroundRefreshService 捕获转为失败结果——设置页重扫据此如实报"扫描失败"。</exception>
    public Task<bool> RunRefreshAsync()
    {
        var dashboard = _dashboard ?? throw new InvalidOperationException(
            "后台刷新未接线（App 启动序列未完成 Attach）——拒绝空转假成功");
        return Task.Run(async () =>
        {
            await dashboard.RefreshCommand.ExecuteAsync(null);
            // 外壳"最近更新"时间戳回写（D-003 既有行为；NoteDataRefreshed 内部 marshal 到 UI 线程，
            // 定时器线程上下文调用安全）。A-12：同轮回写数据源状态灯（error 红 / no data 橙）。
            _shell?.NoteDataRefreshed();
            _shell?.NoteDataSourceStatus(dashboard.Status);
            return dashboard.Status != "error";
        });
    }
}

public class BackgroundRefreshService
{
    private readonly Func<Task<bool>> _refreshAction;
    private readonly int _refreshMinutes;
    private Timer? _timer;

    // 单飞行槽（S-002 审查修复 P2-2 排队合并）：null = 空闲；非 null = 该轮是当前唯一"排队/执行中"
    // 的刷新。CAS 抢占者成为本轮执行者，后到者合并挂到同一完成点等待其结果——慢刷新（>刷新间隔）
    // 下定时器 tick 各自并入在跑的一轮，不再排队补跑成一轮轮、无界堆积。槽机制同时继续满足
    // 审查 P2 的"原子占位防双跑"要求：同一时刻至多一个执行者（此前为 Interlocked CAS 占位，
    // 后为 SemaphoreSlim 排队——排队无合并是本修复动因）。
    private Round? _queued;

    private sealed class Round
    {
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public BackgroundRefreshService(Func<Task<bool>> refreshAction, int refreshMinutes = 15)
    {
        _refreshAction = refreshAction;
        _refreshMinutes = refreshMinutes;
    }

    public void Start()
    {
        if (_timer != null) return;
        var interval = TimeSpan.FromMinutes(_refreshMinutes);
        _timer = new Timer(OnTimerTick, null, interval, interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 立即执行一次刷新（与定时器共用同一单飞槽；不重置定时器计划）。用于启动路径首刷：
    /// 定时器首跑要等一个完整间隔，启动时提前触发一次，避免顶栏一直"尚未刷新"。
    /// <para>返回值（S-002 审查修复 P2-1）：本触发所并入轮次的成败——动作正常完成且未报错为
    /// true，动作抛异常 / 上报失败为 false。既有静默语义保持：定时器 / 启动首刷路径不观察返回值
    /// （fire-and-forget），失败不打断用户；设置页重扫路径据此区分「扫描完成 / 扫描失败」toast。</para>
    /// <para>合并语义（P2-2）：已有轮次在跑时，本触发并入该轮等待其完成并取其结果，不追加新轮——
    /// 设置页重扫与定时器刷新在任意并发下至多一排在跑。等待中的触发对设置页 IsScanning 仍算
    /// "进行中"（其调用尚未返回，按钮保持禁用直到拿到并入轮的真实结果）。</para>
    /// </summary>
    public Task<bool> TriggerRefreshAsync()
    {
        return RunRefreshAsync();
    }

    private async void OnTimerTick(object? state)
    {
        await RunRefreshAsync();
    }

    private async Task<bool> RunRefreshAsync()
    {
        var round = new Round();
        var existing = Interlocked.CompareExchange(ref _queued, round, null);
        if (existing is not null)
        {
            // 已有轮次排队/执行中：合并等待，不追加新轮（防无界堆积）
            return await existing.Done.Task.ConfigureAwait(false);
        }

        try
        {
            var ok = await _refreshAction().ConfigureAwait(false);
            round.Done.TrySetResult(ok);
            return ok;
        }
        catch
        {
            // 静默失败，不打断用户（定时器/首刷路径不观察结果）；设置页路径经 false 如实区分
            round.Done.TrySetResult(false);
            return false;
        }
        finally
        {
            // 结果先发布、槽后释放：迟到的并入者拿到已完成轮的真实结果，之后的触发开启新一轮
            Interlocked.Exchange(ref _queued, null);
        }
    }
}
