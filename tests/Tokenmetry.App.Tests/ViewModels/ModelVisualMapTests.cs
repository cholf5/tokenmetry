namespace Tokenmetry.App.Tests.ViewModels;

using Tokenmetry.App.ViewModels;

/// <summary>
/// ModelVisualMap（模型名 → 内置厂商图标）单元测试：主流厂商命中、OpenAI o 系列前缀判定的
/// 误命中防护、未识别落 generic。恒非 null——统计行不该没有图标。
/// </summary>
public class ModelVisualMapTests
{
    [Theory]
    [InlineData("claude-sonnet-4", "claude")]
    [InlineData("anthropic/claude-3.5-sonnet", "claude")]
    [InlineData("gpt-4o", "openai")]
    [InlineData("gpt-5.2", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("o4-mini-high", "openai")]
    [InlineData("openai/gpt-4.1", "openai")]
    [InlineData("codex-mini-latest", "openai")]
    [InlineData("gemini-2.5-pro", "gemini")]
    [InlineData("glm-5.3-flash", "glm")]
    [InlineData("ark/glm-5.3-flash", "glm")]
    [InlineData("chatglm-tiny", "glm")]
    [InlineData("deepseek-v3.2", "deepseek")]
    [InlineData("qwen3-max", "qwen")]
    [InlineData("kimi-k2", "kimi")]
    [InlineData("moonshot-v1", "kimi")]
    [InlineData("llama-4-maverick", "llama")]
    [InlineData("meta-llama/Llama-3.1-70B", "llama")]
    [InlineData("grok-4", "grok")]
    [InlineData("mistral-large", "mistral")]
    [InlineData("codestral-latest", "mistral")]
    [InlineData("doubao-1.5-pro", "doubao")]
    [InlineData("MiniMax-M2", "minimax")]
    [InlineData("command-r-plus", "cohere")]
    [InlineData("ernie-5.0", "ernie")]
    [InlineData("hunyuan-turbos", "hunyuan")]
    [InlineData("phi-4", "phi")]
    public void Resolve_DetectsMainstreamVendors(string modelName, string expectedVendor)
    {
        ModelVisualMap.Resolve(modelName).Vendor.Should().Be(expectedVendor);
    }

    [Theory]
    [InlineData("mor3-thing")]
    [InlineData("gto1-model")]
    [InlineData("我的自建模型")]
    [InlineData("")]
    public void Resolve_FallsBackToGenericForUnknownNames(string modelName)
    {
        var visual = ModelVisualMap.Resolve(modelName);

        visual.Vendor.Should().Be("generic");
        visual.Glyph.Should().Be('?');
    }

    [Fact]
    public void Resolve_AlwaysReturnsNonNullVisual()
    {
        ModelVisualMap.Resolve("claude-opus-4").Should().NotBeNull();
        ModelVisualMap.Resolve(null!).Vendor.Should().Be("generic", "空名也保持有图标（generic 兜底）");
    }

    [Fact]
    public void Vendors_EnumeratesEveryVendorTheStylesDefine()
    {
        // 伪类名单与 Style 定义一一对应（ModelChip 附加属性逐一 Set）；
        // generic 必须在名单尾部（未识别落点），且名单不含重复项。
        ModelVisualMap.Vendors.Should().ContainInOrder(
            "claude", "openai", "gemini", "glm", "deepseek", "qwen", "kimi", "llama",
            "grok", "mistral", "doubao", "minimax", "cohere", "ernie", "hunyuan", "phi", "generic");
        ModelVisualMap.Vendors.Should().OnlyHaveUniqueItems();
    }
}
