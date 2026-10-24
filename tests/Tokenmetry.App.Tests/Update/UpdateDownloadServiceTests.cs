using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 更新包下载器（I-039）：平台资产名落盘、镜像依次回退、全部失败聚合报因、.part 不残留。
/// 网络经 FakeUpdateHttpHandler 覆盖；落盘目录 / reveal 均注入，不触碰真实「下载」文件夹。
/// </summary>
public class UpdateDownloadServiceTests : IDisposable
{
    private readonly string _dir;
    private string? _revealed;

    public UpdateDownloadServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tokenmetry-dl-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不构成测试失败（Windows 文件锁抖动）。
        }
    }

    private UpdateDownloadService CreateService(FakeUpdateHttpHandler handler) =>
        new(handler, downloadDirectory: _dir, revealOverride: path => _revealed = path);

    [Fact]
    public async Task DownloadAsync_writes_platform_asset_into_download_directory()
    {
        var payload = Encoding.UTF8.GetBytes("fake zip bytes");
        var handler = new FakeUpdateHttpHandler(req =>
            FakeUpdateHttpHandler.Bytes(payload));
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        Path.GetFileName(path).Should().Be(UpdateAssets.AssetFileName("v0.3.0"));
        Path.GetDirectoryName(path).Should().Be(_dir);
        (await File.ReadAllBytesAsync(path)).Should().Equal(payload);
        Directory.GetFiles(_dir).Should().ContainSingle(); // .part 已改名，无残留
    }

    [Fact]
    public async Task DownloadAsync_falls_through_to_next_mirror_on_http_error()
    {
        var payload = Encoding.UTF8.GetBytes("mirror2");
        var handler = new FakeUpdateHttpHandler(req =>
            req.RequestUri!.Host == "github.com"
                ? FakeUpdateHttpHandler.Status(HttpStatusCode.NotFound)
                : FakeUpdateHttpHandler.Bytes(payload));
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        (await File.ReadAllTextAsync(path)).Should().Be("mirror2");
        handler.RequestedUrls[0].Should().StartWith($"https://github.com/");
        handler.RequestedUrls[1].Should().StartWith("https://ghfast.top/https://github.com/");
    }

    [Fact]
    public async Task DownloadAsync_aggregates_failures_when_all_mirrors_die()
    {
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Status(HttpStatusCode.Forbidden));
        var service = CreateService(handler);

        var act = async () => await service.DownloadAsync("v0.3.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        ex.Message.Should().Contain("github.com"); // 每次尝试的 host 都在报因里
        Directory.GetFiles(_dir).Should().BeEmpty(); // 失败不落半截文件
    }

    [Fact]
    public async Task DownloadAsync_reports_progress_between_zero_and_hundred()
    {
        var payload = new byte[200_000];
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Bytes(payload));
        var service = CreateService(handler);
        var reports = new System.Collections.Generic.List<double>();
        var progress = new Progress<double>(reports.Add);

        await service.DownloadAsync("v0.3.0", progress);

        reports.Should().NotBeEmpty();
        reports.Min().Should().BeGreaterThan(0);
        reports.Max().Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Reveal_delegates_to_injected_override()
    {
        var service = CreateService(new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Empty()));

        service.Reveal("/tmp/x.zip");

        _revealed.Should().Be("/tmp/x.zip");
    }
}
