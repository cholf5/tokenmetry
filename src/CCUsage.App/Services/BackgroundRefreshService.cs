using System;
using System.Threading;
using System.Threading.Tasks;

namespace CCUsage.App.Services;

public class BackgroundRefreshService
{
    private readonly Func<Task> _refreshAction;
    private readonly int _refreshMinutes;
    private Timer? _timer;

    // 0 = 空闲，1 = 刷新中。check-and-set 必须 Interlocked 原子化（审查 P2）：定时器回调与
    // 启动首刷（TriggerRefreshAsync）是两个并发入口，bool 的 check-then-set 在同时进入时可双跑。
    private int _isRunning;

    public BackgroundRefreshService(Func<Task> refreshAction, int refreshMinutes = 15)
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
    /// 立即执行一次刷新（与定时器共用互斥与静默异常语义；不重置定时器计划）。
    /// 用于启动路径首刷：定时器首跑要等一个完整间隔，启动时提前触发一次，避免顶栏一直"尚未刷新"。
    /// </summary>
    public Task TriggerRefreshAsync()
    {
        return RunRefreshAsync();
    }

    private async void OnTimerTick(object? state)
    {
        await RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        // CompareExchange 原子占位：并发入口（定时器 + 启动首刷 / 手动触发）只有一个能进入。
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _refreshAction();
        }
        catch
        {
            // 静默失败，不打断用户
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
