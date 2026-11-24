using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Cindy（桌面 Agent 宿主，https://github.com/makecindy/cindy）会话适配器接口。
/// 现阶段只覆盖 Pi runtime（Cindy 把 pi 的 <c>userData/pi-agent-home</c> 当自己的 agent home），
/// 出账 Agent 恒为 <c>"Cindy"</c>。另外两个 runtime 不在这里重复出账，由执行引擎自己的适配器负责：
/// Codex 的私有 home（<c>codex-home</c> / <c>codex-accounts</c>）作为 Codex 适配器的附加发现根入账，
/// Claude Code 的 transcript 在共享 <c>~/.claude</c> 下按 Claude Code 入账。Agent 维度始终是执行引擎；
/// "这次执行是谁发起的"由独立的 Host 维度表达（Cindy / 空 = 命令行），归属仲裁在
/// Infrastructure/Cindy/CindyHostAttribution。
/// </summary>
public interface ICindyAdapter
{
    /// <summary>扫描 Cindy 的 Pi agent home 并产出统一 usage 记录。</summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
