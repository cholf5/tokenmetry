namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// manifest 准入校验。返回的错误串是面向插件作者的可读短句，字段名与 plugin.json
/// 的 camelCase wire 名一致；空列表表示合法。<see cref="PluginApiVersion.Current"/>
/// 是唯一受支持的 API 版本。
/// </summary>
public static class PluginManifestValidator
{
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

        if (string.IsNullOrWhiteSpace(manifest.AgentId))
        {
            errors.Add("agentId is required");
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
