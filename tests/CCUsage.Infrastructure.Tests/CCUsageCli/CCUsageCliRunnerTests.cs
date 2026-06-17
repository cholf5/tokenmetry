namespace CCUsage.Infrastructure.Tests.CCUsageCli;

using CCUsage.Infrastructure.CCUsageCli;

public class CCUsageCliRunnerTests
{
    [Fact]
    public async Task Echo_output_is_captured_correctly()
    {
        var runner = new CCUsageCliRunner("cmd", "/c echo hello world");
        var output = await runner.GetOutputAsync();

        output.Trim().Should().Be("hello world");
    }

    [Fact]
    public async Task Non_zero_exit_code_throws_exception()
    {
        var runner = new CCUsageCliRunner("cmd", "/c exit 1");
        var action = () => runner.GetOutputAsync();
        await action.Should().ThrowAsync<Exception>();
    }
}
