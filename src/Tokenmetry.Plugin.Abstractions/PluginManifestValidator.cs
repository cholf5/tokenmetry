namespace Tokenmetry.Plugin.Abstractions;

using System.Text.RegularExpressions;

/// <summary>
/// manifest 准入校验。返回的错误串是面向插件作者的可读短句，字段名与 plugin.json
/// 的 camelCase wire 名一致；空列表表示合法。<see cref="PluginApiVersion.Current"/>
/// 是唯一受支持的 API 版本。
/// </summary>
public static partial class PluginManifestValidator
{
    // id / agentId 会进入会话主键命名空间（plugin:&lt;id&gt;:）与磁盘路径；冒号、斜杠、
    // 空白等分隔符会导致不同插件编码出同一 key 或逸出路径，契约层先拒绝。
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static IReadOnlyList<string> Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<string>();

        if (manifest.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add("id is required");
        }
        else if (!IdentifierPattern().IsMatch(manifest.Id))
        {
            errors.Add("id must match ^[A-Za-z0-9][A-Za-z0-9._-]*$");
        }

        if (string.IsNullOrWhiteSpace(manifest.AgentId))
        {
            errors.Add("agentId is required");
        }
        else if (!IdentifierPattern().IsMatch(manifest.AgentId))
        {
            errors.Add("agentId must match ^[A-Za-z0-9][A-Za-z0-9._-]*$");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            errors.Add("displayName is required");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("version is required");
        }

        if (!string.Equals(
                manifest.ApiVersion,
                PluginApiVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            errors.Add($"apiVersion must be {PluginApiVersion.Current}");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add("entryAssembly is required");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            errors.Add("entryType is required");
        }

        if (string.IsNullOrWhiteSpace(manifest.Icon))
        {
            errors.Add("icon is required");
        }

        return errors;
    }
}
