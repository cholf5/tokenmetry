using System;
using System.Collections.Generic;
using Tokenmetry.Core.Abstractions;

namespace Tokenmetry.Core.Services;

/// <summary>
/// <see cref="IModelNameResolver"/> 的用户配置实现：持有 别名 → 规范名 字典（来自
/// AppSettings.ModelAliases，settings.json），供查询服务在模型维度聚合时把原始名归一。
/// <list type="bullet">
/// <item>匹配按原始名<b>精确（Ordinal）</b>进行——不做忽略大小写的折叠：忽略大小写既无法
/// 表达 "GLM-5.3-Flash → glm-5.3-flash" 这类恰为大小写变体的规则（字典键互相吞并、规范名
/// 自身会被其他变体的规则误伤），也是一种对未列出变体的隐式猜测；本地数据里出现过的每个
/// 原始名都会进设置页候选清单，逐一映射即可（对齐项目"宁可分开不猜"的口径纪律）。
/// 命中后返回规则里的规范名原文（显示用，不改写大小写）。</item>
/// <item>链式映射支持（a→b、b→c 时 a 归到 c）：规范名本身也可能是别名。以访问集合防环，
/// 环（a→b→a）在重复访问处截断（返回再次访问到的环入口名，绝不无限循环）——环本身是
/// 用户配置矛盾，任何确定性的落点都只是止损，设置页录入侧会劝阻。</item>
/// <item><see cref="UpdateAliases"/> 支持运行时重载（设置页改完即生效），线程安全：
/// 快照整体替换（读取侧无锁），与 PricingEngine.UpdateOverrides 同纪律。</item>
/// </list>
/// </summary>
public sealed class ModelAliasResolver : IModelNameResolver
{
    // 规范名命中后返回原文（不改大小写），匹配用 Ordinal（大小写敏感，见类型说明）。
    private volatile Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    /// <summary>用 映射规则 重载快照：空白键/值、键值相同的规则（自映射，无效果）直接剔除。</summary>
    public void UpdateAliases(IEnumerable<KeyValuePair<string, string>> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, target) in aliases)
        {
            var key = alias?.Trim() ?? string.Empty;
            var value = target?.Trim() ?? string.Empty;
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, value, StringComparison.Ordinal))
            {
                continue;
            }

            snapshot[key] = value;
        }

        _aliases = snapshot;
    }

    /// <inheritdoc />
    public string Resolve(string rawModelName)
    {
        if (string.IsNullOrWhiteSpace(rawModelName))
        {
            return rawModelName ?? string.Empty;
        }

        var current = rawModelName.Trim();
        var aliases = _aliases;
        if (aliases.Count == 0)
        {
            return current;
        }

        // 链式跟进 + 防环：a→b→c 解析到 c；a→b→a 在重复访问处截断（返回环入口名）。
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(current, out var next) && visited.Add(current))
        {
            current = next;
        }

        return current;
    }
}
