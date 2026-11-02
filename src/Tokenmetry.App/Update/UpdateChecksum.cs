using System;
using System.IO;
using System.Security.Cryptography;

namespace Tokenmetry.App.Update;

/// <summary>
/// 更新包完整性校验（SHA-256）。为什么必须有：下载走三个公共镜像前缀，只校验「Content-Length 收满 +
/// 载荷头魔数」时，被劫持的镜像只要回一个 PK 开头的 zip 就能让我们执行任意代码——而在自更新形态下，
/// 「执行」意味着替换用户正在跑的程序本身。CI 为每个资产产出 <c>&lt;资产名&gt;.sha256</c>，应用先取它再校验。
/// </summary>
public static class UpdateChecksum
{
    /// <summary>文件 SHA-256 的小写十六进制串（与 CI 的 <c>sha256sum</c> 输出同一口径）。</summary>
    public static string ComputeHex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 解析校验和文件：接受 <c>&lt;hex&gt;</c> 与 <c>&lt;hex&gt;  &lt;文件名&gt;</c> 两种写法
    /// （后者是 <c>sha256sum</c> / <c>shasum -a 256</c> 的默认输出，CI 直接用它生成）。
    /// 十六进制大小写不敏感。任何不合格的输入都返回 false——调用方据此拒绝安装，不做「看起来像就放行」。
    /// </summary>
    public static bool TryParse(string? text, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var firstToken = text.TrimStart().Split([' ', '\t', '\r', '\n'], 2)[0];
        if (firstToken.Length != 64)
        {
            return false;
        }

        foreach (var c in firstToken)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        hex = firstToken.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// 校验下载到本地的资产。不匹配抛 <see cref="InvalidDataException"/>（消息含资产名，UI 直接展示）；
    /// 调用方负责删掉这个文件——它可能是攻击载荷，不应该留在「下载」文件夹里让人手动双击。
    /// </summary>
    public static void Verify(string filePath, string expectedHex, string assetName)
    {
        var actual = ComputeHex(filePath);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"sha256 mismatch for {assetName}: expected {expectedHex}, got {actual}");
        }
    }
}
