namespace Tokenmetry.Core.Tests.Models;

using Tokenmetry.Core.Models;

/// <summary>
/// UsageStringPool 槽语义：按内容共享实例、触顶直通（既有条目保留、新值不再共享）、
/// 容量判定不依赖 <c>ConcurrentDictionary.Count</c>（挂热路径上会取全部桶锁）。
/// 槽通过注入口以小容量构造——生产槽是进程级静态的，不能靠填满它来验证触顶语义。
/// </summary>
public class UsageStringPoolTests
{
    [Fact]
    public void Equal_content_shares_a_single_instance()
    {
        var slot = UsageStringPool.CreateSlot(16);

        var first = slot.Intern(new string("claude-sonnet-4"));
        var second = slot.Intern(new string("claude-sonnet-4"));

        ReferenceEquals(first, second).Should().BeTrue();
        slot.Count.Should().Be(1, "同内容只占一条");
    }

    [Fact]
    public void Empty_string_collapses_to_the_canonical_instance_without_occupying_a_slot()
    {
        var slot = UsageStringPool.CreateSlot(16);

        slot.Intern(string.Empty).Should().BeSameAs(string.Empty);
        slot.Intern("").Should().BeSameAs(string.Empty);
        slot.Count.Should().Be(0, "空串不占预算");
    }

    [Fact]
    public void Slot_passes_through_new_values_once_capacity_is_reached()
    {
        var slot = UsageStringPool.CreateSlot(2);
        var alpha = slot.Intern(new string("alpha"));
        var beta = slot.Intern(new string("beta"));

        slot.Count.Should().Be(2);

        // 触顶：新值直通（不共享），既有条目照常命中——触达上限即基数异常，继续驻留无收益。
        var overflow = new string("gamma");
        ReferenceEquals(slot.Intern(overflow), overflow).Should().BeTrue("触顶后直通，原样返回");
        ReferenceEquals(slot.Intern(new string("gamma")), overflow).Should().BeFalse("触顶后不再共享");
        ReferenceEquals(slot.Intern(new string("alpha")), alpha).Should().BeTrue();
        ReferenceEquals(slot.Intern(new string("beta")), beta).Should().BeTrue();
        slot.Count.Should().Be(2, "触顶后不再新增条目");
    }

    [Fact]
    public void Source_file_field_occupies_its_own_budget()
    {
        // SourceFile 的基数是 O(会话文件数)，与身份类字段不是一个量级：两者独立记账，
        // 高基数字段吃光预算时不会把身份类字段一起拖成"不共享"。
        var identityBefore = UsageStringPool.IdentityCount;
        var sourceFileBefore = UsageStringPool.SourceFileCount;
        var uniquePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "session.jsonl");

        var record = new UsageRecord { SourceFile = new string(uniquePath), Agent = new string("Claude Code") };

        record.SourceFile.Should().Be(uniquePath);
        UsageStringPool.SourceFileCount.Should().BeGreaterThan(sourceFileBefore, "SourceFile 走文件槽");
        UsageStringPool.IdentityCount.Should().BeGreaterThanOrEqualTo(identityBefore, "身份类字段走身份槽");
    }

    [Fact]
    public void Concurrent_interns_converge_on_one_instance()
    {
        var slot = UsageStringPool.CreateSlot(64);
        var instances = new string[32];

        Parallel.For(0, instances.Length, index => instances[index] = slot.Intern(new string("model-x")));

        instances.Distinct(ReferenceEqualityComparer.Instance).Should().ContainSingle();
        slot.Count.Should().Be(1);
    }
}
