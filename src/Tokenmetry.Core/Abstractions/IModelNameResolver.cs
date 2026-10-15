namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// 模型名归一解析（用户别名映射）：把不同来源对同一模型的原始命名（大小写变体、厂商前缀如
/// <c>ark/glm-5.3-flash</c>、别名如 <c>glm-flash-latest</c>）归一到用户指定的规范名，使统计
/// 按规范名聚合。映射完全来自用户配置（AppSettings.ModelAliases），本应用不做任何内置自动归并
/// ——拼写近似合并是猜测，宁可分开。
/// </summary>
public interface IModelNameResolver
{
    /// <summary>原始模型名 → 规范名。无映射命中时原样返回（去首尾空白）；<paramref name="rawModelName"/>
    /// 为 null/空白时原样返回。匹配按原始名<b>精确（大小写敏感）</b>进行——大小写变体也是数据里独立的
    /// 原始名（候选清单会逐一列出），显式映射而非隐式折叠。</summary>
    string Resolve(string rawModelName);
}
