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

    /// <summary>合法 zip 载荷 = ZIP 本地文件头魔数 + 任意体。下载器现在校验魔数——
    /// “200 回一页 HTML 限流页”不能再被当成安装包，故测试载荷必须形似 zip。</summary>
    private static byte[] ZipPayload(string text) =>
        [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(text)];

    [Fact]
    public async Task DownloadAsync_writes_platform_asset_into_download_directory()
    {
        var payload = ZipPayload("fake zip bytes");
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
        var payload = ZipPayload("mirror2");
        var handler = new FakeUpdateHttpHandler(req =>
            req.RequestUri!.Host == "github.com"
                ? FakeUpdateHttpHandler.Status(HttpStatusCode.NotFound)
                : FakeUpdateHttpHandler.Bytes(payload));
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        (await File.ReadAllTextAsync(path)).Should().EndWith("mirror2");
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
        payload[0] = 0x50;
        payload[1] = 0x4B;
        payload[2] = 0x03;
        payload[3] = 0x04;
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

    // ---- 镜像回退的失败分类（首个公开发布前补）：超时/限流页/截断都归“这个镜像失败”，不是“用户取消” ----

    [Fact]
    public async Task DownloadAsync_treats_request_timeout_as_mirror_failure_and_falls_through()
    {
        // HttpClient.Timeout 到期抛的是 TaskCanceledException（OperationCanceledException 的子类），
        // 但 ct 并未取消——旧实现把它当“用户取消”直接中止整条镜像链，慢网用户一个镜像都拿不到。
        var handler = FakeUpdateHttpHandler.FromAsync(req =>
            req.RequestUri!.Host == "github.com"
                ? Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true))
                : Task.FromResult(FakeUpdateHttpHandler.Bytes(ZipPayload("mirror2"))));
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        handler.RequestedUrls.Should().HaveCount(2);
        (await File.ReadAllTextAsync(path)).Should().EndWith("mirror2");
    }

    [Fact]
    public async Task DownloadAsync_rejects_html_rate_limit_page_and_falls_through()
    {
        // ghproxy 类镜像限流时回 200 + HTML 错误页：没有校验就会把网页改名成 .zip 并在 Finder 里选中。
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>rate limited</body></html>");
        var handler = new FakeUpdateHttpHandler(req =>
            req.RequestUri!.Host == "github.com"
                ? FakeUpdateHttpHandler.Bytes(html)
                : FakeUpdateHttpHandler.Bytes(ZipPayload("real")));
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        handler.RequestedUrls.Should().HaveCount(2);
        (await File.ReadAllTextAsync(path)).Should().EndWith("real");
    }

    [Fact]
    public async Task DownloadAsync_rejects_truncated_body_against_content_length()
    {
        var handler = new FakeUpdateHttpHandler(req =>
        {
            if (req.RequestUri!.Host != "github.com")
            {
                return FakeUpdateHttpHandler.Bytes(ZipPayload("full"));
            }

            var truncated = FakeUpdateHttpHandler.Bytes(ZipPayload("half"));
            truncated.Content!.Headers.ContentLength = 4096; // 服务端声称 4KB，实际只给一点点
            return truncated;
        });
        var service = CreateService(handler);

        var path = await service.DownloadAsync("v0.3.0");

        handler.RequestedUrls.Should().HaveCount(2);
        (await File.ReadAllTextAsync(path)).Should().EndWith("full");
    }

    [Fact]
    public async Task DownloadAsync_fails_with_reason_when_every_mirror_serves_html()
    {
        var html = Encoding.UTF8.GetBytes("<html>blocked</html>");
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Bytes(html));
        var service = CreateService(handler);

        var act = async () => await service.DownloadAsync("v0.3.0");

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Directory.GetFiles(_dir).Should().BeEmpty(); // 网页绝不当成 zip 落盘
    }

    [Fact]
    public async Task DownloadAsync_still_honours_real_cancellation_without_trying_other_mirrors()
    {
        using var cts = new CancellationTokenSource();
        var handler = FakeUpdateHttpHandler.FromAsync(async req =>
        {
            await cts.CancelAsync();
            return FakeUpdateHttpHandler.Bytes(ZipPayload("never"));
        });
        var service = CreateService(handler);

        var act = async () => await service.DownloadAsync("v0.3.0", ct: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        handler.RequestedUrls.Should().ContainSingle(); // 用户取消不浪费时间试下一个镜像
    }
}
