using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 宿主定位器：按 plugin-host/ 子目录 → 应用根目录可执行文件 → 根目录 .dll 的顺序查找，
/// 找不到返回 null（插件功能不可用，但应用正常启动）。
/// </summary>
public class PluginHostLocatorTests
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows()
        ? "Tokenmetry.PluginHost.exe"
        : "Tokenmetry.PluginHost";

    [Fact]
    public void Locate_returns_null_when_host_missing()
    {
        using var directory = new TempDirectory();

        Assert.Null(PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_finds_host_in_plugin_host_subdirectory()
    {
        using var directory = new TempDirectory();
        var hostPath = directory.WriteFile(Path.Combine("plugin-host", ExecutableName));

        Assert.Equal(hostPath, PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_finds_executable_in_root()
    {
        using var directory = new TempDirectory();
        var hostPath = directory.WriteFile(ExecutableName);

        Assert.Equal(hostPath, PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_finds_dll_in_root()
    {
        using var directory = new TempDirectory();
        var hostPath = directory.WriteFile("Tokenmetry.PluginHost.dll");

        Assert.Equal(hostPath, PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_prefers_plugin_host_subdirectory_over_root()
    {
        using var directory = new TempDirectory();
        directory.WriteFile("Tokenmetry.PluginHost.dll");
        var subdirectoryHost = directory.WriteFile(Path.Combine("plugin-host", ExecutableName));

        Assert.Equal(subdirectoryHost, PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_prefers_executable_over_dll()
    {
        using var directory = new TempDirectory();
        directory.WriteFile("Tokenmetry.PluginHost.dll");
        var executable = directory.WriteFile(ExecutableName);

        Assert.Equal(executable, PluginHostLocator.Locate(directory.Path));
    }

    [Fact]
    public void Locate_defaults_to_application_base_directory()
    {
        // 不传目录时以 AppContext.BaseDirectory 为根；缺文件时也是 null 而非抛异常。
        var result = PluginHostLocator.Locate();
        if (result is null)
        {
            return;
        }

        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var locatedDirectory = Path.GetDirectoryName(result);
        Assert.True(
            string.Equals(locatedDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                locatedDirectory,
                Path.Combine(baseDirectory, "plugin-host"),
                StringComparison.OrdinalIgnoreCase),
            $"unexpected host location '{result}'");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("tokenmetry-locator-").FullName;
        }

        public string Path { get; }

        public string WriteFile(string relativePath)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            var parent = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(fullPath, string.Empty);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
