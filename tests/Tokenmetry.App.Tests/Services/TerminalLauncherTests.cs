using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 终端启动计划构建（纯函数；不在测试里真起终端）：平台优先级、working-directory 参数、
/// 含空格路径保持单个参数、iTerm2 走 AppleScript 且路径不进脚本源码。
/// </summary>
public sealed class TerminalLauncherTests
{
    [Fact]
    public void Mac_ghostty_opens_new_window_at_working_directory()
    {
        var plan = Assert.Single(
            TerminalLauncher.BuildMacPlans("/work/alpha with space", TerminalLauncher.MacTerminalKind.Ghostty));

        Assert.Equal("open", plan.FileName);
        Assert.Equal(
            new[]
            {
                "-na", "Ghostty.app", "--args",
                "--working-directory=/work/alpha with space",
                "--window-inherit-working-directory=false",
            },
            plan.Arguments);
        Assert.Equal("/work/alpha with space", plan.WorkingDirectory);
    }

    [Fact]
    public void Mac_iterm_passes_path_as_argv_not_script_source()
    {
        var plan = Assert.Single(
            TerminalLauncher.BuildMacPlans("/work/alpha", TerminalLauncher.MacTerminalKind.ITerm));

        Assert.Equal("osascript", plan.FileName);
        Assert.Equal("/work/alpha", plan.Arguments[^1]); // 路径经 "--" 后的 argv 传入
        Assert.Contains(plan.Arguments, argument => argument.Contains("quoted form of item 1 of argv", StringComparison.Ordinal));
        var scriptArguments = plan.Arguments.TakeWhile(argument => argument != "--");
        Assert.DoesNotContain(scriptArguments, argument => argument.Contains("/work/alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void Mac_terminal_app_opens_directory()
    {
        var plan = Assert.Single(
            TerminalLauncher.BuildMacPlans("/work/alpha", TerminalLauncher.MacTerminalKind.Terminal));

        Assert.Equal("open", plan.FileName);
        Assert.Equal(new[] { "-a", "Terminal", "/work/alpha" }, plan.Arguments);
    }

    [Fact]
    public void Windows_prefers_windows_terminal_then_cmd()
    {
        var plans = TerminalLauncher.BuildWindowsPlans(@"C:\work\alpha");

        Assert.Equal(2, plans.Count);
        Assert.Equal("wt.exe", plans[0].FileName);
        Assert.Equal(new[] { "-d", @"C:\work\alpha" }, plans[0].Arguments);
        Assert.Equal("cmd.exe", plans[1].FileName);
        Assert.Equal(@"C:\work\alpha", plans[1].WorkingDirectory); // cmd 靠进程工作目录
    }

    [Fact]
    public void Linux_honors_terminal_env_before_xdg_and_known_emulators()
    {
        var plans = TerminalLauncher.BuildLinuxPlans("/work/alpha", "alacritty --class dev");

        Assert.Equal("alacritty", plans[0].FileName);
        Assert.Equal(new[] { "--class", "dev" }, plans[0].Arguments);
        Assert.Equal("xdg-terminal-exec", plans[1].FileName);
        Assert.Equal("ghostty", plans[2].FileName);
        Assert.Equal("gnome-terminal", plans[3].FileName);
        Assert.Equal(new[] { "--working-directory", "/work/alpha" }, plans[3].Arguments);
        Assert.All(plans, plan => Assert.Equal("/work/alpha", plan.WorkingDirectory));
    }

    [Fact]
    public void Linux_without_terminal_env_starts_with_xdg_terminal_exec()
    {
        var plans = TerminalLauncher.BuildLinuxPlans("/work/alpha", terminalEnvironmentVariable: null);

        Assert.Equal("xdg-terminal-exec", plans[0].FileName);
        Assert.Contains(plans, plan => plan.FileName == "xterm");
    }
}
