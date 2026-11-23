using System.IO.Compression;
using System.Text;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 一次插件包校验的结果。<see cref="Manifest"/> 在 manifest 无法读取/解析时为 <c>null</c>；
/// <see cref="Errors"/> 非空即不可安装；<see cref="Warnings"/> 不阻断安装，供确认界面展示。
/// </summary>
public sealed record PluginPackageValidation(
    PluginManifest? Manifest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 插件包（<c>.tkmplugin</c>，本质是 zip）的结构校验。只读取 zip 条目与 root <c>plugin.json</c>
/// 做静态检查，**绝不加载插件程序集**（入口类型由宿主在扫描时校验），因此安装期不会执行不可信代码。
/// <para>
/// 规则：root <c>plugin.json</c> 存在且 manifest 合法；入口程序集与 PNG 图标存在；拒绝路径穿越、
/// 绝对路径、符号链接；禁止携带契约程序集；包大小 ≤ 100 MB、文件数 ≤ 2000；显示名不得与保留
/// Agent 名冲突。
/// </para>
/// </summary>
public static class PluginPackageValidator
{
    /// <summary>包大小上限（压缩包与解压后总量都适用）。</summary>
    public const long MaxPackageSizeBytes = 100L * 1024 * 1024;

    /// <summary>包内文件数上限。</summary>
    public const int MaxFileCount = 2000;

    /// <summary>契约程序集名；插件包不得自带，否则类型身份冲突。</summary>
    public const string ContractAssemblyFileName = "Tokenmetry.Plugin.Abstractions.dll";

    private const long MaxManifestBytes = 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 校验一个插件包。返回错误列表而不是抛异常，方便安装确认 UI 一次性展示全部问题。
    /// <paramref name="reservedAgentNames"/> 是「不得占用的 Agent 显示名」集合（内置 Agent +
    /// 其他已安装插件）；比较大小写不敏感。
    /// </summary>
    public static PluginPackageValidation Validate(
        string packagePath,
        IReadOnlyCollection<string>? reservedAgentNames = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            errors.Add("package path is required");
            return new PluginPackageValidation(null, errors, warnings);
        }

        if (!File.Exists(packagePath))
        {
            errors.Add($"package file was not found: {packagePath}");
            return new PluginPackageValidation(null, errors, warnings);
        }

        long packageLength;
        try
        {
            packageLength = new FileInfo(packagePath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"package could not be read: {exception.Message}");
            return new PluginPackageValidation(null, errors, warnings);
        }

        if (packageLength > MaxPackageSizeBytes)
        {
            errors.Add(
                $"package is too large ({packageLength} bytes); the limit is {MaxPackageSizeBytes} bytes");
            return new PluginPackageValidation(null, errors, warnings);
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(packagePath);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            errors.Add(exception is InvalidDataException
                ? "package is not a valid zip archive"
                : $"package could not be read: {exception.Message}");
            return new PluginPackageValidation(null, errors, warnings);
        }

        using (archive)
        {
            return ValidateArchive(archive, reservedAgentNames, errors, warnings);
        }
    }

    /// <summary>
    /// 解压后的再校验：对 staging 目录做与压缩包等价的结构检查，并防御性地拒绝符号链接与
    /// 契约程序集。安装器解压后调用，确保落盘内容与校验过的包一致。
    /// </summary>
    internal static PluginPackageValidation ValidateExtractedDirectory(
        string directory,
        IReadOnlyCollection<string>? reservedAgentNames = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            errors.Add("extracted plugin directory was not found");
            return new PluginPackageValidation(null, errors, warnings);
        }

        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add("plugin.json was not found at the package root");
            return new PluginPackageValidation(null, errors, warnings);
        }

        PluginManifest? manifest;
        try
        {
            manifest = PluginJson.DeserializeManifest(File.ReadAllText(manifestPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            errors.Add("plugin.json is not valid JSON");
            return new PluginPackageValidation(null, errors, warnings);
        }

        if (manifest is null)
        {
            errors.Add("plugin.json is not valid JSON");
            return new PluginPackageValidation(null, errors, warnings);
        }

        errors.AddRange(PluginManifestValidator.Validate(manifest));
        ValidateManifestPaths(manifest, errors);

        if (IsSafeRelativePath(manifest.EntryAssembly)
            && !File.Exists(ResolveInside(root, manifest.EntryAssembly)))
        {
            errors.Add($"entry assembly '{manifest.EntryAssembly}' was not found in the package");
        }

        if (IsSafeRelativePath(manifest.Icon))
        {
            var iconPath = ResolveInside(root, manifest.Icon);
            if (!File.Exists(iconPath))
            {
                errors.Add($"icon '{manifest.Icon}' was not found in the package");
            }
            else if (!FileHasPngMagic(iconPath))
            {
                errors.Add($"icon '{manifest.Icon}' is not a valid PNG image");
            }
        }

        RejectContractAssemblyOnDisk(root, errors);
        RejectSymlinksOnDisk(root, errors);
        ValidateDisplayName(manifest, reservedAgentNames, errors);

        return new PluginPackageValidation(manifest, errors, warnings);
    }

    /// <summary>供安装器在解析阶段读取 manifest id（冲突集合需要排除升级中的插件自身）。</summary>
    internal static PluginManifest? TryReadManifest(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entry = FindRootManifest(archive);
            if (entry is null || entry.Length > MaxManifestBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return PluginJson.DeserializeManifest(reader.ReadToEnd());
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static PluginPackageValidation ValidateArchive(
        ZipArchive archive,
        IReadOnlyCollection<string>? reservedAgentNames,
        List<string> errors,
        List<string> warnings)
    {
        var fileCount = 0;
        long expandedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0)
            {
                // 目录条目；不落盘、不计数。
                continue;
            }

            fileCount++;
            expandedBytes += entry.Length;

            if (IsUnsafeEntryName(entry.FullName))
            {
                errors.Add($"package entry '{entry.FullName}' has an unsafe path");
            }

            if (IsSymlinkEntry(entry))
            {
                errors.Add($"package entry '{entry.FullName}' is a symbolic link");
            }

            if (string.Equals(entry.Name, ContractAssemblyFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"package must not contain {ContractAssemblyFileName}");
            }
        }

        if (fileCount > MaxFileCount)
        {
            errors.Add($"package contains too many files ({fileCount}); the limit is {MaxFileCount}");
        }

        if (expandedBytes > MaxPackageSizeBytes)
        {
            errors.Add(
                $"package expands to {expandedBytes} bytes; the limit is {MaxPackageSizeBytes} bytes");
        }

        var manifestEntry = FindRootManifest(archive);
        if (manifestEntry is null)
        {
            errors.Add("plugin.json was not found at the package root");
            return new PluginPackageValidation(null, errors, warnings);
        }

        var manifest = ReadManifest(manifestEntry, errors);
        if (manifest is null)
        {
            return new PluginPackageValidation(null, errors, warnings);
        }

        errors.AddRange(PluginManifestValidator.Validate(manifest));
        ValidateManifestPaths(manifest, errors);

        if (IsSafeRelativePath(manifest.EntryAssembly)
            && FindEntry(archive, manifest.EntryAssembly) is null)
        {
            errors.Add($"entry assembly '{manifest.EntryAssembly}' was not found in the package");
        }

        if (IsSafeRelativePath(manifest.Icon))
        {
            var iconEntry = FindEntry(archive, manifest.Icon);
            if (iconEntry is null)
            {
                errors.Add($"icon '{manifest.Icon}' was not found in the package");
            }
            else if (!EntryHasPngMagic(iconEntry))
            {
                errors.Add($"icon '{manifest.Icon}' is not a valid PNG image");
            }
        }

        ValidateDisplayName(manifest, reservedAgentNames, errors);

        return new PluginPackageValidation(manifest, errors, warnings);
    }

    private static void ValidateManifestPaths(PluginManifest manifest, List<string> errors)
    {
        // id / version 会拼进 plugins/<id>/<version>，必须是单段安全目录名。
        if (!IsSafeSegment(manifest.Id))
        {
            errors.Add("id must be a single safe directory name");
        }

        if (!IsSafeSegment(manifest.Version))
        {
            errors.Add("version must be a single safe directory name");
        }

        if (!IsSafeRelativePath(manifest.EntryAssembly))
        {
            errors.Add("entryAssembly must be a relative path inside the package");
        }

        if (!IsSafeRelativePath(manifest.Icon))
        {
            errors.Add("icon must be a relative path inside the package");
        }
    }

    private static void ValidateDisplayName(
        PluginManifest manifest,
        IReadOnlyCollection<string>? reservedAgentNames,
        List<string> errors)
    {
        if (reservedAgentNames is null || reservedAgentNames.Count == 0
            || string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            return;
        }

        foreach (var reserved in reservedAgentNames)
        {
            if (string.Equals(reserved, manifest.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"displayName '{manifest.DisplayName}' is already used by another agent or installed plugin");
                return;
            }
        }
    }

    private static PluginManifest? ReadManifest(ZipArchiveEntry entry, List<string> errors)
    {
        if (entry.Length > MaxManifestBytes)
        {
            errors.Add("plugin.json is too large");
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var manifest = PluginJson.DeserializeManifest(reader.ReadToEnd());
            if (manifest is null)
            {
                errors.Add("plugin.json is not valid JSON");
            }

            return manifest;
        }
        catch (System.Text.Json.JsonException)
        {
            errors.Add("plugin.json is not valid JSON");
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            errors.Add($"plugin.json could not be read: {exception.Message}");
            return null;
        }
    }

    private static ZipArchiveEntry? FindRootManifest(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length > 0
                && string.Equals(NormalizeEntryName(entry.FullName), "plugin.json", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string relativePath)
    {
        var wanted = NormalizeEntryName(relativePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length > 0
                && string.Equals(NormalizeEntryName(entry.FullName), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool EntryHasPngMagic(ZipArchiveEntry entry)
    {
        if (entry.Length < PngSignature.Length)
        {
            return false;
        }

        try
        {
            using var stream = entry.Open();
            Span<byte> header = stackalloc byte[8];
            var read = stream.ReadAtLeast(header, PngSignature.Length, throwOnEndOfStream: false);
            return read == PngSignature.Length && header.SequenceEqual(PngSignature);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool FileHasPngMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var read = stream.ReadAtLeast(header, PngSignature.Length, throwOnEndOfStream: false);
            return read == PngSignature.Length && header.SequenceEqual(PngSignature);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RejectContractAssemblyOnDisk(string root, List<string> errors)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, ContractAssemblyFileName, SearchOption.AllDirectories))
            {
                errors.Add($"package must not contain {ContractAssemblyFileName} (found '{file}')");
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"package contents could not be inspected: {exception.Message}");
        }
    }

    private static void RejectSymlinksOnDisk(string root, List<string> errors)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(
                        $"package entry '{Path.GetRelativePath(root, path)}' is a symbolic link");
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"package contents could not be inspected: {exception.Message}");
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, normalized));
    }

    internal static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    internal static bool IsUnsafeEntryName(string name)
    {
        var normalized = NormalizeEntryName(name);
        if (normalized.StartsWith('/'))
        {
            return true;
        }

        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            return true;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        var unixMode = (uint)entry.ExternalAttributes >> 16;
        return (unixMode & 0xF000) == 0xA000;
    }

    /// <summary>单段安全目录名：非空、非 <c>.</c>/<c>..</c>、非绝对、无路径分隔符/非法字符。</summary>
    internal static bool IsSafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || Path.IsPathRooted(value))
        {
            return false;
        }

        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return value.IndexOf('/') < 0 && value.IndexOf('\\') < 0;
    }

    /// <summary>安全的相对路径：非绝对、无空段、无 <c>.</c>/<c>..</c>。</summary>
    internal static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        foreach (var segment in NormalizeEntryName(value).Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}
