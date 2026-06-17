using System;
using System.Threading;
using System.Threading.Tasks;

namespace CCUsage.App.Services;

public class BackgroundRefreshService
{
    private readonly Func<Task> _refreshAction;
    private readonly int _refreshMinutes;
    private Timer? _timer;
    private bool _isRunning;

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

    private async void OnTimerTick(object? state)
    {
        if (_isRunning) return;
        _isRunning = true;
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
            _isRunning = false;
        }
    }
}
