using System.Globalization;
using System.IO.Compression;
using System.Text;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>生成合法/准合法 manifest 与测试插件包，全部落在系统临时目录，绝不碰真实 AppData。</summary>
internal static class ValidManifest
{
    /// <summary>PNG 魔数 + 少量填充；校验器只检查文件头 8 字节。</summary>
    public static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    public static string Json(
        string id = "com.acme.foo",
        string? agentId = null,
        string displayName = "Foo",
        string version = "1.0.0",
        string entryAssembly = "Foo.Plugin.dll",
        string icon = "icon.png")
        => PluginJson.SerializeManifest(new PluginManifest
        {
            SchemaVersion = 1,
            Id = id,
            AgentId = agentId ?? id,
            DisplayName = displayName,
            Version = version,
            ApiVersion = PluginApiVersion.Current.ToString(CultureInfo.InvariantCulture),
            EntryAssembly = entryAssembly,
            EntryType = "Foo.Plugin.FooUsagePlugin",
            Icon = icon,
            Author = "Test",
            Description = "Test plugin",
        });
}

/// <summary>一个临时 <c>.tkmplugin</c>（zip）文件，<see cref="Dispose"/> 时删除。</summary>
internal sealed class TestPackage : IDisposable
{
    /// <summary>Unix <c>S_IFLNK</c> 模式，用于构造 symlink 条目。</summary>
    public const int SymlinkAttributes = unchecked((int)0xA0000000u);

    private TestPackage(string path) => Path = path;

    public string Path { get; }

    /// <summary>文本条目包（manifest 错误原样保留）。</summary>
    public static TestPackage Create(params (string Name, string Text)[] entries)
        => CreateRaw(entries
            .Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Text), 0))
            .ToArray());

    /// <summary>一个结构完整的合法包：root plugin.json + 入口程序集 + PNG 图标。</summary>
    public static TestPackage CreateValid(
        string id = "com.acme.foo",
        string displayName = "Foo",
        string version = "1.0.0",
        string entryAssembly = "Foo.Plugin.dll",
        string icon = "icon.png")
        => CreateRaw(
            ("plugin.json", Encoding.UTF8.GetBytes(
                ValidManifest.Json(id, displayName: displayName, version: version, entryAssembly: entryAssembly, icon: icon)), 0),
            (entryAssembly, Encoding.UTF8.GetBytes("not a real assembly"), 0),
            (icon, ValidManifest.PngBytes, 0));

    /// <summary>按字节与外部属性建包，便于构造 symlink 等畸形条目。</summary>
    public static TestPackage CreateRaw(params (string Name, byte[] Bytes, int Attributes)[] entries)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tokenmetry-pkg-" + Guid.NewGuid().ToString("N") + ".tkmplugin");

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes, attributes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                entry.ExternalAttributes = attributes;
                using var entryStream = entry.Open();
                entryStream.Write(bytes);
            }
        }

        return new TestPackage(path);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
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

/// <summary>临时根目录下的一组 <see cref="PluginPaths"/>，用完整删除。</summary>
internal sealed class TestPluginPaths : IDisposable
{
    public TestPluginPaths()
    {
        Root = Directory.CreateTempSubdirectory("tokenmetry-plugin-paths-").FullName;
        Paths = new PluginPaths(
            System.IO.Path.Combine(Root, "plugins"),
            System.IO.Path.Combine(Root, "plugin-data"),
            System.IO.Path.Combine(Root, "plugins.json"));
    }

    public string Root { get; }

    public PluginPaths Paths { get; }

    /// <summary>在磁盘上伪造一个已安装版本目录（写出 plugin.json，可选入口程序集）。</summary>
    public string WriteInstalledPlugin(
        string id,
        string version,
        string? manifestJson = null,
        string entryAssembly = "Foo.Plugin.dll",
        bool writeEntryAssembly = true)
    {
        var directory = System.IO.Path.Combine(Paths.PluginsDirectory, id, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            System.IO.Path.Combine(directory, "plugin.json"),
            manifestJson ?? ValidManifest.Json(id: id, version: version, entryAssembly: entryAssembly));

        if (writeEntryAssembly)
        {
            File.WriteAllText(System.IO.Path.Combine(directory, entryAssembly), "not a real assembly");
        }

        return directory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
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
