namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// ModelCatalog（I-035 模型目录）单元测试：三层合成（内置快照 → models.dev 远程 → 用户条目）、
/// 用户条目的逐字段覆盖与"价格覆盖不被刷新冲掉"、身份解析收敛（大小写 / 点号 / provider 前缀 /
/// -fast 采集标记 / 用户别名）。身份解析的判定口径与计价候选链同源——"能计价的名字"与
/// "能聚合的名字"必须是同一套判定。
/// </summary>
public class ModelCatalogTests
{
    /// <summary>可替换快照的远程源（模拟 models.dev 后台刷新换对象）。</summary>
    private sealed class FakeRemoteSource : IRemotePricingSource
    {
        public RemotePricingSnapshot? Current { get; set; }
    }

    private static RemotePricingSnapshot Remote(params ModelPriceEntry[] entries) => new()
    {
        FetchedAt = DateTimeOffset.UtcNow,
        Entries = entries,
    };

    private static ModelPriceEntry Entry(string provider, string modelId, decimal input, decimal output) => new()
    {
        ModelId = modelId,
        Provider = provider,
        InputPerMillion = input,
        OutputPerMillion = output,
        CacheWritePerMillion = input * ModelPriceEntry.DefaultCacheWriteMultiplier,
        CacheReadPerMillion = input * ModelPriceEntry.DefaultCacheReadMultiplier,
    };

    [Fact]
    public void Definitions_ComeFromBuiltinSnapshotWithNormalizedIds()
    {
        var catalog = new ModelCatalog();

        var glm = catalog.FindById("glm-4.6");

        glm.Should().NotBeNull();
        glm!.Id.Should().Be("glm-4-6");
        glm.Vendor.Should().Be("zai");
        glm.Origin.Should().Be(ModelDefinitionOrigin.Snapshot);
        glm.HasPrice.Should().BeTrue();
        glm.IsPriceOverridden.Should().BeFalse();
        // 展示键保留导入来源的原拼写（"zai/glm-4.6"），与计价对账的 matchedKey 同形。
        glm.SourceKey.Should().Be("zai/glm-4.6");
    }

    [Theory]
    [InlineData("glm-4.6")]
    [InlineData("GLM-4.6")]
    [InlineData("glm-4-6")]
    [InlineData("zai/glm-4.6")]
    [InlineData("GLM-4.6-fast")]
    public void ResolveIdentity_ConvergesKnownNameVariantsToSameId(string raw)
    {
        var catalog = new ModelCatalog();

        var identity = catalog.ResolveIdentity(raw);

        identity.Id.Should().Be("glm-4-6", $"{raw} 是已知模型的拼写变体");
        identity.IsLinked.Should().BeTrue();
        identity.Definition.Should().NotBeNull();
    }

    [Fact]
    public void ResolveIdentity_UnknownNameStaysUnlinkedAndKeepsSpelling()
    {
        var catalog = new ModelCatalog();

        var identity = catalog.ResolveIdentity("totally-unknown-model-x");

        identity.Kind.Should().Be(ModelMatchKind.None);
        identity.Id.Should().Be("totally-unknown-model-x");
        identity.Definition.Should().BeNull();
    }

    [Fact]
    public void ResolveIdentity_UserAliasLinksRawNameToModelId()
    {
        var catalog = new ModelCatalog(aliases: new Dictionary<string, string>
        {
            ["glm46-preview-build"] = "glm-4.6",
        });

        var identity = catalog.ResolveIdentity("glm46-preview-build");

        identity.Kind.Should().Be(ModelMatchKind.Alias);
        identity.Id.Should().Be("glm-4-6");
        identity.Definition.Should().NotBeNull();
    }

    [Fact]
    public void ResolveIdentity_AliasToUnknownNameKeepsAliasTarget()
    {
        var catalog = new ModelCatalog(aliases: new Dictionary<string, string>
        {
            ["weird-raw-name"] = "custom-thing",
        });

        var identity = catalog.ResolveIdentity("weird-raw-name");

        identity.Kind.Should().Be(ModelMatchKind.Alias);
        identity.Id.Should().Be("custom-thing");
        identity.Definition.Should().BeNull();
    }

    [Fact]
    public void ResolveIdentity_DatedVariantHitsFamilyRule()
    {
        var catalog = new ModelCatalog();

        // 带日期后缀的历史模型名：精确条目不存在，靠快照的家族模式命中。
        var identity = catalog.ResolveIdentity("claude-sonnet-4-5-20250929");

        identity.IsLinked.Should().BeTrue();
        identity.Kind.Should().BeOneOf(ModelMatchKind.Family, ModelMatchKind.Exact);
        identity.Id.Should().Be("claude-sonnet-4-5");
    }

    [Fact]
    public void UserOverride_ReplacesPriceButInheritsImportedMetadata()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride { Id = "glm-4.6", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        var definition = catalog.FindById("glm-4-6");

        definition!.HasPrice.Should().BeTrue();
        definition.IsPriceOverridden.Should().BeTrue();
        definition.InputPerMillion.Should().Be(1m);
        definition.OutputPerMillion.Should().Be(2m);
        // cache 价按覆盖后的 input 推导（不用导入层的 cache 价：同一行的单价来源必须可解释）。
        definition.CacheWritePerMillion.Should().Be(1.25m);
        definition.CacheReadPerMillion.Should().Be(0.1m);
        definition.Vendor.Should().Be("zai", "未覆盖厂商时沿用导入层");
        definition.Origin.Should().Be(ModelDefinitionOrigin.Snapshot, "条目的来源仍是导入层，只有价格被覆盖");
    }

    [Fact]
    public void UserCreatedModel_IsAddedAsUserOriginEntry()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride
            {
                Id = "my-local-model",
                Vendor = "local",
                DisplayName = "本地模型",
                InputPerMillion = 3m,
                OutputPerMillion = 4m,
            },
        });

        var definition = catalog.FindById("my-local-model");

        definition.Should().NotBeNull();
        definition!.Origin.Should().Be(ModelDefinitionOrigin.User);
        definition.Vendor.Should().Be("local");
        definition.DisplayLabel.Should().Be("本地模型");
        definition.HasPrice.Should().BeTrue();
    }

    [Fact]
    public void UserEntryWithoutPrice_IsRegisteredButProducesNoPriceEntry()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride { Id = "pending-model", Vendor = "local" },
        });

        catalog.FindById("pending-model").Should().NotBeNull();
        catalog.FindById("pending-model")!.HasPrice.Should().BeFalse();
        catalog.UserEntries.Should().BeEmpty("没有价就不参与计价，也绝不折 0");
    }

    [Fact]
    public void InvalidUserEntryIds_AreDropped()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride { Id = "bad#id", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        catalog.UserOverrides.Should().BeEmpty();
        catalog.FindById("bad#id").Should().BeNull();
    }

    [Fact]
    public void UserEntryIds_AreNormalizedToTheConvention()
    {
        var catalog = new ModelCatalog(userDefinitions: new[]
        {
            new ModelDefinitionOverride { Id = "GLM-4.6", InputPerMillion = 5m, OutputPerMillion = 6m },
        });

        catalog.FindById("glm-4.6")!.InputPerMillion.Should().Be(5m);
    }

    [Fact]
    public void RemoteSnapshotAddsEntriesAndOverridesSnapshotPrice()
    {
        var remote = new FakeRemoteSource { Current = Remote(Entry("zai", "glm-4.6", 7m, 8m)) };
        var catalog = new ModelCatalog(remote);

        var definition = catalog.FindById("glm-4-6");

        definition!.Origin.Should().Be(ModelDefinitionOrigin.Remote);
        definition.InputPerMillion.Should().Be(7m);
        definition.IsPriceOverridden.Should().BeFalse("远程数据不是用户覆盖，下次刷新照常更新");
    }

    [Fact]
    public void RemoteOverrideKeepsSnapshotFamilyPattern()
    {
        // 家族匹配规则只在快照的 "match" 字段上：远程按 ID 覆盖价格时必须把它带过来，
        // 否则 gpt-5.6 系的历史名 / 变体名在一轮刷新后会失去家族命中（对账回归）。
        var remote = new FakeRemoteSource { Current = Remote(Entry("openai", "gpt-5.6-sol", 9m, 90m)) };
        var catalog = new ModelCatalog(remote);

        var definition = catalog.FindById("gpt-5-6-sol");

        definition!.MatchPattern.Should().Be("gpt-5.6");
        catalog.ResolveIdentity("gpt-5.6-codex").Kind.Should().Be(ModelMatchKind.Family);
        catalog.ResolveIdentity("gpt-5.6-codex").Id.Should().Be("gpt-5-6-sol");
    }

    [Fact]
    public void RemoteRefreshPicksUpNewSnapshotObject()
    {
        var remote = new FakeRemoteSource();
        var catalog = new ModelCatalog(remote);
        catalog.FindById("remote-only-model").Should().BeNull();

        remote.Current = Remote(Entry("acme", "remote-only-model", 1m, 1m));

        catalog.FindById("remote-only-model").Should().NotBeNull(
            "远程快照换对象后目录必须感知（后台刷新不重启进程）");
    }

    [Fact]
    public void RemoteRefresh_DoesNotClobberOverriddenPrice()
    {
        var remote = new FakeRemoteSource { Current = Remote(Entry("zai", "glm-4.6", 7m, 8m)) };
        var catalog = new ModelCatalog(remote, new[]
        {
            new ModelDefinitionOverride { Id = "glm-4.6", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        var definition = catalog.FindById("glm-4-6");

        definition!.InputPerMillion.Should().Be(1m, "用户覆盖的价格是权威，刷新不得冲掉");
        definition.IsPriceOverridden.Should().BeTrue();
    }

    [Fact]
    public void UpdateUserDefinitionsTakesEffectImmediately()
    {
        var catalog = new ModelCatalog();
        catalog.FindById("glm-4-6")!.IsPriceOverridden.Should().BeFalse();

        catalog.UpdateUserDefinitions(new[]
        {
            new ModelDefinitionOverride { Id = "glm-4.6", InputPerMillion = 42m, OutputPerMillion = 43m },
        });

        catalog.FindById("glm-4-6")!.InputPerMillion.Should().Be(42m);
    }

    [Fact]
    public void MergeUserDefinitions_FoldsLegacyOverridesAndPrefersNewField()
    {
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride
        {
            Id = "glm-5-3-flash",
            InputPerMillion = 1m,
            OutputPerMillion = 2m,
        });
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "glm-5.3-flash", InputPerMillion = 9m, OutputPerMillion = 90m });
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "my-legacy-model", InputPerMillion = 3m, OutputPerMillion = 4m });
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "   ", InputPerMillion = 3m, OutputPerMillion = 4m });
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "negative-model", InputPerMillion = -1m, OutputPerMillion = 4m });

        var merged = ModelCatalog.MergeUserDefinitions(settings);

        merged.Should().HaveCount(2, "同 ID 以新字段为准，非法旧规则整条剔除（沿用 FromLegacy 口径）");
        merged.Single(definition => definition.Id == "glm-5-3-flash").InputPerMillion.Should().Be(1m, "新旧同 ID 时以新字段为准");
        merged.Single(definition => definition.Id == "my-legacy-model").InputPerMillion.Should().Be(3m, "旧字段折算并入，不丢老配置");
    }
}
