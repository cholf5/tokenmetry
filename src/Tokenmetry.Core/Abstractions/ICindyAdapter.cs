using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Cindy（桌面 Agent 宿主，https://github.com/makecindy/cindy）会话适配器接口。
/// 现阶段只覆盖 Pi runtime（Cindy 把 pi 的 <c>userData/pi-agent-home</c> 当自己的 agent home），
/// 出账 Agent 恒为 <c>"Cindy"</c>——不区分 runtime：Claude Code / Codex 作为 runtime 时的会话
/// 仍由各自的适配器按原样出账（避免共享 <c>~/.claude</c> 目录下的归属猜测与双算）。
/// </summary>
public interface ICindyAdapter
{
    /// <summary>扫描 Cindy 的 agent home 并产出统一 usage 记录。</summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
