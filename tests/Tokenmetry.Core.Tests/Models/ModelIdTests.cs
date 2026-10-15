namespace Tokenmetry.Core.Tests.Models;

using Tokenmetry.Core.Models;

/// <summary>
/// ModelId（模型 ID 约定）单元测试：折叠规则（点号 / 斜杠 / @ / 下划线 / 空格 → 连字符、大小写折叠）、
/// 连续与首尾连字符削平、合法性判定（大小写敏感段、单连字符分隔、无其它字符）。
/// </summary>
public class ModelIdTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", "gpt-5-6-sol")]
    [InlineData("openai/gpt-5.6-terra", "openai-gpt-5-6-terra")]
    [InlineData("kimi@k2@6", "kimi-k2-6")]
    [InlineData("GLM-5.3-Flash", "glm-5-3-flash")]
    [InlineData("  claude-opus-4-8  ", "claude-opus-4-8")]
    [InlineData("a__b", "a-b")]
    [InlineData("...trim-me...", "trim-me")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_FoldsSeparatorsAndCase(string input, string expected)
    {
        ModelId.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("glm-4.6", false)]
    [InlineData("gpt-5", true)]
    [InlineData("a", true)]
    [InlineData("glm-4-6", true)]
    [InlineData("", false)]
    [InlineData("GLM-4.6", false)]
    [InlineData("-glm", false)]
    [InlineData("glm-", false)]
    [InlineData("glm--4", false)]
    [InlineData("glm_4", false)]
    [InlineData("glm.4", false)]
    public void IsValid_EnforcesLowercaseHyphenConvention(string id, bool expected)
    {
        ModelId.IsValid(id).Should().Be(expected);
    }
}
