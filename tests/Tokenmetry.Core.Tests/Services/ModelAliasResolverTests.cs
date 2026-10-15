namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Services;

/// <summary>
/// ModelAliasResolver（模型别名映射解析）单元测试：恒等兜底、按原始名精确（Ordinal）命中、
/// 链式映射与环截断、自映射剔除、UpdateAliases 热替换。命中后返回规则里的规范名原文
/// （不改写大小写——规范名是显示名，大小写是用户的选择）；大小写变体是数据里独立的原始名，
/// 需要各自映射（候选清单会列出），不做隐式折叠。
/// </summary>
public class ModelAliasResolverTests
{
    [Fact]
    public void Resolve_WithoutAliases_ReturnsTrimmedInput()
    {
        var resolver = new ModelAliasResolver();

        resolver.Resolve("GLM-5.3-Flash").Should().Be("GLM-5.3-Flash");
        resolver.Resolve("  ark/glm-5.3-flash  ").Should().Be("ark/glm-5.3-flash");
    }

    [Fact]
    public void Resolve_WithNullOrWhitespace_ReturnsAsIs()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases([new KeyValuePair<string, string>("a", "b")]);

        resolver.Resolve(null!).Should().BeNullOrEmpty();
        resolver.Resolve(string.Empty).Should().BeEmpty();
        resolver.Resolve("   ").Should().Be("   ");
    }

    [Fact]
    public void Resolve_MatchesExactly_AndReturnsCanonicalOriginalCase()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("GLM-5.3-Flash", "glm-5.3-flash"),
            new("ark/glm-5.3-flash", "glm-5.3-flash"),
        ]);

        resolver.Resolve("GLM-5.3-Flash").Should().Be("glm-5.3-flash", "大小写变体映射正是核心场景");
        resolver.Resolve("ark/glm-5.3-flash").Should().Be("glm-5.3-flash");
        resolver.Resolve("GLM-5.3-FLASH").Should().Be("GLM-5.3-FLASH", "未映射的变体原样保留（精确匹配，不隐式折叠）");
        resolver.Resolve("glm-flash-latest").Should().Be("glm-flash-latest");
    }

    [Fact]
    public void Resolve_FollowsChainedAliases()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("glm-flash-latest", "ark/glm-5.3-flash"),
            new("ark/glm-5.3-flash", "glm-5.3-flash"),
        ]);

        resolver.Resolve("glm-flash-latest").Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void Resolve_BreaksAliasCycles_Deterministically()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("a", "b"),
            new("b", "a"),
        ]);

        resolver.Invoking(r => r.Resolve("a")).Should().NotThrow();
        resolver.Resolve("a").Should().BeOneOf("a", "b");
    }

    [Fact]
    public void UpdateAliases_DropsSelfMappingsAndBlankRules()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases(
        [
            new("same", "same"),
            new("  ", "x"),
            new("x", "   "),
            new("valid", "canonical"),
        ]);

        resolver.Resolve("same").Should().Be("same");
        resolver.Resolve("valid").Should().Be("canonical");
    }

    [Fact]
    public void UpdateAliases_ReplacesSnapshotEntirely()
    {
        var resolver = new ModelAliasResolver();
        resolver.UpdateAliases([new("old", "target")]);
        resolver.Resolve("old").Should().Be("target");

        resolver.UpdateAliases([new("new", "target2")]);

        resolver.Resolve("old").Should().Be("old", "热替换后旧规则不应残留");
        resolver.Resolve("new").Should().Be("target2");
    }
}
