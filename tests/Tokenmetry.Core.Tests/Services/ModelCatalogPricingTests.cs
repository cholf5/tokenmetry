namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// 定价引擎与模型目录的接线验收（I-035）：<b>用户在设置页做的关联对价格生效</b>——
/// 这是本次改动要解决的核心问题（此前别名只影响统计聚合，缺价模型永远无价）。
/// 覆盖：未关联 → 缺价（未知，不折 0）、关联后 → 按关联到的模型价计价、用户自建模型 → 用户价、
/// 用户目录条目热更新即时生效。
/// </summary>
public class ModelCatalogPricingTests
{
    private const string Glm46Id = "glm-4-6";
    private const string RawSpelling = "glm46-preview-build";

    private static TokenUsageDetail Tokens(long input, long output) => new()
    {
        InputTokens = input,
        OutputTokens = output,
    };

    [Fact]
    public void UnlinkedRawNameIsUnpricedAndRecordedForUserAction()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog());

        var result = engine.PriceTokens(RawSpelling, Tokens(1_000_000, 0));

        result.IsPriced.Should().BeFalse();
        result.Source.Should().Be(PricingSourceKind.Missing);
        result.CostUsd.Should().BeNull("缺价是未知，不是 0");
        engine.GetUnpricedModels().Should().ContainSingle(info => info.Model == RawSpelling);
    }

    [Fact]
    public void LinkingRawNameToKnownModelMakesItPriced()
    {
        var catalog = new ModelCatalog(aliases: new Dictionary<string, string>
        {
            [RawSpelling] = "glm-4.6",
        });
        var engine = new PricingEngine(catalog: catalog);

        var result = engine.PriceTokens(RawSpelling, Tokens(1_000_000, 0));

        result.IsPriced.Should().BeTrue("关联之后价格必须跟着关联到的模型走");
        result.Source.Should().Be(PricingSourceKind.Snapshot);
        result.CostUsd.Should().Be(
            catalog.FindById(Glm46Id)!.InputPerMillion,
            "1M input 按关联模型（glm-4.6）的 input 单价计");
    }

    [Fact]
    public void LinkingRawNameToKnownModelAlsoWorksThroughCandidatePlans()
    {
        var catalog = new ModelCatalog(aliases: new Dictionary<string, string>
        {
            [RawSpelling] = "glm-4.6",
        });
        var engine = new PricingEngine(catalog: catalog);

        // I-024 候选链路径（各源适配器的计价计划）同样吃关联：候选链按身份归一后逐个解析。
        var plan = new UsagePricingPlan([RawSpelling], PricingCandidateWalk.PerCandidateFull);
        var result = engine.PriceTokens(plan, RawSpelling, Tokens(1_000_000, 0));

        result.IsPriced.Should().BeTrue();
        result.CostUsd.Should().Be(catalog.FindById(Glm46Id)!.InputPerMillion);
    }

    [Fact]
    public void UserCreatedModelIsPricedAsUserOverride()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride
            {
                Id = "my-local-model",
                Vendor = "local",
                InputPerMillion = 3m,
                OutputPerMillion = 30m,
            },
        });
        var engine = new PricingEngine(catalog: catalog);

        var result = engine.PriceTokens("my-local-model", Tokens(1_000_000, 1_000_000));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(33m);
    }

    [Fact]
    public void UserDefinitionsUpdateTakesEffectWithoutRestart()
    {
        var catalog = new ModelCatalog();
        var engine = new PricingEngine(catalog: catalog);
        engine.PriceTokens(Glm46Id, Tokens(1_000_000, 0)).Source.Should().Be(PricingSourceKind.Snapshot);

        catalog.UpdateUserDefinitions(new[]
        {
            new ModelDefinitionOverride { Id = Glm46Id, InputPerMillion = 100m, OutputPerMillion = 200m },
        });

        var result = engine.PriceTokens(Glm46Id, Tokens(1_000_000, 0));
        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(100m);
    }

    [Fact]
    public void UserPriceOverrideBeatsRemoteAndSnapshot()
    {
        var remote = new RemotePricingSourceStub(new RemotePricingSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Entries =
            [
                new ModelPriceEntry
                {
                    ModelId = "glm-4.6",
                    Provider = "zai",
                    InputPerMillion = 7m,
                    OutputPerMillion = 8m,
                },
            ],
        });
        var catalog = new ModelCatalog(remote, new[]
        {
            new ModelDefinitionOverride { Id = Glm46Id, InputPerMillion = 1m, OutputPerMillion = 2m },
        });
        var engine = new PricingEngine(catalog: catalog);

        var result = engine.PriceTokens(Glm46Id, Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(1m);
    }

    private sealed class RemotePricingSourceStub : Tokenmetry.Core.Abstractions.IRemotePricingSource
    {
        public RemotePricingSourceStub(RemotePricingSnapshot? current) => Current = current;

        public RemotePricingSnapshot? Current { get; }
    }
}
