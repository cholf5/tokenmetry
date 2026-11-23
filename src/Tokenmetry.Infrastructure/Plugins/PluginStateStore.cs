using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary><c>plugins.json</c> 的落盘形状。启用状态按 plugin id 记录，是唯一的状态来源。</summary>
internal sealed class PluginStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, bool> Plugins { get; set; } = new(StringComparer.Ordinal);
}

// 源生成上下文：注册表也是生产代码里的 JSON，禁止反射序列化（AOT/单文件发布安全）。
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginStateDocument))]
internal sealed partial class PluginStateJsonContext : JsonSerializerContext;

/// <summary>
/// 插件启用状态注册表（<c>plugins.json</c>）。写入走临时文件 + 原子 <see cref="File.Move(string, string, bool)"/>；
/// 读取失败（缺失/损坏/权限）回落为「全部禁用」而不是抛异常，保证应用照常启动。
/// </summary>
internal sealed class PluginStateStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _path;

    public PluginStateStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("registry file path must be provided", nameof(path));
        }

        _path = path;
    }

    /// <summary>读取 id → enabled 映射。任何读取/解析失败都返回空映射（等价于全部禁用）。</summary>
    public IReadOnlyDictionary<string, bool> Load()
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);

        if (!File.Exists(_path))
        {
            return states;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var document = JsonSerializer.Deserialize(json, PluginStateJsonContext.Default.PluginStateDocument);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Plugins is null)
            {
                return states;
            }

            foreach (var (id, enabled) in document.Plugins)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    states[id] = enabled;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            states.Clear();
        }

        return states;
    }

    /// <summary>原子写入注册表：同目录临时文件写完后 <c>File.Move(overwrite: true)</c> 覆盖。</summary>
    public void Save(IReadOnlyDictionary<string, bool> states)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new PluginStateDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Plugins = new Dictionary<string, bool>(states, StringComparer.Ordinal),
        };
        var json = JsonSerializer.Serialize(document, PluginStateJsonContext.Default.PluginStateDocument);

        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
