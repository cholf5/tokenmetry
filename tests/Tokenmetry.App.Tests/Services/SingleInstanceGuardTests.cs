using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// B-013 单实例守卫：命名 Mutex 独占 + 命名管道激活通道（真实 OS 基元，三平台一致语义；
/// 名字逐用例 Guid 隔离，xunit 并行安全）。headless 旁路本身在 Program.Main（测试宿主不经
/// Program.Main，守卫对整套测试天然不可见，无需专门用例）。
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public async Task Second_instance_cannot_acquire_while_first_holds()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        first.TryAcquire().Should().BeTrue();

        // Mutex 是线程递归的：同线程重复 WaitOne 会重入成功。真实第二实例是跨进程（必然
        // 跨线程），守卫语义正确，本用例也必须跨线程尝试获取——LongRunning 保证专用线程；
        // 普通 Task.Run 会复用刚归还线程池的 xunit 工作线程（正是持锁线程）而偶发重入。
        var secondAcquired = await Task.Factory.StartNew(
            () =>
            {
                using var second = new SingleInstanceGuard(name);
                return second.TryAcquire();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        secondAcquired.Should().BeFalse();
    }

    [Fact]
    public void Guard_released_after_dispose_allows_new_first_instance()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        first.TryAcquire().Should().BeTrue();
        first.Dispose();

        using var next = new SingleInstanceGuard(name);
        next.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public async Task Activation_request_reaches_first_instance_listener()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        first.TryAcquire().Should().BeTrue();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListener(() => activated.TrySetResult());

        // 重试预算盖过监听线程建管道的启动窗口（与生产第二实例的重试语义同源）。
        SingleInstanceGuard.TrySignalActivation(name, attempts: 8, connectTimeoutMs: 500).Should().BeTrue();

        var delivered = await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(5))) == activated.Task;
        delivered.Should().BeTrue("第二实例的激活请求应送达首实例监听回调");
    }

    [Fact]
    public async Task Listener_survives_dropped_connection_and_accepts_next_request()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        first.TryAcquire().Should().BeTrue();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListener(() => activated.TrySetResult());

        // 客户端连上后不发请求即断开：只作废该次请求，监听必须继续接受后续实例。
        using (var flaky = new System.IO.Pipes.NamedPipeClientStream(".", $"{name}.Activation",
            System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            flaky.Connect(2000);
        }

        SingleInstanceGuard.TrySignalActivation(name, attempts: 8, connectTimeoutMs: 500).Should().BeTrue();

        var delivered = await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(5))) == activated.Task;
        delivered.Should().BeTrue("断连后的下一次激活请求应仍能送达");
    }

    [Fact]
    public void Signal_fails_when_no_instance_listens()
    {
        var name = UniqueName();

        SingleInstanceGuard.TrySignalActivation(name, attempts: 1, connectTimeoutMs: 200).Should().BeFalse();
    }

    private static string UniqueName() => $"{SingleInstanceGuard.DefaultInstanceName}-{Guid.NewGuid():N}";
}
