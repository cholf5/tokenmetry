namespace Tokenmetry.Infrastructure.Tests.CCUsageCli;

using Tokenmetry.Infrastructure.CCUsageCli;

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: executes cmd.exe / .cmd shims";
        }
    }
}

public class CCUsageCliRunnerTests
{
    [Fact]
    public async Task Echo_output_is_captured_correctly()
    {
        var runner = OperatingSystem.IsWindows()
            ? new CCUsageCliRunner("cmd", "/c echo hello world")
            : new CCUsageCliRunner("echo", "hello world");
        var output = await runner.GetOutputAsync();

        output.Trim().Should().Be("hello world");
    }

    [Fact]
    public async Task Non_zero_exit_code_throws_exception()
    {
        var runner = OperatingSystem.IsWindows()
            ? new CCUsageCliRunner("cmd", "/c exit 1")
            : new CCUsageCliRunner("false", string.Empty);
        var action = () => runner.GetOutputAsync();
        await action.Should().ThrowAsync<Exception>();
    }

    [WindowsOnlyFact]
    public async Task Extensionless_command_resolves_to_cmd_shim_on_windows()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ccusage-runner-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var commandPath = Path.Combine(tempDirectory, "test-command.cmd");
            await File.WriteAllTextAsync(commandPath, "@echo off\r\necho shim works\r\n");

            var previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", tempDirectory + Path.PathSeparator + previousPath);

            try
            {
                var runner = new CCUsageCliRunner("test-command", string.Empty);
                var output = await runner.GetOutputAsync();

                output.Trim().Should().Be("shim works");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [WindowsOnlyFact]
    public async Task Command_with_explicit_extension_runs_without_path_shim_resolution()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ccusage-runner-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var commandPath = Path.Combine(tempDirectory, "explicit-command.cmd");
            await File.WriteAllTextAsync(commandPath, "@echo off\r\necho explicit works\r\n");

            var runner = new CCUsageCliRunner(commandPath, string.Empty);
            var output = await runner.GetOutputAsync();

            output.Trim().Should().Be("explicit works");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [WindowsOnlyFact]
    public async Task Extensionless_path_command_resolves_sibling_cmd_shim_on_windows()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ccusage-runner-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var commandPath = Path.Combine(tempDirectory, "path-command.cmd");
            await File.WriteAllTextAsync(commandPath, "@echo off\r\necho path works\r\n");

            var runner = new CCUsageCliRunner(Path.Combine(tempDirectory, "path-command"), string.Empty);
            var output = await runner.GetOutputAsync();

            output.Trim().Should().Be("path works");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
