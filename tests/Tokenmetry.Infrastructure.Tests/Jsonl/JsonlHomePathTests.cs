namespace Tokenmetry.Infrastructure.Tests.Jsonl;

using Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// 2026-10-23 Windows 冒烟回归锁：旧实现 "~/a/b" 展开后保留 remainder 的正斜杠，
/// Windows 的 Path.Combine 不归一化分隔符，产出 "C:\Users\x\a/b" 混合路径并被
/// PiLogLocator 等无 GetFullPath 包装的调用方原样返回。
/// 断言期望值用 Path.Combine 分段构造、三平台通用（Windows 下旧实现即红，无需跳过）。
/// </summary>
public class JsonlHomePathTests
{
    [Fact]
    public void Tilde_prefix_expansion_uses_platform_separator()
    {
        var home = FakeHome();
        var stub = StubEnv(home);

        var expanded = JsonlHomePath.ExpandHomePath("~/.omp/agent/sessions", stub);

        expanded.Should().Be(Path.Combine(home, ".omp", "agent", "sessions"));
        if (OperatingSystem.IsWindows())
        {
            expanded.Should().NotContain("/", "Windows 下展开结果不得残留正斜杠混合路径");
        }
    }

    [Fact]
    public void Tilde_only_expands_to_home()
    {
        var home = FakeHome();

        JsonlHomePath.ExpandHomePath("~", StubEnv(home)).Should().Be(home);
    }

    [Fact]
    public void Non_home_paths_pass_through_untouched()
    {
        var stub = StubEnv(FakeHome());
        // "~user/..."（他人 home）蓝本语义不展开，一并锁定。
        string[] rawPaths = ["/absolute/path", "relative/path", "~user/sessions", "/home/tester"];

        foreach (var raw in rawPaths)
        {
            JsonlHomePath.ExpandHomePath(raw, stub).Should().Be(raw);
        }
    }

    [Fact]
    public void Tilde_prefix_without_home_stays_raw()
    {
        JsonlHomePath.ExpandHomePath("~/sessions", StubEnv(null)).Should().Be("~/sessions");
    }

    private static string FakeHome() =>
        OperatingSystem.IsWindows() ? @"C:\Users\tester" : "/home/tester";

    private static Func<string, string?> StubEnv(string? home) =>
        name => name is "USERPROFILE" or "HOME" ? home : null;
}
