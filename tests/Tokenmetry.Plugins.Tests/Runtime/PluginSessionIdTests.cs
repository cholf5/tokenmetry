using Tokenmetry.Core.Models;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 插件会话命名空间：facts 里的 session_id 用 <c>plugin:&lt;pluginId&gt;:&lt;raw&gt;</c> 编码，
/// 避免不同插件（以及插件与内置源）的会话 id 相撞；UI（Task 7）展示时再剥掉前缀。
/// </summary>
public class PluginSessionIdTests
{
    [Fact]
    public void Encode_prefixes_plugin_id()
    {
        Assert.Equal("plugin:com.acme.foo:1", PluginSessionId.Encode("com.acme.foo", "1"));
        Assert.True(PluginSessionId.TryStrip("plugin:com.acme.foo:1", out var raw));
        Assert.Equal("1", raw);
        Assert.False(PluginSessionId.TryStrip("builtin-session", out _));
    }

    [Fact]
    public void TryStrip_keeps_additional_colons_in_raw_session_id()
    {
        // 只在 plugin id 后的第一个冒号处切分，剩下的原文（哪怕还带冒号）完整返回。
        Assert.True(PluginSessionId.TryStrip("plugin:com.acme.foo:a:b:c", out var raw));
        Assert.Equal("a:b:c", raw);
    }

    [Theory]
    [InlineData("plugin:")]
    [InlineData("plugin:no-second-colon")]
    [InlineData("plugin::1")]
    [InlineData("plugin:com.acme.foo:")]
    [InlineData("")]
    public void TryStrip_rejects_malformed_ids(string sessionId)
    {
        Assert.False(PluginSessionId.TryStrip(sessionId, out var raw));
        Assert.Equal(string.Empty, raw);
    }
}
