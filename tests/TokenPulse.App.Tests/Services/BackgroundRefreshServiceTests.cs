using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TokenPulse.App.Services;

namespace TokenPulse.App.Tests.Services;

/// <summary>
/// BackgroundRefreshService 互斥与合并语义（S-002 审查修复轮定案）：单飞行槽保证同一时刻至多
/// 一轮刷新（原子占位防双跑，审查 P2 要求）；后到触发<b>并入</b>在跑轮次等待其真实结果而非
/// 排队补跑（P2-2：慢刷新下定时器 tick 不无界堆积）；动作成败经返回值上报（P2-1），既有
/// 静默失败语义对定时器/首刷路径保持。
/// </summary>
public class BackgroundRefreshServiceTests
{
    [Fact]
    public async Task SequentialTriggers_RunActionEachTimeAndReportSuccess()
    {
        var runs = 0;
        var service = new BackgroundRefreshService(() =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(true);
        });

        (await service.TriggerRefreshAsync()).Should().BeTrue();
        (await service.TriggerRefreshAsync()).Should().BeTrue();

        runs.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentTriggers_CoalesceIntoSingleRun_AllObserveSameResult()
    {
        // P2-2 核心语义：并发触发（定时器 tick + 设置页重扫 + 启动首刷任意组合）合并为一轮，
        // 不各自排队补跑（慢刷新下不无界堆积）
        var gate = new TaskCompletionSource();
        var runs = 0;
        var service = new BackgroundRefreshService(async () =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return true;
        });

        var triggers = Enumerable.Range(0, 5).Select(_ => service.TriggerRefreshAsync()).ToList();
        await Task.Delay(50);
        runs.Should().Be(1, "并发触发应合并进在跑的一轮，不追加执行");

        gate.SetResult();
        var results = await Task.WhenAll(triggers);

        runs.Should().Be(1, "整轮只执行一次动作");
        results.Should().OnlyContain(r => r);
    }

    [Fact]
    public async Task MergedTrigger_WaitsForInFlightRound_DoesNotReturnEarly()
    {
        var gate = new TaskCompletionSource();
        var order = new List<string>();
        var service = new BackgroundRefreshService(async () =>
        {
            order.Add("run-start");
            await gate.Task;
            order.Add("run-end");
            return true;
        });

        var first = service.TriggerRefreshAsync(); // 占住单飞槽（模拟定时器刷新在跑）
        var second = service.TriggerRefreshAsync(); // 后到者：并入该轮等待

        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse("在跑的那轮未完成时，并入的触发应等待其真实结果而非空返回");

        gate.SetResult();
        (await first).Should().BeTrue();
        (await second).Should().BeTrue();

        order.Should().Equal("run-start", "run-end"); // 单飞：整轮只执行一次
    }

    [Fact]
    public async Task NewTriggerAfterRoundCompletes_StartsFreshRound()
    {
        // 合并不影响常态：轮次完成后槽即释放，之后的触发开启新一轮（拿到最新数据）
        var runs = 0;
        var service = new BackgroundRefreshService(() =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(true);
        });

        await service.TriggerRefreshAsync();
        await service.TriggerRefreshAsync();

        runs.Should().Be(2, "轮次完成后的新触发应各自开启新一轮");
    }

    [Fact]
    public async Task ActionThrowing_ReportsFailureInsteadOfPropagating()
    {
        // P2-1：动作异常（如接线未完成 / 采集线抛错）转为失败结果；静默语义保持（不向
        // fire-and-forget 的定时器路径传播）
        var service = new BackgroundRefreshService(() => throw new InvalidOperationException("boom"));

        (await service.TriggerRefreshAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ActionReportingError_PropagatesFalseResultToMergedWaiters()
    {
        // P2-1：采集线完成但上报失败（dashboard.Status == "error"）→ 并入等待的设置页路径拿到 false
        var gate = new TaskCompletionSource();
        var service = new BackgroundRefreshService(async () =>
        {
            await gate.Task;
            return false; // dashboard.RefreshAsync 吞异常后 Status="error"，接线层如实上报
        });

        var first = service.TriggerRefreshAsync();
        var second = service.TriggerRefreshAsync(); // 并入同一轮

        gate.SetResult();
        (await first).Should().BeFalse();
        (await second).Should().BeFalse("并入同一轮的触发应拿到同一真实结果");
    }
}
