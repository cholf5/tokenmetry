using Avalonia.Media;

namespace Tokenmetry.App.Services;

/// <summary>
/// 插件图标加载口（Task 7）：把安装目录里的 PNG 绝对路径换成可绑定的 <see cref="IImage"/>。
/// 抽成接口是为了让 <see cref="AgentIconService"/> 的单测不碰真实文件与渲染平台。
/// </summary>
public interface IPluginIconLoader
{
    /// <summary>加载图标；路径不存在 / 文件损坏 / 非 PNG 等任何失败返回 null（不抛）。</summary>
    IImage? Load(string path);
}
