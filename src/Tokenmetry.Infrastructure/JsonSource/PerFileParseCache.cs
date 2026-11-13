namespace Tokenmetry.Infrastructure.JsonSource;

/// <summary>逐文件探针结论：上轮产出可复用 / 需要重解析 / stat 失败（保守重解析且不落账）。</summary>
public enum PerFileProbe
{
    /// <summary>文件戳未变：取用上轮产出，不读盘不解析。</summary>
    Reused,

    /// <summary>新文件或文件戳变了：调用方解析并 <see cref="PerFileParseCache{T}.Store"/>。</summary>
    NeedsParse,

    /// <summary>stat 失败：调用方保守重解析，无论如何都不落账（下轮重试）。</summary>
    StatFailed,
}

/// <summary>
/// "整文件解析 + 全集合折叠"型适配器（Amp / Codebuff / Droid / Gemini CLI）共用的<b>逐文件</b>解析记忆：
/// 文件戳（路径 + mtime + 大小）没变的文件不重读不重解析，直接取用上轮产出。
/// <para>
/// 为什么必须逐文件：这类源的活跃文件（当前会话）每回合都在长，若用"整批戳集全等才复用"
/// （<see cref="FileStampMemo"/>）的口径，一份活跃文件就会把整批历史拉回全量重读——Cindy / pi-agent
/// 源踩过同一个坑（本机实测 399 MB/轮）。
/// </para>
/// <para>
/// 记账准入与共享扫描器一致：stat 失败、打不开、读取中途失败的文件不落账（调用方
/// <see cref="Forget"/>），下次扫描重试整个文件。文件级"存在解析错误"由调用方自行累计
/// （状态聚合要纳入未重解析的历史文件，见各适配器的 <c>_filesWithParseErrors</c>）。
/// </para>
/// <para>实例持有记忆，非线程安全；随适配器以单例接入 App DI，并发由采集管道信号量串行化。</para>
/// </summary>
public sealed class PerFileParseCache<T>
{
    private readonly Dictionary<string, (FileStamp Stamp, T Outcome)> _entries = new(StringComparer.Ordinal);

    /// <summary>当前记账的文件数。</summary>
    public int Count => _entries.Count;

    /// <summary>清理已消失文件的记账，返回清掉的条数（计入本轮账面变化数）。</summary>
    public int RemoveMissing(IReadOnlyCollection<string> files)
    {
        var current = files as ISet<string>
                      ?? new HashSet<string>(files, StringComparer.Ordinal);
        var removed = 0;
        foreach (var gone in _entries.Keys.Where(file => !current.Contains(file)).ToList())
        {
            _entries.Remove(gone);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// 单文件探针（只 stat，不读内容）。<see cref="PerFileProbe.Reused"/> 时 <paramref name="outcome"/>
    /// 为上轮产出；<see cref="PerFileProbe.NeedsParse"/> 时 <paramref name="stamp"/> 可供解析成功后
    /// <see cref="Store"/> 落账。
    /// </summary>
    public PerFileProbe Probe(string file, out FileStamp stamp, out T outcome)
    {
        outcome = default!;
        if (!FileStampMemo.TryCapture(file, out var current))
        {
            stamp = default;
            return PerFileProbe.StatFailed;
        }

        stamp = current;
        if (!_entries.TryGetValue(file, out var entry) || entry.Stamp != current)
        {
            return PerFileProbe.NeedsParse;
        }

        outcome = entry.Outcome;
        return PerFileProbe.Reused;
    }

    /// <summary>取已落账的产出（不 stat、不读盘）；本轮的或上轮的都行。未落账（本轮打不开）返回 false。</summary>
    public bool TryGetStored(string file, out T outcome)
    {
        if (_entries.TryGetValue(file, out var entry))
        {
            outcome = entry.Outcome;
            return true;
        }

        outcome = default!;
        return false;
    }

    /// <summary>落账：本轮解析成功（读取未失败）才调用。</summary>
    public void Store(string file, FileStamp stamp, T outcome) => _entries[file] = (stamp, outcome);

    /// <summary>清掉单文件记账（本轮读取失败 → 下轮重试整个文件）。</summary>
    public void Forget(string file) => _entries.Remove(file);

    /// <summary>清空全部记账（解析期配置变化时）。</summary>
    public void Clear() => _entries.Clear();
}
