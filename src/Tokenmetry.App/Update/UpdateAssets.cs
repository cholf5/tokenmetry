using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Update;

/// <summary>Release 资产的唯一出处：仓库、资产命名（与 CI 产物名严格一致）、平台挑选与镜像列表。</summary>
public static class UpdateAssets
{
    /// <summary>GitHub 仓库 slug（release.sh / CI workflow / 应用内检查 / 关于面板三处共用同一个事实：tag = v{csproj Version}）。</summary>
    public const string Repository = "cholf5/tokenmetry";

    /// <summary>仓库主页（关于面板「仓库」行出口，I-041）。</summary>
    public const string RepositoryUrl = $"https://github.com/{Repository}";

    /// <summary>仓库 LICENSE 文件直链（关于面板「许可」行出口，I-041；默认分支 main）。</summary>
    public const string LicenseUrl = $"https://github.com/{Repository}/blob/main/LICENSE";

    /// <summary>浏览器打开的 Release 列表页——无网络细节可诊断时的逃生口。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Repository}/releases";

    /// <summary>某个 tag 的 Release 页（浏览器打开，手动下载用）。</summary>
    public static string ReleasePageUrl(string tag) => $"https://github.com/{Repository}/releases/tag/{tag}";

    /// <summary>资产直链。CI 产物命名见 scripts/package.sh 与 .github/workflows/ci.yml，改一处必须三处同改。</summary>
    public static string AssetDownloadUrl(string tag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";

    /// <summary>GitHub 直链优先，其后是 CN 网络下可用的公共加速镜像（前缀拼接）。顺序即尝试顺序；
    /// 镜像挂了就换前缀——这是代码，谁都能改。</summary>
    public static IEnumerable<string> MirrorUrls(string githubUrl)
    {
        yield return githubUrl;
        foreach (var prefix in new[] { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" })
            yield return prefix + githubUrl;
    }

    /// <summary>平台 RID（CI 实际构建的四种之一）。internal 供测试注入 OS/架构。</summary>
    public static string PickRid() =>
        PickRid(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "OSX"
            : "Linux",
            RuntimeInformation.ProcessArchitecture.ToString());

    internal static string PickRid(string os, string arch)
    {
        // 每个 OS 只有单一资产：Windows 仅出 win-x64（ARM 设备走系统 x64 模拟）；
        // Linux 仅出 linux-x64。macOS 分 arm64/x64——Apple Silicon 上跑 x64 必须装 Rosetta，
        // 挑错资产是真实的可用性损失，按进程架构精确匹配。
        return os switch
        {
            "Windows" => "win-x64",
            "OSX" => arch == "Arm64" ? "osx-arm64" : "osx-x64",
            _ => "linux-x64",
        };
    }

    /// <summary>Release 资产完整文件名（便携/解压分发）：Tokenmetry-{版本}-{rid}.zip。版本取自检查结果的 tag
    /// （去 v 前缀），与 scripts/package.sh、.github/workflows/ci.yml 的产物命名严格一致——改一处必须三处同改。</summary>
    public static string AssetFileName(string latestTag) => ZipAssetFileName(latestTag);

    /// <summary>同 <see cref="AssetFileName"/>，指定 RID 版本（测试注入平台，避免跟着构建机架构跑）。</summary>
    internal static string ZipAssetFileName(string latestTag, string rid) =>
        $"Tokenmetry-{latestTag.TrimStart('v')}-{rid}.zip";

    internal static string ZipAssetFileName(string latestTag) => ZipAssetFileName(latestTag, PickRid());

    /// <summary>Windows 安装器资产名（Inno Setup 产物，与 zip 同批产出）。已安装副本走它做静默升级。
    /// 注意：安装器是 PE（MZ 头）不是 zip，下载完整性校验按资产后缀分流。</summary>
    internal static string InstallerAssetFileName(string latestTag) =>
        $"Tokenmetry-{latestTag.TrimStart('v')}-win-x64-setup.exe";

    /// <summary>Windows 上安装器把程序装到哪（Inno 脚本 DefaultDirName 与之同源）。</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tokenmetry");

    /// <summary>本进程是否跑在安装目录里（即"已安装副本"而不是解压出来的便携版）。
    /// 决定升级方式：已安装 → 下安装器静默升级；便携 → 仍旧下 zip 交给用户自己解压。
    /// 判据是进程可执行文件所在目录，不是注册表/标记文件——便携用户把解压目录放哪儿都算便携。</summary>
    public static bool IsInstalledCopy() => IsInstalledCopy(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Environment.ProcessPath, InstallDirectory);

    internal static bool IsInstalledCopy(bool isWindows, string? processPath, string installDirectory)
    {
        if (!isWindows || string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        // 不借 Path.GetDirectoryName：它在 Unix 上不认反斜杠，那用例（以及任何跨平台构造）就会
        // 把 Windows 路径当成一个无目录的文件名。这里只做字符串切分，两种分隔符都认。
        var directory = DirectoryOf(processPath);
        return directory is not null
            && string.Equals(TrimSeparators(directory), TrimSeparators(installDirectory),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? DirectoryOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index > 0 ? path[..index] : null;
    }

    private static string TrimSeparators(string path) => path.TrimEnd('\\', '/');

    /// <summary>本次升级要下的资产名：Windows 已安装副本 → 安装器；其余（含 Windows 便携版）→ zip。</summary>
    internal static string PickAssetName(string latestTag, bool windowsInstalledCopy) =>
        windowsInstalledCopy ? InstallerAssetFileName(latestTag) : ZipAssetFileName(latestTag);
}
