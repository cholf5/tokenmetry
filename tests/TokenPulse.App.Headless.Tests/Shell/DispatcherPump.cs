using System;
using System.Threading;
using Avalonia.Threading;

namespace TokenPulse.App.Headless.Tests.Shell;

/// <summary>
/// headless 冒烟测试的异步加载等待泵：数据面（UsageQueryService / CacheService）的查询经
/// Task.Run 在线程池执行（SQLite 同步完成型 async + 全量内存聚合不得内联压在 UI 线程上，
/// 见 UsageQueryService 线程模型说明），VM 续体随后回 UI 队列——单次 RunJobs 不再保证加载到位。
/// PumpUntil 轮询泵 Dispatcher 并短暂让渡线程池，直至加载完成谓词成立；超时即抛错失败，
/// 不静默放过未加载断言。
/// </summary>
internal static class DispatcherPump
{
    public static void PumpUntil(Func<bool> loaded, int timeoutMilliseconds = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!loaded())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("异步加载未在超时时间内完成（headless 冒烟等待泵）");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
