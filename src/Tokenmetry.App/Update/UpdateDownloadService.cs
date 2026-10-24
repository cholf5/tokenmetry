using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tokenmetry.App.Update;

/// <summary>
/// 更新包下载器：按当前平台挑资产（<see cref="UpdateAssets.PickAssetName"/>），GitHub 直链 +
/// 镜像列表依次尝试，流式落盘（.part → 改名，中断不产半截 zip），成功后在文件管理器中 reveal。
/// 不做自更新替换——应用无签名、单文件/​.app 被运行中进程锁定，替换自身是跨平台雷区；下载 +
/// reveal 已经把「跳 GitHub 翻页找资产」省掉，用户只差一次解压/安装。
/// </summary>
public sealed class UpdateDownloadService
{
    private readonly HttpMessageHandler? _handler;
    private readonly string _downloadDirectory;
    private readonly Action<string>? _revealOverride;

    /// <summary>handler 供测试注入假响应；downloadDirectory 覆盖落盘目录（测试写临时目录）；
    /// revealOverride 覆盖「文件管理器显示」（测试记录调用，不真弹 Finder）。</summary>
    public UpdateDownloadService(HttpMessageHandler? handler = null, string? downloadDirectory = null,
        Action<string>? revealOverride = null)
    {
        _handler = handler;
        _downloadDirectory = downloadDirectory ?? PickDownloadDirectory();
        _revealOverride = revealOverride;
    }

    /// <summary>落盘目录：用户「下载」文件夹（不存在则回落用户主目录——Linux 桌面各有别名，不猜）。</summary>
    public string DownloadDirectory => _downloadDirectory;

    public static string PickDownloadDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    /// <summary>
    /// 下载 latestTag（形如 v0.3.0）对应的平台资产到落盘目录，返回完整路径。
    /// 镜像列表按序尝试：一个 URL 下载启动失败（连接/HTTP/超时）才试下一个；
    /// 已写出部分的 .part 文件每次尝试前清掉。全部失败抛 <see cref="InvalidOperationException"/>，
    /// 消息聚合各尝试的失败原因（UI 直接展示）。
    /// </summary>
    public async Task<string> DownloadAsync(string latestTag, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var asset = UpdateAssets.AssetFileName(latestTag);
        var targetPath = Path.Combine(_downloadDirectory, asset);
        var partPath = targetPath + ".part";
        // 落盘目录缺省取真实「下载」文件夹（必然存在）；注入/回落路径可能没有，自建兜底。
        Directory.CreateDirectory(_downloadDirectory);

        var failures = new List<string>();
        foreach (var url in UpdateAssets.MirrorUrls(UpdateAssets.AssetDownloadUrl(latestTag, asset)))
        {
            try
            {
                await DownloadToAsync(new Uri(url), partPath, targetPath, progress, ct);
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                TryCleanup(partPath);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                TryCleanup(partPath);
                failures.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(string.Join("；", failures));
    }

    private async Task DownloadToAsync(Uri url, string partPath, string targetPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        // ResponseHeadersRead：边下边写边报进度，不把整个大文件缓冲进内存。
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(partPath))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total is > 0)
                {
                    progress?.Report(written * 100.0 / total.Value);
                }
            }
        }

        File.Move(partPath, targetPath, overwrite: true);
    }

    private HttpClient CreateClient()
    {
        // release download 302 → CDN 对象，这里要跟随重定向（与 UpdateChecker 相反）。
        // 超时放宽到 10 分钟：self-contained 包几十 MB，HttpClient 默认 100s 管的是整个请求
        // 生命周期，慢网络直接掐断下载。
        var client = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"tokenmetry-desktop/{UpdateChecker.CurrentVersion.TrimStart('v')}");
        return client;
    }

    private static void TryCleanup(string partPath)
    {
        try
        {
            File.Delete(partPath);
        }
        catch (IOException)
        {
            // 半截文件留着无害（下次尝试会覆盖），删除失败不掩盖原始异常。
        }
    }

    /// <summary>在文件管理器中显示已下载的文件（best-effort：失败只打诊断，不打断主流程）。</summary>
    public void Reveal(string filePath)
    {
        if (_revealOverride is { } reveal)
        {
            reveal(filePath);
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // explorer 对参数自己做解析（不用标准引号规则），/select,"path" 必须拼成单串。
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", Path.GetDirectoryName(filePath)!)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 文件管理器显示更新包失败（{filePath}）：{ex.Message}");
        }
    }
}
