using System.Collections.Concurrent;

namespace Tokenmetry.Core.Models;

/// <summary>
/// 用量记录的低基数串驻留池：解析层逐条 new 出的重复串（Agent / Model / ProjectPath / Provider /
/// SourceFile）经 <see cref="UsageRecord"/> 写入口共享单一实例——历史几十万行时这几列的重复字符串是
/// 常驻内存的大头（同内容串每条记录各占一份）。高基数字段（SessionId / MessageId / RequestId）有意
/// 不驻留（几乎逐条唯一，驻留的字典开销净增内存）。
/// <para>与 <see cref="string.Intern(string)"/> 的区别：不进进程级驻留池、<b>按槽分预算</b>并有上限。
/// 分槽的理由：身份类字段（Agent / ProjectPath / Model / Provider）基数天然很小，而 SourceFile 的基数
/// 是 O(会话文件数)，与身份字段同量级使用年限下可到十万级——共用一个预算时高基数字段会把预算吃光，
/// 让后续出现的低基数字段一起退化成"不共享"。</para>
/// <para>触顶语义：该槽到达上限后新值直通（不再共享）、既有条目保留不淘汰——上限触达即基数异常，
/// 继续驻留无收益；分子**独立**，一个槽触顶不影响另一个槽。生产侧可用
/// <see cref="IdentityCount"/> / <see cref="SourceFileCount"/> 观察是否触顶。</para>
/// <para>线程安全（并发字典 + 原子计数）；语义 = 按内容共享实例，值语义不变。</para>
/// </summary>
public static class UsageStringPool
{
    /// <summary>身份类字段（Agent / ProjectPath / Model / Provider）驻留上限：这几列的真实基数
    /// 是"模型几百 / 项目几千"，三万余条已是异常信号。</summary>
    public const int IdentityCapacity = 32_768;

    /// <summary>来源文件路径（SourceFile）驻留上限：基数是 O(会话文件数)，与身份字段不是一个量级，
    /// 因此单列预算；重度用户三年十万级，仍留出余量。</summary>
    public const int SourceFileCapacity = 262_144;

    private static readonly Slot Identity = new(IdentityCapacity);
    private static readonly Slot SourceFiles = new(SourceFileCapacity);

    /// <summary>身份类槽的当前条目数（触达 <see cref="IdentityCapacity"/> 即该槽退化为直通）。</summary>
    public static int IdentityCount => Identity.Count;

    /// <summary>来源文件槽的当前条目数（触达 <see cref="SourceFileCapacity"/> 即该槽退化为直通）。</summary>
    public static int SourceFileCount => SourceFiles.Count;

    /// <summary>驻留身份类字段（Agent / ProjectPath / Model / Provider）。</summary>
    public static string Intern(string value) => Identity.Intern(value);

    /// <summary>驻留来源文件路径（SourceFile，独立预算）。</summary>
    public static string InternSourceFile(string value) => SourceFiles.Intern(value);

    /// <summary>测试注入口：生产代码只用上面的两个静态槽。</summary>
    internal static Slot CreateSlot(int capacity) => new(capacity);

    /// <summary>
    /// 一个有界驻留槽。<b>容量判定走原子计数而不是 <c>ConcurrentDictionary.Count</c></b>——后者的
    /// 实现要取全部桶锁再逐桶求和，挂在解析热路径上（每条记录 5 次 setter）会把并发解析串行化。
    /// </summary>
    internal sealed class Slot(int capacity)
    {
        private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal string Intern(string value)
        {
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (_pool.TryGetValue(value, out var shared))
            {
                return shared;
            }

            if (Volatile.Read(ref _count) >= capacity)
            {
                // 触顶：新值直通（不共享），既有条目照常命中（并发下允许轻微超出上限，无害）。
                return value;
            }

            if (_pool.TryAdd(value, value))
            {
                Interlocked.Increment(ref _count);
                return value;
            }

            // 并发同内容竞争：另一线程刚插入，取它的实例（本次未新增条目，不计数）。
            return _pool.TryGetValue(value, out var raced) ? raced : value;
        }
    }
}
