using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// 远程价格数据源（I-003，models.dev 客户端实现）。
/// 查询路径只读 <see cref="Current"/> 内存快照，绝不触发网络；网络只发生在实现类的后台刷新路径。
/// 尚未成功拉取过时为 null（计价回退到内置快照）。
/// </summary>
public interface IRemotePricingSource
{
    RemotePricingSnapshot? Current { get; }
}
