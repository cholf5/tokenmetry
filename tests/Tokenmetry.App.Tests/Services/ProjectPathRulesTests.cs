using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 项目行可打开路径判定：只认当前平台绝对路径。Claude Code 编码目录名、
/// 占位串、相对路径与另一平台的路径形态一律 false（不猜路径）。
/// </summary>
public sealed class ProjectPathRulesTests
{
    [Theory]
    [InlineData("/work/alpha", true)]
    [InlineData("/", true)]
    [InlineData("relative/path", false)]
    [InlineData("-Users-test-dev-cholf5-mu", false)]
    [InlineData("C:/work/test", false)]
    [InlineData(@"C:\work\test", false)]
    [InlineData("~", false)]
    [InlineData("Gemini", false)]
    [InlineData("Unknown Project", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Unix_paths_are_absolute_only_with_leading_slash(string? path, bool expected) =>
        Assert.Equal(expected, ProjectPathRules.IsLocalAbsolutePath(path, isWindows: false));

    [Theory]
    [InlineData(@"C:\work\alpha", true)]
    [InlineData("C:/work/alpha", true)]
    [InlineData(@"\\server\share\project", true)]
    [InlineData(@"C:work", false)] // 盘符相对路径
    [InlineData("/work/alpha", false)] // Unix 路径在 Windows 不落到当前盘
    [InlineData("-Users-test-dev-cholf5-mu", false)]
    [InlineData("Gemini", false)]
    [InlineData("Unknown Project", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Windows_paths_require_drive_root_or_unc(string? path, bool expected) =>
        Assert.Equal(expected, ProjectPathRules.IsLocalAbsolutePath(path, isWindows: true));
}
