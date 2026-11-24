using System;

namespace Tokenmetry.App.Services;

/// <summary>
/// 项目行的「可打开路径」判定：只认当前操作系统的绝对路径形态。
/// 项目维度键来自各 Agent 日志，混杂真实 cwd、Claude Code 的编码目录名（"-Users-foo-bar"）
/// 与 "Gemini" / "Unknown Project" 这类占位串；右键菜单只对前者开放，绝不猜测/解码编码目录
/// （猜错会打开到另一个目录）。存在性不在这里做 I/O——点击时再检查，加载期保持纯字符串判定。
/// </summary>
public static class ProjectPathRules
{
    /// <summary>按当前平台判断是否为本机绝对路径（Windows 盘符/UNC，Unix 前导 '/'）。</summary>
    public static bool IsLocalAbsolutePath(string? path) =>
        IsLocalAbsolutePath(path, OperatingSystem.IsWindows());

    /// <summary>显式平台版本（测试跨平台断言两种语义，不受测试机 OS 影响）。</summary>
    internal static bool IsLocalAbsolutePath(string? path, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!isWindows)
        {
            return path[0] == '/';
        }

        // 盘符绝对路径（C:\foo / C:/foo）；"C:foo" 是盘符相对路径，不算。
        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/')
        {
            return true;
        }

        // UNC（\\server\share）。
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
