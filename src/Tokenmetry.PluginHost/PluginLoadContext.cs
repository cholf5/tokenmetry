using System.Reflection;
using System.Runtime.Loader;

namespace Tokenmetry.PluginHost;

/// <summary>
/// 单插件加载上下文。契约程序集强制从宿主默认上下文解析（返回 null），保证插件里的
/// <see cref="Tokenmetry.Plugin.Abstractions.IUsageSourcePlugin"/> 与宿主是同一类型身份；
/// 其余依赖交给 <see cref="AssemblyDependencyResolver"/> 按插件目录的 .deps.json 解析。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string ContractAssemblyName = "Tokenmetry.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 契约类型必须与宿主共享同一份程序集，否则 IUsageSourcePlugin 转换会失败。
        if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.Ordinal))
        {
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
