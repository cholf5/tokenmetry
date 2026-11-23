namespace Tokenmetry.Core.Models;

/// <summary>
/// 插件会话 id 的命名空间编解码。插件产出的原始会话 id 只在插件内唯一，多个插件
/// （或插件与内置源）之间会相撞，落 facts 前统一编码为 <c>plugin:&lt;pluginId&gt;:&lt;raw&gt;</c>。
/// 前缀格式固定，UI 展示时用 <see cref="TryStrip"/> 剥回原文（Task 7）。
/// </summary>
public static class PluginSessionId
{
    private const string Prefix = "plugin:";

    /// <summary>编码为 <c>plugin:&lt;pluginId&gt;:&lt;rawSessionId&gt;</c>。</summary>
    public static string Encode(string pluginId, string rawSessionId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(rawSessionId);
        return string.Concat(Prefix, pluginId, ":", rawSessionId);
    }

    /// <summary>
    /// 剥离插件命名空间：仅接受 <c>plugin:</c> 开头且 pluginId、raw 均非空的串，
    /// 返回第一个分隔冒号之后的原文；其余（内置源 id、畸形串）返回 false。
    /// </summary>
    public static bool TryStrip(string sessionId, out string rawSessionId)
    {
        rawSessionId = string.Empty;

        if (sessionId is null || !sessionId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = sessionId[Prefix.Length..];
        var separator = rest.IndexOf(':', StringComparison.Ordinal);

        // 需要非空 pluginId 与非空 raw：分隔符不能在开头或结尾。
        if (separator <= 0 || separator >= rest.Length - 1)
        {
            return false;
        }

        rawSessionId = rest[(separator + 1)..];
        return true;
    }
}
