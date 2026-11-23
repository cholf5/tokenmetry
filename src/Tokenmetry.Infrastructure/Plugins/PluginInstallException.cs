namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 插件安装失败。<see cref="Errors"/> 是面向用户的可读短句集合（包校验错误、IO 错误等），
/// 调用方应整段展示。消息不含插件记录内容（隐私红线）。
/// </summary>
public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public PluginInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public PluginInstallException(IReadOnlyList<string> errors)
        : base(errors.Count > 0 ? string.Join("; ", errors) : "plugin installation failed")
    {
        Errors = errors;
    }

    /// <summary>逐条可读错误，UI 可分行展示。</summary>
    public IReadOnlyList<string> Errors { get; }
}
