using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Services;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    // JSON 源生成上下文（Native AOT 适配，P-004）：反射式序列化在 AOT 下不可用（IL3050）。
    private static readonly AppSettingsJsonContext Context = AppSettingsJsonContext.Indented;

    private readonly AppPaths _paths;

    /// <summary>保存串行化（见 <see cref="SaveAsync"/> 并发纪律）：store 是 DI 单例，这一个 gate 管全部调用方。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

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
            return await JsonSerializer.DeserializeAsync(stream, Context.AppSettings, ct) ?? new AppSettings();
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
                Console.Error.WriteLine($"[Tokenmetry] settings.json 损坏且改名失败（{moveEx.Message}），回落默认设置");
                return new AppSettings();
            }

            Console.Error.WriteLine($"[Tokenmetry] settings.json 损坏（{ex.Message}），已改名 {badPath}，回落默认设置");
            return new AppSettings();
        }
    }

    /// <summary>A-7 原子保存：先写同目录临时文件再 <see cref="File.Move(string,string,bool)"/> 覆盖，
    /// 崩溃 / 断电不会留下半截 settings.json（配合 LoadAsync 自愈双保险）。
    /// <para><b>并发纪律</b>：调用方（设置页 / 数据计价页 / 时间预设 / 筛选持久化 / 后台周检 /
    /// 首关弹窗）各自拿着不同的私有 gate，管不住彼此——串行化和临时文件唯一性都必须在 store
    /// 这一层。旧实现把临时文件名写死为 <c>settings.json.tmp</c>：两个写者同时 <c>File.Create</c>
    /// （位于 Unix 上就是 open+O_TRUNC）时，先写方会继续在旧偏移写，落下带 \0 空洞的文件，
    /// 下次启动判 JSON 损坏 → 全部设置（含用户自定义价格/别名）回落默认。</para></summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_paths.AppDataDirectory);
            var tmpPath = $"{_paths.SettingsFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, Context.AppSettings, ct);
                }

                File.Move(tmpPath, _paths.SettingsFilePath, overwrite: true);
            }
            finally
            {
                // 序列化/改名失败（含取消）不留孤儿临时文件；删除失败不掩盖原始异常。
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
