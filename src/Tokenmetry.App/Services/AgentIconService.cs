using System;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.App.Services;

/// <summary>
/// Agent 名 → 图标视觉的唯一入口（Task 7）：先按已安装插件的 <c>Manifest.DisplayName</c> 命中
/// （显示名就是写入 facts 的 Agent 键）给出插件自定义图标，未命中再委托 <see cref="AgentVisualMap"/>
/// 的内置品牌判定。命中但图标加载失败 / 无加载器时仍落 generic 槽位（散列字形兜底，不伪造品牌标）。
/// <para>
/// <see cref="BuiltInOnly"/> 无目录/加载器，恒委托内置映射——测试与预览的诚实默认（内置 Agent 的
/// <see cref="AgentVisual.CustomIcon"/> 恒为 null）。Resolve 每次读 <c>catalog.Installed</c>，
/// 目录重载后新安装的插件立即生效（v1 不做按版本缓存）。
/// </para>
/// </summary>
public sealed class AgentIconService
{
    private readonly IPluginCatalog? _catalog;
    private readonly IPluginIconLoader? _loader;

    /// <summary>无插件目录的纯内置实例（行 VM 的默认依赖）。</summary>
    public static AgentIconService BuiltInOnly { get; } = new(catalog: null, loader: null);

    public AgentIconService(IPluginCatalog? catalog, IPluginIconLoader? loader)
    {
        _catalog = catalog;
        _loader = loader;
    }

    /// <summary>解析 Agent 名；任何名字都返回非 null（未识别落 generic）。</summary>
    public AgentVisual Resolve(string agent)
    {
        var name = agent ?? string.Empty;

        if (_catalog is not null)
        {
            foreach (var installation in _catalog.Installed)
            {
                if (string.Equals(installation.Manifest.DisplayName, name, StringComparison.Ordinal))
                {
                    var icon = _loader?.Load(installation.IconPath);
                    return new AgentVisual("generic", AgentVisualMap.Glyph(name), icon);
                }
            }
        }

        return AgentVisualMap.Resolve(name);
    }
}
