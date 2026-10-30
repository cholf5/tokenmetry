using System;
using System.IO;
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

    // ---- Windows 安装器（P-008）：已安装副本升级下安装器，其余仍下 zip ----

    [Fact]
    public void PickAssetName_installed_copy_takes_the_installer_others_take_the_zip()
    {
        UpdateAssets.PickAssetName("v0.3.1", windowsInstalledCopy: true)
            .Should().Be("Tokenmetry-0.3.1-win-x64-setup.exe");
        UpdateAssets.PickAssetName("v0.3.1", windowsInstalledCopy: false)
            .Should().Be($"Tokenmetry-0.3.1-{UpdateAssets.PickRid()}.zip");
    }

    [Fact]
    public void Installer_asset_name_matches_the_ci_and_package_sh_output()
    {
        // 三处同改契约：package.sh / ci.yml 出 dist/Tokenmetry-{版本}-win-x64-setup.exe，
        // Inno 脚本的 OutputBaseFilename 与这里必须逐字相同。
        UpdateAssets.InstallerAssetFileName("v9.9.9").Should().Be("Tokenmetry-9.9.9-win-x64-setup.exe");
    }

    [Theory]
    [InlineData(true, @"C:\Users\u\AppData\Local\Programs\Tokenmetry\Tokenmetry.exe", @"C:\Users\u\AppData\Local\Programs\Tokenmetry", true)]
    [InlineData(true, @"c:\users\U\appdata\local\programs\tokenmetry\tokenmetry.exe", @"C:\Users\u\AppData\Local\Programs\Tokenmetry", true)] // Windows 路径大小写不敏感
    [InlineData(true, @"D:\tools\Tokenmetry.exe", @"C:\Users\u\AppData\Local\Programs\Tokenmetry", false)] // 解压出来的便携版
    [InlineData(false, "/Applications/Tokenmetry.app/Contents/MacOS/Tokenmetry", @"C:\x", false)] // 非 Windows 永不静默升级
    [InlineData(true, null, @"C:\x", false)]
    public void IsInstalledCopy_compares_the_process_directory_with_the_install_directory(
        bool isWindows, string? processPath, string installDirectory, bool expected)
    {
        UpdateAssets.IsInstalledCopy(isWindows, processPath, installDirectory).Should().Be(expected);
    }

    [Fact]
    public void InstallDirectory_matches_the_inno_default_dir_name()
    {
        // installer/Tokenmetry.iss 的 DefaultDirName={localappdata}\Programs\Tokenmetry 必须与这里一致：
        // 不一致则已安装副本认不出来，升级会退化成“下载 zip 让你自己解压”。
        UpdateAssets.InstallDirectory.Should().EndWith(Path.Combine("Programs", "Tokenmetry"));
        Path.GetFileName(UpdateAssets.InstallDirectory).Should().Be("Tokenmetry");
    }
}
