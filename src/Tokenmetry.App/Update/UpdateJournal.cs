using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tokenmetry.App.Update;

/// <summary>
/// 「有一次更新正在生效」的磁盘凭据。写它的是即将退出的旧进程，读它的是重启后的新进程：
/// 版本号对上 = 更新成功（清理备份、弹「已更新到 vX」）；对不上 = 替换失败（应用还是旧版本，
/// 备份还在，横幅提示用户可手动恢复）。之所以要落盘而不是内存标志：更新过程中进程会死，
/// 内存里的任何约定都跟着死。
/// </summary>
public sealed class UpdateJournalRecord
{
    /// <summary>这次更新要装上的版本（"vX.Y.Z"）。</summary>
    public string? PendingVersion { get; set; }

    /// <summary>旧版本备份路径（<c>&lt;目标&gt;.old</c>）；成功后被删，失败时是回滚用的唯一凭据。</summary>
    public string? BackupPath { get; set; }

    /// <summary>被替换的目标（exe / .app），失败提示里给用户看。</summary>
    public string? TargetPath { get; set; }

    /// <summary>发起时间（排查用；不参与判定）。</summary>
    public DateTime? StagedUtc { get; set; }
}

/// <summary>update-state.json 的 JSON 源生成上下文（AOT 纪律同 AppSettingsJsonContext：不许反射序列化）。</summary>
[JsonSerializable(typeof(UpdateJournalRecord))]
internal sealed partial class UpdateJournalJsonContext : JsonSerializerContext;

/// <summary>
/// 更新日志（<c>%LOCALAPPDATA%\Tokenmetry\update-state.json</c>）的读写。全部 best-effort：
/// 这个文件丢了最多让「更新成功」的提示不出现，绝不能因此挡住启动或更新。
/// </summary>
public sealed class UpdateJournal
{
    private static readonly UpdateJournalJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
    });

    private readonly string _path;

    public UpdateJournal(string path) => _path = path;

    public string FilePath => _path;

    /// <summary>读上一次更新留下的记录；没有 / 坏文件 / 读不动都返回 null。</summary>
    public UpdateJournalRecord? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, Context.UpdateJournalRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 更新日志读取失败（{_path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>写记录（替换前调用：进程如果死在替换中间，重启的实例靠它知道发生了什么）。</summary>
    public void Save(UpdateJournalRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再改名：半截 JSON 与「没有记录」一样没法解读，但会污染 next 判断。
            var temporary = _path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, record, Context.UpdateJournalRecord);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记不下来不阻止更新：最坏结果是成功提示不出现。
            Console.Error.WriteLine($"[Tokenmetry] 更新日志写入失败（{_path}）：{ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 更新日志清理失败（{_path}）：{ex.Message}");
        }
    }
}
