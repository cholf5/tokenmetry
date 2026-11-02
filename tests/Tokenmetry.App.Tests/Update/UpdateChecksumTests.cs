using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// SHA-256 校验（自更新的前提）：发布侧文本解析要能吃下 <c>sha256sum</c> 的两种常见写法，
/// 校验失败要拦住安装——镜像前缀是第三方，没校验的自更新等于把执行权交给它们。
/// </summary>
public class UpdateChecksumTests : IDisposable
{
    private readonly string _directory;

    public UpdateChecksumTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-sha-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不构成测试失败。
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  Tokenmetry-0.3.3-osx-arm64.zip\n")]
    public void TryParse_accepts_bare_and_sha256sum_style_text(string text)
    {
        UpdateChecksum.TryParse(text, out var hex).Should().BeTrue();
        hex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")] // 63 位
    [InlineData("<html>rate limited</html>")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParse_rejects_anything_that_is_not_a_sha256(string text)
    {
        UpdateChecksum.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void ComputeHex_matches_the_platform_hash_of_the_file()
    {
        var path = Path.Combine(_directory, "payload.bin");
        var bytes = Encoding.UTF8.GetBytes("tokenmetry");
        File.WriteAllBytes(path, bytes);

        UpdateChecksum.ComputeHex(path).Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Verify_accepts_a_matching_hash_and_rejects_a_different_one()
    {
        var path = Path.Combine(_directory, "payload.bin");
        File.WriteAllBytes(path, "tokenmetry"u8.ToArray());
        var good = UpdateChecksum.ComputeHex(path);

        UpdateChecksum.Verify(path, good, "pkg.zip"); // 不抛即通过
        UpdateChecksum.Verify(path, good.ToUpperInvariant(), "pkg.zip"); // 大小写不敏感

        var act = () => UpdateChecksum.Verify(path, new string('a', 64), "pkg.zip");
        act.Should().Throw<InvalidDataException>().WithMessage("*pkg.zip*");
    }
}
