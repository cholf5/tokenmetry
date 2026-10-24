using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Services;

/// <summary>
/// 模型名 → 头像视觉（统计页 / 托盘弹层的目录联动入口）：经 <see cref="ModelCatalog"/>
/// 身份解析拿到条目——用户在数据与计价页给模型设置的头像（图标选择器四模式）随之在全应用
/// 展示；名字没关联进目录时回落 ModelVisualMap 名推断（统计行不该没有图标）。
/// </summary>
public sealed class ModelAvatarResolver(ModelCatalog catalog)
{
    public ModelAvatarVisual Resolve(string modelName) =>
        ModelAvatarVisual.FromDefinition(catalog.ResolveIdentity(modelName).Definition, modelName);
}
