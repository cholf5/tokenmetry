using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Tokenmetry.App.Services;

/// <summary>
/// <see cref="IPluginIconLoader"/> 的真实实现（Task 7）：用 Avalonia 的 <see cref="Bitmap"/> 解码 PNG。
/// 按绝对路径缓存——同一插件的行在列表里反复实例化，不重复解码；插件重载/升级产生新路径时
/// 缓存自然 miss（v1 不做按路径失效，同 <see cref="AgentIconService"/> 注释）。加载失败返回 null
/// 且不缓存失败（文件暂时不可读时下次再试，不把一次失败钉成永久空白）。
/// </summary>
public sealed class AvaloniaPluginIconLoader : IPluginIconLoader
{
    private readonly ConcurrentDictionary<string, IImage> _cache = new(StringComparer.Ordinal);

    public IImage? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bitmap = new Bitmap(path);
            _cache[path] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            // 损坏 / 无权限 / 非图片格式：诚实回退字形，不影响列表渲染。
            return null;
        }
    }
}
