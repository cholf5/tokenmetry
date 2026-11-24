using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// macOS 应用图标提取的纯逻辑（Info.plist 声明解析 / icns 选择）+ 一次真实系统应用提取。
/// 真实提取只在 macOS 上跑，其余平台直接返回；不涉及 Avalonia 位图。
/// </summary>
public sealed class MacAppIconExtractorTests
{
    [Theory]
    [InlineData("AppIcon", "AppIcon.icns")]
    [InlineData("AppIcon.icns", "AppIcon.icns")]
    [InlineData("appicon.ICNS", "appicon.ICNS")]
    public void Parses_declared_bundle_icon_file(string declared, string expected)
    {
        var json = $"{{\"CFBundleIconFile\":\"{declared}\"}}";

        Assert.Equal(expected, MacAppIconExtractor.ParseIconFileName(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"CFBundleIconFile\":42}")]
    public void Missing_or_malformed_plist_yields_null(string? json) =>
        Assert.Null(MacAppIconExtractor.ParseIconFileName(json));

    [Fact]
    public void Chooses_declared_icon_case_insensitively()
    {
        var available = new[]
        {
            "/app/Contents/Resources/appicon.icns",
            "/app/Contents/Resources/Other.icns",
        };

        Assert.Equal(available[0], MacAppIconExtractor.ChooseIconFile("AppIcon.icns", available));
    }

    [Fact]
    public void Falls_back_to_first_icns_when_declared_missing()
    {
        var available = new[]
        {
            "/app/Contents/Resources/One.icns",
            "/app/Contents/Resources/Two.icns",
        };

        Assert.Equal(available[0], MacAppIconExtractor.ChooseIconFile("Missing.icns", available));
        Assert.Null(MacAppIconExtractor.ChooseIconFile(null, []));
    }

    [Fact]
    public async Task Extracts_system_terminal_icon_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        const string terminal = "/System/Applications/Utilities/Terminal.app";
        if (!Directory.Exists(terminal))
        {
            return;
        }

        var png = await MacAppIconExtractor.ExtractPngBytesAsync(terminal);

        Assert.NotNull(png);
        Assert.True(png!.Length > 8);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }
}
