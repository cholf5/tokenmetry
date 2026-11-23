namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 在应用输出目录里定位插件宿主可执行文件。查找顺序：
/// <c>&lt;dir&gt;/plugin-host/Tokenmetry.PluginHost[.exe]</c> →
/// <c>&lt;dir&gt;/Tokenmetry.PluginHost[.exe]</c> →
/// <c>&lt;dir&gt;/Tokenmetry.PluginHost.dll</c>。返回 <c>null</c> 表示插件功能不可用，
/// 应用仍应正常启动。
/// </summary>
public static class PluginHostLocator
{
    private const string BaseName = "Tokenmetry.PluginHost";
    private const string AssemblyFileName = BaseName + ".dll";

    private static readonly string ExecutableFileName = OperatingSystem.IsWindows()
        ? BaseName + ".exe"
        : BaseName;

    public static string? Locate(string? directory = null)
    {
        var root = string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(root, "plugin-host", ExecutableFileName),
            Path.Combine(root, ExecutableFileName),
            Path.Combine(root, AssemblyFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
