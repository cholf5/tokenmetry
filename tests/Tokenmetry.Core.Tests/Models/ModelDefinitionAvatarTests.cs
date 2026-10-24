using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.Core.Tests.Models;

/// <summary>
/// 模型头像（图标选择器四模式）的装订语义：用户显式模式整组覆盖导入层；Auto = 回落名推断；
/// 旧 settings.json 手写 LogoVendor 的 Auto 兼容语义在装订后保留（解析层据此呈现 Vendor 模式）；
/// 头像覆盖计入元数据覆盖（models.dev 刷新不得冲掉）。
/// </summary>
public class ModelDefinitionAvatarTests
{
    [Fact]
    public void Bind_ExplicitKind_OverridesImportedAndClearsOtherPayloads()
    {
        var user = new ModelDefinitionOverride
        {
            Id = "my-model",
            AvatarKind = ModelAvatarKind.Color,
            AvatarColor = "#4C8DFF",
        };

        var bound = user.Bind(new ModelDefinition { Id = "my-model", Vendor = "zai", LogoVendor = "glm" });

        bound.AvatarKind.Should().Be(ModelAvatarKind.Color);
        bound.AvatarColor.Should().Be("#4C8DFF");
        bound.AvatarLetter.Should().BeNull("非选中模式的载荷不落盘（无中间态）");
        bound.AvatarImage.Should().BeNull();
        bound.IsMetadataOverridden.Should().BeTrue("头像覆盖计入元数据覆盖，刷新不得冲掉");
    }

    [Fact]
    public void Bind_AutoKind_FallsBackToNameInferenceAndDropsStalePayloads()
    {
        var user = new ModelDefinitionOverride
        {
            Id = "my-model",
            AvatarKind = ModelAvatarKind.Auto,
            AvatarLetter = "M", // 手改 settings.json 的不一致状态：Auto + 载荷
        };

        var bound = user.Bind(null);

        bound.AvatarKind.Should().Be(ModelAvatarKind.Auto);
        bound.AvatarLetter.Should().BeNull("Auto 语义丢弃载荷，不做半渲染");
        bound.IsMetadataOverridden.Should().BeFalse();
    }

    [Fact]
    public void Bind_LegacyHandwrittenLogoVendor_PreservedThroughMerge()
    {
        // 旧字段兼容：Auto + LogoVendor（老 settings.json 手写）装订后 LogoVendor 保留，
        // AvatarKind 保持 Auto——呈现层的 Vendor 模式由解析层兼容语义给出。
        var user = new ModelDefinitionOverride { Id = "my-model", LogoVendor = "qwen" };

        var bound = user.Bind(new ModelDefinition { Id = "my-model" });

        bound.AvatarKind.Should().Be(ModelAvatarKind.Auto);
        bound.LogoVendor.Should().Be("qwen");
        bound.IsMetadataOverridden.Should().BeTrue("手写 LogoVendor 属元数据覆盖");
    }

    [Fact]
    public void Bind_LetterAndImageKinds_KeepRespectivePayloadOnly()
    {
        var letter = new ModelDefinitionOverride
        {
            Id = "my-model",
            AvatarKind = ModelAvatarKind.Letter,
            AvatarLetter = "M",
        }.Bind(null);
        letter.AvatarLetter.Should().Be("M");
        letter.AvatarColor.Should().BeNull();
        letter.AvatarImage.Should().BeNull();

        const string dataUri = "data:image/png;base64,aGVsbG8=";
        var image = new ModelDefinitionOverride
        {
            Id = "my-model",
            AvatarKind = ModelAvatarKind.Image,
            AvatarImage = dataUri,
        }.Bind(null);
        image.AvatarImage.Should().Be(dataUri);
        image.AvatarLetter.Should().BeNull();
    }

    [Fact]
    public void Bind_ScopedEntry_KeepsAvatarSemantics()
    {
        // P-001 T4 作用域条目走 Bind(null) 自建语义——头像字段同样跟随用户层。
        var user = new ModelDefinitionOverride
        {
            Id = "my-model",
            ProviderScope = "openrouter",
            AvatarKind = ModelAvatarKind.Letter,
            AvatarLetter = "G",
        };

        var bound = user.Bind(null);

        bound.AvatarKind.Should().Be(ModelAvatarKind.Letter);
        bound.AvatarLetter.Should().Be("G");
    }

    [Fact]
    public void Catalog_BoundDefinitionCarriesAvatarIntoDefinitionTable()
    {
        var catalog = new ModelCatalog(userDefinitions: [new ModelDefinitionOverride
        {
            Id = "glm-5-3-flash",
            AvatarKind = ModelAvatarKind.Letter,
            AvatarLetter = "G",
        }]);

        catalog.FindById("glm-5-3-flash")!.AvatarLetter.Should().Be("G");
    }
}
