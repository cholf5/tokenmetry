using System.Linq;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>资产命名/平台挑选/镜像列表（I-039）：与 CI 产物名的对齐契约是硬约束（改一处必须三处同改）。</summary>
public class UpdateAssetsTests
{
    [Theory]
    [InlineData("Windows", "Arm64", "win-x64")]
    [InlineData("Windows", "X64", "win-x64")] // Windows 单资产，ARM 走系统 x64 模拟
    [InlineData("OSX", "Arm64", "osx-arm64")]
    [InlineData("OSX", "X64", "osx-x64")]
    [InlineData("Linux", "X64", "linux-x64")]
    [InlineData("Linux", "Arm64", "linux-x64")] // Linux 单资产
    public void PickRid_maps_platform_to_built_asset(string os, string arch, string expected)
    {
        UpdateAssets.PickRid(os, arch).Should().Be(expected);
    }

    [Fact]
    public void AssetFileName_bakes_tag_version_and_rid()
    {
        UpdateAssets.AssetFileName("v0.3.0").Should().Be($"Tokenmetry-0.3.0-{UpdateAssets.PickRid()}.zip");
    }

    [Fact]
    public void AssetDownloadUrl_matches_ci_asset_naming()
    {
        UpdateAssets.AssetDownloadUrl("v0.2.0", "osx-arm64.zip")
            .Should().Be($"https://github.com/{UpdateAssets.Repository}/releases/download/v0.2.0/osx-arm64.zip");
    }

    [Fact]
    public void MirrorUrls_starts_with_github_then_known_prefixes()
    {
        var urls = UpdateAssets.MirrorUrls("https://github.com/cholf5/tokenmetry/releases/download/v0.2.0/win-x64.zip").ToList();

        urls.Should().HaveCount(4);
        urls[0].Should().Be("https://github.com/cholf5/tokenmetry/releases/download/v0.2.0/win-x64.zip");
        urls[1].Should().StartWith("https://ghfast.top/https://github.com/");
        urls[2].Should().StartWith("https://gh-proxy.com/https://github.com/");
        urls[3].Should().StartWith("https://ghproxy.net/https://github.com/");
    }
}
