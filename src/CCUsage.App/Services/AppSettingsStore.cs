using System;
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

    /// <summary>
    /// A-7 自愈加载：settings.json 损坏（坏 JSON / 半截写入 / IO 错误）不再让启动即崩——
    /// 坏文件改名 <c>.bad-时间戳</c> 留证后回落默认设置。改名而非删除：用户数据不静默销毁。
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.SettingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_paths.SettingsFilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, ct) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var badPath = $"{_paths.SettingsFilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_paths.SettingsFilePath, badPath);
            }
            catch (IOException moveEx)
            {
                // 改名失败（文件被占用等）不阻塞启动，坏文件留在原地，下次启动再试。
                Console.Error.WriteLine($"[ccusage] settings.json 损坏且改名失败（{moveEx.Message}），回落默认设置");
                return new AppSettings();
            }

            Console.Error.WriteLine($"[ccusage] settings.json 损坏（{ex.Message}），已改名 {badPath}，回落默认设置");
            return new AppSettings();
        }
    }

    /// <summary>A-7 原子保存：先写同目录临时文件再 <see cref="File.Move(string,string,bool)"/> 覆盖，
    /// 崩溃 / 断电不会留下半截 settings.json（配合 LoadAsync 自愈双保险）。</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.AppDataDirectory);
        var tmpPath = $"{_paths.SettingsFilePath}.tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, ct);
        }

        File.Move(tmpPath, _paths.SettingsFilePath, overwrite: true);
    }
}
