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
    public void Second_instance_cannot_acquire_while_first_holds()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        first.TryAcquire().Should().BeTrue();

        // Mutex 是线程递归的：同线程重复 WaitOne 会重入成功。真实第二实例是跨进程（必然
        // 跨线程），守卫语义正确，本用例也必须跨线程尝试获取——第二获取者用专用 Thread
        //（全新线程，结构性排除复用持锁线程的重入），主线程 Join 等待。
        //
        // 历史教训（本用例曾经的偶发失败根因，勿改回 await 形态）：不能 await 跨线程探测。
        // await 会把测试方法挂起在拿锁线程上，而该线程（线程池 worker / LongRunning 任务
        // 收尾的内联续体线程）可能在挂起后随即退出；CoreCLR PAL 按命名 Mutex 语义把死亡
        // 线程的所有权标记为 abandoned，第二获取者的 WaitOne(0) 反而抛 AbandonedMutex
        // Exception，TryAcquire 按"上一实例崩溃接管"设计返回 true。拿锁线程必须活到探测
        // 结束，故整个用例保持同步。诊断依据：xunit 宿主内 200 轮复现 195 次，违例全部走
        // abandoned 分支且第二对象 createdNew=false（同一命名对象，非名字注册竞态）。
        bool? secondAcquired = null;
        var secondThread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondAcquired = second.TryAcquire();
        });
        secondThread.Start();
        secondThread.Join();

        secondAcquired.Should().BeFalse();
    }

    [Fact]
    public void Abandoned_mutex_from_dead_owner_thread_is_taken_over()
    {
        var name = UniqueName();
        SingleInstanceGuard? abandoned = null;
        var ownerAcquired = false;

        // 持有者线程故意不 Dispose 守卫就退出：PAL 把死亡线程持有的命名 Mutex 标记为
        // abandoned——这正是生产中"上一实例崩溃"的真实形态。线程死亡发生在 Join 返回前，
        // 废弃状态对后续获取者确定可见。
        var deadOwner = new Thread(() =>
        {
            abandoned = new SingleInstanceGuard(name);
            ownerAcquired = abandoned.TryAcquire();
        });
        deadOwner.Start();
        deadOwner.Join();

        ownerAcquired.Should().BeTrue();

        using var next = new SingleInstanceGuard(name);
        next.TryAcquire().Should().BeTrue("持有者线程已死亡，abandoned mutex 应视为已获得");

        // 守卫仍被本测试引用到断言之后：确保 abandoned 状态由线程死亡产生，而不是 GC
        // 终结器提前释放句柄把对象从名字注册表里移除。
        GC.KeepAlive(abandoned);
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

    // Unix 命名管道落盘为 $TMPDIR/CoreFxPipe_<管道名> 的 Unix domain socket，macOS 路径上限
    // 104 字符：32 位完整 hex GUID 必超限（/var/folders 前缀 ~46 字符 + "CoreFxPipe_" +
    // ".Activation"）。12 位 hex（48 bit）对 xunit 并行隔离绰绰有余且留足余量。
    private static string UniqueName()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{SingleInstanceGuard.DefaultInstanceName}-{suffix}";
    }
}
