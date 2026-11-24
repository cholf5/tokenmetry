using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Tokenmetry.App.Services;

/// <summary>
/// 外部应用图标提供者：菜单项按应用缓存图标，失败返回 null（视图保留纯文字项）。
/// 目前 macOS 从已安装的 <c>.app</c> 提取真实图标；Windows / Linux 的同一接口后续补实现。
/// </summary>
public interface IAppIconProvider
{
    /// <summary>
    /// 取一个应用的图标。<paramref name="cacheKey"/> 是进程内缓存键（同一应用重复开菜单不重复提取），
    /// <paramref name="iconSource"/> 是平台定位到的图标源（macOS = .app 路径）。
    /// </summary>
    Task<IImage?> GetIconAsync(string cacheKey, string? iconSource);
}

/// <summary>
/// 进程级图标缓存 + 平台分派。缓存的是提取任务本身（并发首开只提取一次），
/// 失败也缓存（同一应用本次进程不再反复起子进程）。
/// </summary>
public sealed class AppIconProvider : IAppIconProvider
{
    public static AppIconProvider Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<IImage?>>> _cache = new(StringComparer.Ordinal);

    public Task<IImage?> GetIconAsync(string cacheKey, string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return Task.FromResult<IImage?>(null);
        }

        var lazy = _cache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<IImage?>>(
                () => LoadAsync(iconSource),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private static async Task<IImage?> LoadAsync(string iconSource)
    {
        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                return null;
            }

            var png = await MacAppIconExtractor.ExtractPngBytesAsync(iconSource);
            if (png is null || png.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 读取应用图标失败（{iconSource}）：{ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// macOS 应用图标提取（与 dsh open-in-app 同一路径）：读 bundle 的
/// <c>CFBundleIconFile</c>（`plutil -convert json`），缺省扫 <c>Resources/*.icns</c>，
/// 再用系统自带的 `sips` 转成 128px PNG。全程 argv 直传，不拼 shell 字符串；
/// 任一命令失败/超时返回 null，视图回退纯文字。
/// </summary>
internal static class MacAppIconExtractor
{
    private const int CommandTimeoutMs = 5000;

    internal static Task<byte[]?> ExtractPngBytesAsync(string bundlePath) =>
        Task.Run(() => ExtractPngBytes(bundlePath));

    private static byte[]? ExtractPngBytes(string bundlePath)
    {
        if (!Directory.Exists(bundlePath)
            || !bundlePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var contents = Path.Combine(bundlePath, "Contents");
        var resources = Path.Combine(contents, "Resources");
        var infoPlist = Path.Combine(contents, "Info.plist");
        var plistJson = RunCommand("plutil", ["-convert", "json", "-o", "-", infoPlist]);

        string[] icnsFiles;
        try
        {
            icnsFiles = Directory.Exists(resources)
                ? Directory.GetFiles(resources, "*.icns")
                : [];
        }
        catch
        {
            icnsFiles = [];
        }

        var iconFile = ChooseIconFile(ParseIconFileName(plistJson), icnsFiles);
        if (iconFile is null)
        {
            return null;
        }

        var workDirectory = Path.Combine(
            Path.GetTempPath(), "tokenmetry-icon-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDirectory);
            var output = Path.Combine(workDirectory, "icon.png");
            if (RunCommand("sips", ["-s", "format", "png", "-Z", "128", iconFile, "--out", output]) is null)
            {
                return null;
            }

            return File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响图标结果。
            }
        }
    }

    /// <summary>从 Info.plist JSON 里取图标文件名（可能省略 .icns 扩展名）。</summary>
    internal static string? ParseIconFileName(string? plistJson)
    {
        if (string.IsNullOrWhiteSpace(plistJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(plistJson);
            if (document.RootElement.TryGetProperty("CFBundleIconFile", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } name)
            {
                return name.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? name : name + ".icns";
            }
        }
        catch (JsonException)
        {
            // plutil 输出损坏时走 Resources 扫描兜底。
        }

        return null;
    }

    /// <summary>声明的图标存在则用声明值（大小写不敏感），否则取 Resources 里第一个 .icns。</summary>
    internal static string? ChooseIconFile(string? declaredName, IReadOnlyList<string> availableIcns)
    {
        if (declaredName is not null)
        {
            var match = availableIcns.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), declaredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return availableIcns.FirstOrDefault();
    }

    private static string? RunCommand(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已退出/无权限时忽略。
                }

                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 图标提取命令失败（{fileName}）：{ex.Message}");
            return null;
        }
    }
}
