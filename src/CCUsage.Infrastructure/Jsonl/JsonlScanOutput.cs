using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.Jsonl;

/// <summary>
/// 各 agent JSONL 直读适配器共享的解析产出；计数为单个文件口径，
/// 由 <see cref="IncrementalJsonlScanner"/> 折叠进全局诊断。
/// </summary>
public sealed record JsonlFileParseOutcome(
    List<UsageRecord> Records,
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords,
    bool Interrupted)
{
    public static JsonlFileParseOutcome Failure(int linesSkipped) =>
        new([], FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0, Interrupted: true);
}

/// <summary>一次增量扫描的产出：去重后的统一记录与全局诊断（结果模型由调用方包装）。</summary>
public sealed record JsonlScanOutput(List<UsageRecord> Records, JsonlScanDiagnostics Diagnostics);
