namespace Tokenmetry.Core.Models;

/// <summary>
/// 一次本地 JSONL 会话日志扫描的结果（各 agent 适配器共享，I-001 Claude / I-004 Codex）：
/// 解析出的统一记录 + 诊断计数。
/// </summary>
public class JsonlScanResult
{
    /// <summary>本次扫描实际使用的日志根目录（已归一化）。</summary>
    public IReadOnlyList<string> Roots { get; set; } = new List<string>();

    /// <summary>去重后的统一 usage 记录（调用方视为只读）。</summary>
    public IReadOnlyList<UsageRecord> Records { get; set; } = new List<UsageRecord>();

    public JsonlScanDiagnostics Diagnostics { get; set; } = new();
}

/// <summary>
/// 扫描诊断计数，字段口径与 ccusage-tauri claude.rs / codex.rs 对齐，各 agent 适配器共享。
/// </summary>
public class JsonlScanDiagnostics
{
    public JsonlScanStatus Status { get; set; }

    /// <summary>发现的 .jsonl 文件数。</summary>
    public int FilesDiscovered { get; set; }

    /// <summary>本次实际打开并解析的文件数（增量扫描时不含未变更文件）。</summary>
    public int FilesRead { get; set; }

    /// <summary>不含 usage 标记而跳过的行数（含打不开的文件，与 claude.rs / codex.rs 口径一致）。</summary>
    public int LinesSkipped { get; set; }

    /// <summary>
    /// 含 usage 标记但解析失败的行数（Claude 口径：不是合法 JSON；
    /// Codex 口径：蓝本 serde 单次 from_str 同时做语法与类型校验，字段类型不符同样计入）。
    /// </summary>
    public int MalformedLines { get; set; }

    /// <summary>JSON 合法但被结构/语义校验拒绝的记录数。</summary>
    public int InvalidRecords { get; set; }

    /// <summary>消息级去重折叠掉的记录数（按当前累计记录集计算）。</summary>
    public int DuplicateRecords { get; set; }

    /// <summary>日志未内建成本、需定价引擎（I-003）计算的记录数（按当前结果集计算）。</summary>
    public int MissingCostRecords { get; set; }

    /// <summary>无模型名的记录数（按当前结果集计算）。</summary>
    public int UnknownModelRecords { get; set; }
}

/// <summary>扫描整体状态。</summary>
public enum JsonlScanStatus
{
    /// <summary>全部记录解析正常。</summary>
    Ok,

    /// <summary>有记录产出，但累计发现的文件中存在损坏行或无效记录（含本次未重新解析的历史坏文件）。</summary>
    Partial,

    /// <summary>没有发现任何 usage 记录。</summary>
    NoData,

    /// <summary>扫描委托抛出异常（A-6 逐源隔离）：该源本轮跳过出数，其余源不受影响。
    /// 该状态只由 LocalIngestionDataSource 的隔离 catch 登记，适配器自身不产出。</summary>
    Error,
}
