using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.JsonSource;

/// <summary>单文件的变更戳：路径 + UTC 修改时刻 + 长度（与 <see cref="Infrastructure.Jsonl.IncrementalJsonlScanner"/>
/// 的文件记账同口径；同 mtime 粒度内的原地等长改写不会被发现——共享记账的既有边界）。</summary>
public readonly record struct FileStamp(string Path, DateTime ModifiedUtc, long Length);

/// <summary>
/// 文件型数据源适配器的零变更轮记忆（I-042 同构，与 <see cref="Infrastructure.Jsonl.IncrementalJsonlScanner"/>
/// 的零变更缓存同口径）：全量读取前先对文件清单取戳（只 stat、不读内容），戳集与上轮成功读取完全一致
/// → 复用上轮记录（零读取零解析），FilesChanged=0 驱动采集管道短路；否则全量读取并落账。
/// <para>记忆化准入由调用方把守：仅"全部发现文件都成功读取"（FilesRead == FilesDiscovered）的轮次
/// 以本轮戳落账；有文件打不开的轮次以 <c>null</c> 戳落账（= 清空记忆），下一轮必重试整个清单——
/// 内容级计数（Malformed / Invalid / 内容跳过）随文件戳稳定，照常复用（与共享扫描器一致）。</para>
/// <para>文件戳只覆盖"文件内容"：解析结果还取决于解析期配置（作用域名、store 归属、扫描根等）的
/// 适配器，必须把配置签名一并折进指纹（<see cref="WithConfig"/>）——否则改配置而文件未动时会复用到
/// 旧归属的记录。</para>
/// <para>实例持有扫描记忆，非线程安全；随适配器以单例接入 App DI，并发由
/// LocalIngestionDataSource 的管道信号量串行化。</para>
/// </summary>
public sealed class FileStampMemo
{
    private FileStamp[]? _lastStamps;
    private IReadOnlyList<UsageRecord>? _lastRecords;
    private JsonlScanDiagnostics? _lastDiagnostics;

    /// <summary>配置戳的合成路径：以 <c>\0</c> 开头，排序必在一切真实路径之前（<see cref="WithConfig"/>
    /// 依赖这个序不变式保持戳集有序）。</summary>
    internal const string ConfigProbePath = "\u0000config";

    /// <summary>
    /// 把解析期配置签名折进戳集：在已排序的戳集头部插入一枚合成戳（内容 = 签名的 FNV-1a 64）。
    /// 签名不变 → 戳集与上轮一致（照常复用）；签名变化 → 必落空重扫。用于"同一批文件在不同配置下
    /// 产出不同记录"的适配器（如 pi-agent 的命名存储：store 改名或重叠根迁移时文件集可以完全不变，
    /// 但 Agent / Model / ProjectPath 归属全变）。<paramref name="stamps"/> 为 null（本轮 stat 失败）
    /// 时保持 null（不伪造可用指纹）。
    /// </summary>
    public static FileStamp[]? WithConfig(FileStamp[]? stamps, string configSignature)
    {
        if (stamps is null)
        {
            return null;
        }

        var combined = new FileStamp[stamps.Length + 1];
        combined[0] = new FileStamp(ConfigProbePath, DateTime.MinValue, Fnv1a64(configSignature));
        Array.Copy(stamps, 0, combined, 1, stamps.Length);
        return combined;
    }

    /// <summary>签名指纹：FNV-1a 64（确定性、无分配依赖；只用于等值比较，不作安全用途）。</summary>
    private static long Fnv1a64(string value)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// 对文件清单取戳（按路径排序，枚举序不影响复用判定）；任一文件不存在或 stat 失败返回 null
    /// （本轮保守按有变化处理）。
    /// </summary>
    public static FileStamp[]? Capture(IReadOnlyList<string> files)
    {
        var stamps = new FileStamp[files.Count];
        for (var index = 0; index < files.Count; index++)
        {
            var path = files[index];
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return null;
                }

                stamps[index] = new FileStamp(path, info.LastWriteTimeUtc, info.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        Array.Sort(stamps, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return stamps;
    }

    /// <summary>
    /// SQLite 账本库的指纹文件清单：主库 + <c>-wal</c> 边车——WAL 库的写入先落边车，主库 mtime 在
    /// checkpoint 前不变，只看主库会漏检；<c>-shm</c> 是读写共享的派生索引（读端也会触碰），不进指纹。
    /// </summary>
    public static IReadOnlyList<string> SqliteFingerprintFiles(IEnumerable<string> databases) =>
        databases.SelectMany(database => new[] { database, database + "-wal" }).ToList();

    /// <summary>
    /// 对文件清单取戳（按路径排序），<b>不存在的文件取哨兵戳</b>——缺失状态本身是指纹的一部分
    /// （SQLite 边车按需出现/消失，checkpoint 收尾会删边车）。清单里"本轮存在、上轮缺失"的文件
    /// 戳必不同；"持续缺失"即稳定指纹（调用方的读取失败准入把守兜底）。
    /// </summary>
    public static FileStamp[] CaptureWithOptional(IReadOnlyList<string> files)
    {
        var stamps = new FileStamp[files.Count];
        for (var index = 0; index < files.Count; index++)
        {
            var path = files[index];
            try
            {
                var info = new FileInfo(path);
                stamps[index] = info.Exists
                    ? new FileStamp(path, info.LastWriteTimeUtc, info.Length)
                    : new FileStamp(path, DateTime.MinValue, -1);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                stamps[index] = new FileStamp(path, DateTime.MinValue, -1);
            }
        }

        Array.Sort(stamps, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return stamps;
    }

    /// <summary>
    /// 零变更判定并产出复用结果：本轮戳集与上轮成功读取完全一致时返回 true，<paramref name="result"/>
    /// 为复用载荷——记录同实例（消费方视为只读），FilesRead=0 / FilesChanged=0（本轮无读取无解析事件），
    /// 累计四项诊断（Duplicate / Missing / Unknown）与 Status 沿用上轮，内容诊断（Malformed / Invalid /
    /// LinesSkipped）归零——与 <see cref="Infrastructure.Jsonl.IncrementalJsonlScanner"/> 的零变更轮口径
    /// 逐位一致（"本轮没有解析事件，就没有新的坏行"；累计语义只留在 Status 与四项累计计数里）。
    /// FilesDiscovered 取本轮清单数。戳为 null（本轮存在 stat 失败）或与记忆不一致（含空记忆）时返回
    /// false。<paramref name="previousDiagnostics"/> 为上一轮全量落账的诊断原样（供"记录复用了、但诊断要
    /// 自己重算"的双侧适配器使用，如 OpenClaw 的 SQLite 侧）。
    /// </summary>
    public bool TryReuse(FileStamp[]? stamps, int fileCount, IReadOnlyList<string> roots, out JsonlScanResult result)
        => TryReuse(stamps, fileCount, roots, out result, out _);

    /// <inheritdoc cref="TryReuse(FileStamp[], int, IReadOnlyList{string}, out JsonlScanResult)"/>
    public bool TryReuse(
        FileStamp[]? stamps,
        int fileCount,
        IReadOnlyList<string> roots,
        out JsonlScanResult result,
        out JsonlScanDiagnostics? previousDiagnostics)
    {
        previousDiagnostics = null;

        if (stamps is not { } current
            || _lastStamps is not { } last
            || _lastRecords is not { } records
            || _lastDiagnostics is not { } lastDiagnostics
            || current.Length != last.Length)
        {
            result = null!;
            return false;
        }

        for (var index = 0; index < current.Length; index++)
        {
            if (current[index] != last[index])
            {
                result = null!;
                return false;
            }
        }

        previousDiagnostics = lastDiagnostics;
        result = new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = new JsonlScanDiagnostics
            {
                Status = lastDiagnostics.Status,
                FilesDiscovered = fileCount,
                FilesRead = 0,
                MalformedLines = 0,
                InvalidRecords = 0,
                DuplicateRecords = lastDiagnostics.DuplicateRecords,
                MissingCostRecords = lastDiagnostics.MissingCostRecords,
                UnknownModelRecords = lastDiagnostics.UnknownModelRecords,
                FilesChanged = 0,
            },
        };
        return true;
    }

    /// <summary>
    /// 全量读取后落账。<paramref name="stamps"/> 为 null 时清空记忆（本轮存在读取失败 / stat 失败，
    /// 下一轮保守全量重试）；记录与累计诊断照存（仅在有戳时可能被复用）。
    /// </summary>
    public void Update(FileStamp[]? stamps, IReadOnlyList<UsageRecord> records, JsonlScanDiagnostics diagnostics)
    {
        _lastStamps = stamps;
        _lastRecords = records;
        _lastDiagnostics = diagnostics;
    }
}
