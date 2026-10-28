namespace Tokenmetry.Core.Tests.Models;

using Tokenmetry.Core.Models;

/// <summary>
/// UsageRecord 低基数字符串驻留（P0-3 内存收敛）：解析层逐条 new 的重复串（Agent / Model /
/// ProjectPath / Provider / SourceFile）跨记录共享单一实例；高基数字段（SessionId / MessageId）
/// 有意不驻留（几乎逐条唯一，驻留净增字典开销）。
/// </summary>
public class UsageRecordTests
{
    [Fact]
    public void Repeated_low_cardinality_strings_share_instances_across_records()
    {
        var first = new UsageRecord
        {
            Agent = new string("Claude Code"),
            Model = new string("claude-sonnet-4"),
            ProjectPath = new string("/work/alpha"),
            SourceFile = new string("/logs/projects/alpha/session-a.jsonl"),
        };
        var second = new UsageRecord
        {
            Agent = new string("Claude Code"),
            Model = new string("claude-sonnet-4"),
            ProjectPath = new string("/work/alpha"),
            SourceFile = new string("/logs/projects/alpha/session-a.jsonl"),
        };

        // 逐条 new 的等值串 → 驻留后同实例（历史百万行时重复串常驻内存的大头）。
        ReferenceEquals(first.Agent, second.Agent).Should().BeTrue();
        ReferenceEquals(first.Model, second.Model).Should().BeTrue();
        ReferenceEquals(first.ProjectPath, second.ProjectPath).Should().BeTrue();
        ReferenceEquals(first.SourceFile, second.SourceFile).Should().BeTrue();
        // 值语义不受驻留影响。
        second.Agent.Should().Be("Claude Code");
        second.Model.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public void High_cardinality_fields_are_not_interned()
    {
        var first = new UsageRecord { SessionId = new string("session-aaa") };
        var second = new UsageRecord { SessionId = new string("session-aaa") };

        // SessionId / MessageId 几乎逐条唯一：不驻留（等值即可，引用独立保留）。
        ReferenceEquals(first.SessionId, second.SessionId).Should().BeFalse();
        second.SessionId.Should().Be("session-aaa");
    }

    [Fact]
    public void Null_assignment_collapses_to_empty_and_distinct_values_stay_distinct()
    {
        var record = new UsageRecord { Agent = null!, Model = "model-a" };

        record.Agent.Should().BeEmpty();
        record.Model.Should().Be("model-a");

        var other = new UsageRecord { Model = "model-b" };
        ReferenceEquals(record.Model, other.Model).Should().BeFalse("不同内容不共享实例");
    }
}
