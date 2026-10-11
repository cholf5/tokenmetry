namespace CCUsage.Core.Models;

/// <summary>
/// 一次 Claude Code JSONL 扫描的结果：解析出的统一记录 + 诊断计数。
/// </summary>
public class ClaudeCodeScanResult
{
    /// <summary>本次扫描实际使用的日志根目录（已归一化）。</summary>
    public IReadOnlyList<string> Roots { get; set; } = new List<string>();

    /// <summary>去重后的统一 usage 记录（调用方视为只读）。</summary>
    public IReadOnlyList<UsageRecord> Records { get; set; } = new List<UsageRecord>();

    public ClaudeCodeScanDiagnostics Diagnostics { get; set; } = new();
}

/// <summary>
/// 扫描诊断计数，字段口径与 ccusage-tauri claude.rs 对齐。
/// </summary>
public class ClaudeCodeScanDiagnostics
{
    public ClaudeCodeScanStatus Status { get; set; }

    /// <summary>发现的 .jsonl 文件数。</summary>
    public int FilesDiscovered { get; set; }

    /// <summary>本次实际打开并解析的文件数（增量扫描时不含未变更文件）。</summary>
    public int FilesRead { get; set; }

    /// <summary>不含 usage 标记而跳过的行数（含打不开的文件，与 claude.rs 口径一致）。</summary>
    public int LinesSkipped { get; set; }

    /// <summary>含 usage 标记但不是合法 JSON 的行数。</summary>
    public int MalformedLines { get; set; }

    /// <summary>JSON 合法但被结构/语义校验拒绝的记录数。</summary>
    public int InvalidRecords { get; set; }

    /// <summary>消息级去重折叠掉的记录数（按当前累计记录集计算）。</summary>
    public int DuplicateRecords { get; set; }

    /// <summary>日志未内建 costUSD、需定价引擎计算的记录数（按当前结果集计算）。</summary>
    public int MissingCostRecords { get; set; }

    /// <summary>无模型名（&lt;synthetic&gt; 等）的记录数（按当前结果集计算）。</summary>
    public int UnknownModelRecords { get; set; }
}

/// <summary>扫描整体状态。</summary>
public enum ClaudeCodeScanStatus
{
    /// <summary>全部记录解析正常。</summary>
    Ok,

    /// <summary>有记录产出，但累计发现的文件中存在损坏行或无效记录（含本次未重新解析的历史坏文件）。</summary>
    Partial,

    /// <summary>没有发现任何 usage 记录。</summary>
    NoData,
}
