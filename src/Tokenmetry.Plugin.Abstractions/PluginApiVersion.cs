namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 契约 API 版本。宿主只接受与 <see cref="Current"/> 相等的 manifest `apiVersion`；
/// v1 线内不递增，破坏性变更走新的程序集主版本线。
/// </summary>
public static class PluginApiVersion
{
    public const int Current = 1;
}
