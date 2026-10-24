using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 更新检查器（I-039）：releases/latest 302 重定向 → tag → 三段版本比较。断言语言无关
/// （只碰数字与 URL，不碰文案）。网络行为经 FakeUpdateHttpHandler 覆盖（302 相对/绝对 Location、
/// 无 Location、HTTP 403、网络异常）。
/// </summary>
public class UpdateCheckerTests
{
    [Fact]
    public void Evaluate_newer_release_reports_available_with_tag_and_url()
    {
        var result = UpdateChecker.Evaluate("https://github.com/cholf5/tokenmetry/releases/tag/v0.3.1", "v0.2.0");

        result.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        result.LatestVersion.Should().Be("v0.3.1");
        result.ReleaseUrl.Should().Be("https://github.com/cholf5/tokenmetry/releases/tag/v0.3.1");
    }

    [Fact]
    public void Evaluate_same_or_older_release_reports_up_to_date()
    {
        UpdateChecker.Evaluate("https://github.com/cholf5/tokenmetry/releases/tag/v0.2.0", "v0.2.0")
            .Outcome.Should().Be(UpdateCheckOutcome.UpToDate);

        UpdateChecker.Evaluate("https://github.com/cholf5/tokenmetry/releases/tag/v0.1.9", "v0.2.0")
            .Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
    }

    [Theory]
    [InlineData("v0.10.0", "v0.9.0")] // 逐段数字比较：10 > 9 而非字符串序
    [InlineData("v1.0.0", "v0.99.99")]
    public void Evaluate_compares_segments_numerically(string latest, string current)
    {
        UpdateChecker.Evaluate($"https://github.com/cholf5/tokenmetry/releases/tag/{latest}", current)
            .Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com/cholf5/tokenmetry")] // 无 tag 段
    [InlineData("not-a-version")] // 非数字 tag
    public void Evaluate_unrecognizable_tag_fails_as_invalid_response(string? releaseUrl)
    {
        var result = UpdateChecker.Evaluate(releaseUrl, "v0.2.0");

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.ErrorKind.Should().Be(UpdateCheckErrorKind.InvalidResponse);
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V4.5", 4, 5, 0)] // 大写 v + 缺段按 0
    [InlineData("2.0.0-beta.1", 2, 0, 0)] // 非数字后缀忽略
    public void TryParseVersion_parses_three_segments(string tag, int major, int minor, int patch)
    {
        UpdateChecker.TryParseVersion(tag, out var version).Should().BeTrue();
        version.Should().Be((major, minor, patch));
    }

    [Fact]
    public void ExtractTag_takes_last_path_segment_unescaped()
    {
        UpdateChecker.ExtractTag("https://github.com/cholf5/tokenmetry/releases/tag/v0.3.0")
            .Should().Be("v0.3.0");
    }

    [Fact]
    public async Task CheckAsync_follows_relative_location_contract_and_evaluates()
    {
        // 网页端点回 302 + 相对 Location（GitHub 实际行为）；确认无需跟随重定向即可拿到 tag。
        var handler = new FakeUpdateHttpHandler(_ =>
            FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.4.0"));
        var checker = new UpdateChecker(handler, currentVersion: "v0.2.0");

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        result.LatestVersion.Should().Be("v0.4.0");
        handler.RequestedUrls.Should().Contain($"https://github.com/{UpdateAssets.Repository}/releases/latest");
    }

    [Fact]
    public async Task CheckAsync_without_location_header_fails_as_invalid_response()
    {
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Empty());
        var checker = new UpdateChecker(handler, currentVersion: "v0.2.0");

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.ErrorKind.Should().Be(UpdateCheckErrorKind.InvalidResponse);
    }

    [Fact]
    public async Task CheckAsync_http_status_failure_becomes_short_diagnosis()
    {
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Status(HttpStatusCode.Forbidden));
        var checker = new UpdateChecker(handler, currentVersion: "v0.2.0");

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.ErrorKind.Should().Be(UpdateCheckErrorKind.HttpStatus);
        result.Error.Should().Contain("403");
    }

    [Fact]
    public async Task CheckAsync_network_failure_becomes_result_not_exception()
    {
        var handler = new FakeUpdateHttpHandler(_ => throw new HttpRequestException("connection reset"));
        var checker = new UpdateChecker(handler, currentVersion: "v0.2.0");

        var result = await checker.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.ErrorKind.Should().Be(UpdateCheckErrorKind.Network);
        result.Error.Should().Contain("connection reset");
    }

    [Fact]
    public async Task CheckAsync_uses_injected_baseline_for_comparison()
    {
        // 同一响应在注入基线 v0.3.0 下判 UpToDate——比较基线可注入（测试自洽），与静态程序集版本无关。
        var handler = new FakeUpdateHttpHandler(_ =>
            FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.2.0"));
        var checker = new UpdateChecker(handler, currentVersion: "v0.3.0");

        (await checker.CheckAsync()).Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
        checker.Version.Should().Be("v0.3.0");
    }
}
