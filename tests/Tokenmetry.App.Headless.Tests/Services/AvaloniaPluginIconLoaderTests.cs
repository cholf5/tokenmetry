using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Services;

/// <summary>
/// Task 7 插件图标加载器的真实解码路径：PNG 字节 → 位图、同路径缓存同一实例、文件句柄不被持有
/// （Windows 上插件升级要删除旧版本目录，句柄泄漏会让删除失败——用删除源文件来固化该契约），
/// 以及损坏 / 不存在的文件诚实返回 null。
/// </summary>
public class AvaloniaPluginIconLoaderTests
{
    /// <summary>1×1 透明 PNG（最小合法图片，避免依赖嵌入资源路径）。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [AvaloniaFact]
    public void Load_decodes_png_caches_and_releases_file_handle()
    {
        var path = TempIconPath();
        File.WriteAllBytes(path, OnePixelPng);
        try
        {
            var loader = new AvaloniaPluginIconLoader();

            var icon = loader.Load(path);
            Assert.NotNull(icon);
            Assert.Same(icon, loader.Load(path));

            // Windows：位图若仍持有文件句柄，File.Delete 会抛 IOException（与安装器删除旧版本目录同语义）。
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [AvaloniaFact]
    public void Missing_or_corrupt_file_returns_null()
    {
        var loader = new AvaloniaPluginIconLoader();
        Assert.Null(loader.Load(TempIconPath()));

        var corruptPath = TempIconPath();
        File.WriteAllBytes(corruptPath, [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03]);
        try
        {
            Assert.Null(loader.Load(corruptPath));
        }
        finally
        {
            File.Delete(corruptPath);
        }
    }

    private static string TempIconPath() =>
        Path.Combine(Path.GetTempPath(), $"tokenmetry-icon-{Guid.NewGuid():N}.png");
}
