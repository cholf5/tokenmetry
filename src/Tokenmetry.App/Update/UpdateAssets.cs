using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Update;

/// <summary>Release 资产的唯一出处：仓库、资产命名（与 CI 产物名严格一致）、平台挑选与镜像列表。</summary>
public static class UpdateAssets
{
    /// <summary>GitHub 仓库 slug（release.sh / CI workflow / 应用内检查三处共用同一个事实：tag = v{csproj Version}）。</summary>
    public const string Repository = "cholf5/tokenmetry";

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

    /// <summary>Release 资产完整文件名：Tokenmetry-{版本}-{rid}.zip。版本取自检查结果的 tag（去 v 前缀），
    /// 与 scripts/package.sh、.github/workflows/ci.yml 的产物命名严格一致——改一处必须三处同改。</summary>
    public static string AssetFileName(string latestTag) =>
        $"Tokenmetry-{latestTag.TrimStart('v')}-{PickRid()}.zip";
}
