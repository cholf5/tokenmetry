using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CCUsage.Core.Models;

namespace CCUsage.App.Services;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public JsonAppSettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.SettingsFilePath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_paths.SettingsFilePath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, ct) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.AppDataDirectory);
        await using var stream = File.Create(_paths.SettingsFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, ct);
    }
}
