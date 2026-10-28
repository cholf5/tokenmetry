using System.Collections.Concurrent;

namespace Tokenmetry.Core.Models;

/// <summary>
/// 用量记录的低基数串驻留池（P0-3 内存收敛）：解析层逐条 new 出的重复串（Agent / Model /
/// ProjectPath / Provider / SourceFile）经 <see cref="UsageRecord"/> 写入口共享单一实例——
/// 历史几十万行时这几列的重复字符串是常驻内存的大头（同内容串每条记录各占一份）。
/// 有意<b>不驻留</b>高基数字段（SessionId / MessageId：几乎逐条唯一，驻留的字典开销净增内存）。
/// 与 <see cref="string.Intern(string)"/> 的区别：不进进程级驻留池、有容量上限——超限后直通
/// （不再共享），防御异常源把池撑成无界；上限内的池不淘汰（达到上限即基数异常，继续驻留无收益）。
/// 线程安全（并发字典）；语义 = 按内容共享实例，值语义不变。
/// </summary>
public static class UsageStringPool
{
    /// <summary>驻留上限：低基数字段的真实基数（模型几百 / 项目几千 / 文件几万）远低于此值；
    /// 触达即视为基数异常，直通保底。</summary>
    public const int Capacity = 100_000;

    private static readonly ConcurrentDictionary<string, string> Pool = new(StringComparer.Ordinal);

    public static string Intern(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (Pool.Count >= Capacity)
        {
            return value;
        }

        return Pool.GetOrAdd(value, static shared => shared);
    }
}
