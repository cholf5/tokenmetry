namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 插件宿主协议/进程失败。消息面向日志与数据源行，不含插件记录内容（隐私红线）。
/// </summary>
public sealed class PluginHostException : Exception
{
    public PluginHostException(string message)
        : base(message)
    {
    }

    public PluginHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
