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
/// <para>
/// 解码走显式只读流且立即释放（<c>using</c>）——<c>new Bitmap(path)</c> 会让底层文件句柄跟随位图
/// 生命周期，Windows 上插件升级删除旧版本目录会被占用而失败。位图构造时已把像素解码进内存，
/// 关闭流不影响后续渲染。
/// </para>
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

            // 显式流 + using：Bitmap(stream) 解码后不持有文件句柄（见类型注释），
            // 插件升级删除旧版本目录在 Windows 上不会被图标锁挡住。
            using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
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
