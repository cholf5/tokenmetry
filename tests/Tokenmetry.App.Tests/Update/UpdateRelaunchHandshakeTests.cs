using System;
using System.Diagnostics;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 重启交接的握手参数：新实例必须能从命令行认出「要等哪个旧进程退出」。
/// 认错（把普通启动当成交接）会让每次启动都白等 20 秒；认不出则新实例抢不到单实例锁、
/// 发个激活请求就退出——用户看到更新完应用消失。
/// </summary>
public class UpdateRelaunchHandshakeTests
{
    [Fact]
    public void TryParse_reads_the_previous_process_id()
    {
        UpdateRelaunchHandshake.TryParse(["--wait-for-exit", "1234"], out var processId).Should().BeTrue();
        processId.Should().Be(1234);

        UpdateRelaunchHandshake.TryParse(["--other", "--wait-for-exit", "42", "extra"], out var later)
            .Should().BeTrue();
        later.Should().Be(42);
    }

    [Fact]
    public void TryParse_ignores_missing_or_invalid_values()
    {
        string[][] invalid =
        [
            [],
            ["--wait-for-exit"],
            ["--wait-for-exit", "0"],
            ["--wait-for-exit", "-3"],
            ["--wait-for-exit", "abc"],
            ["--waitfor-exit", "12"],
        ];

        foreach (var args in invalid)
        {
            UpdateRelaunchHandshake.TryParse(args, out var processId).Should().BeFalse();
            processId.Should().Be(0);
        }
    }

    [Fact]
    public void TryParse_tolerates_null_args()
    {
        UpdateRelaunchHandshake.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void WaitForExit_returns_immediately_for_a_process_that_does_not_exist()
    {
        // GetProcessById 会抛 ArgumentException（进程不存在）——必须被吞掉，不能挡住启动。
        var stopwatch = Stopwatch.StartNew();
        var act = () => UpdateRelaunchHandshake.WaitForExit(int.MaxValue - 1);
        act.Should().NotThrow();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
