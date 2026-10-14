namespace TokenPulse.Core.Models;

/// <summary>
/// I-001 兼容别名：Claude 适配器的扫描结果即共享 JSONL 扫描模型 <see cref="JsonlScanResult"/>，无额外成员。
/// 新代码（含 Codex 适配器、I-005 接线）直接使用共享模型。
/// </summary>
public class ClaudeCodeScanResult : JsonlScanResult
{
}

/// <summary>I-001 兼容别名，见 <see cref="JsonlScanDiagnostics"/>。</summary>
public class ClaudeCodeScanDiagnostics : JsonlScanDiagnostics
{
}

/// <summary>I-001 兼容别名：以 I-001 时代名称暴露 <see cref="JsonlScanStatus"/> 的枚举值。</summary>
public static class ClaudeCodeScanStatus
{
    public const JsonlScanStatus Ok = JsonlScanStatus.Ok;

    public const JsonlScanStatus Partial = JsonlScanStatus.Partial;

    public const JsonlScanStatus NoData = JsonlScanStatus.NoData;
}
