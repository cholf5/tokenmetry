using System.IO.Compression;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 从 <c>.tkmplugin</c> 包装插件：
/// <list type="number">
/// <item>校验包结构（只读 zip/manifest，不加载程序集）；</item>
/// <item>解压到 <c>plugins/.staging/&lt;guid&gt;</c> 并再次校验落盘内容；</item>
/// <item>原子移动到 <c>plugins/&lt;id&gt;/&lt;version&gt;</c>（同版本旧目录先移开、失败回滚）；</item>
/// <item>删除同 id 的其他版本目录；</item>
/// <item>写 <c>plugins.json</c>（默认启用）并重载目录。</item>
/// </list>
/// 校验/解压失败不会触碰已有安装。
/// </summary>
public sealed class PluginInstaller : IPluginInstaller
{
    /// <summary>staging 子目录名（位于 <see cref="PluginPaths.PluginsDirectory"/> 下，目录扫描会忽略点目录）。</summary>
    internal const string StagingFolderName = ".staging";

    private readonly IPluginCatalog _catalog;
    private readonly PluginPaths _paths;
    private readonly IReadOnlyCollection<string> _reservedAgentNames;

    public PluginInstaller(
        IPluginCatalog catalog,
        PluginPaths paths,
        IReadOnlyCollection<string> reservedAgentNames)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(paths);

        _catalog = catalog;
        _paths = paths;
        _reservedAgentNames = reservedAgentNames?.ToArray() ?? [];

        // 上次安装中途退出/删除失败会在 plugins/.staging 留下解压目录或 .backup；
        // 启动装配时（构造即初始化）尽力清掉，避免无限堆积。已安装版本不在 .staging 内，不受影响。
        SweepStaleStaging(paths);
    }

    public PluginInstallPreview Preview(string packagePath)
    {
        // 冲突集合要覆盖当前已安装插件；目录实现是同步的，这里确保读到最新事实。
        _catalog.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();

        var peeked = PluginPackageValidator.TryReadManifest(packagePath);
        var reserved = BuildReservedNames(peeked?.Id);
        var validation = PluginPackageValidator.Validate(packagePath, reserved);

        if (validation.Manifest is null || validation.Errors.Count > 0)
        {
            throw new PluginInstallException(
                validation.Errors.Count > 0
                    ? validation.Errors.ToArray()
                    : ["package is not a valid plugin package"]);
        }

        var manifest = validation.Manifest;
        var warnings = new List<string>(validation.Warnings);

        var existing = _catalog.Installed.FirstOrDefault(
            installation => string.Equals(installation.Manifest.Id, manifest.Id, StringComparison.Ordinal));
        if (existing is not null
            && !string.Equals(existing.Manifest.DisplayName, manifest.DisplayName, StringComparison.Ordinal))
        {
            warnings.Add(
                $"displayName is changing from '{existing.Manifest.DisplayName}' to '{manifest.DisplayName}'; " +
                "historical facts remain under the old agent name");
        }

        return new PluginInstallPreview(manifest, warnings, packagePath);
    }

    public async Task<PluginInstallation> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = Preview(packagePath).Manifest;

        var staging = Path.Combine(
            _paths.PluginsDirectory, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var committed = false;
        try
        {
            Extract(packagePath, staging);

            var revalidated = PluginPackageValidator.ValidateExtractedDirectory(
                staging, BuildReservedNames(manifest.Id));
            if (revalidated.Manifest is null || revalidated.Errors.Count > 0)
            {
                throw new PluginInstallException(
                    revalidated.Errors.Count > 0
                        ? revalidated.Errors.ToArray()
                        : ["extracted package contents are not valid"]);
            }

            // 防御 TOCTOU：落盘 manifest 必须与校验时一致。
            if (!string.Equals(revalidated.Manifest.Id, manifest.Id, StringComparison.Ordinal)
                || !string.Equals(revalidated.Manifest.Version, manifest.Version, StringComparison.Ordinal))
            {
                throw new PluginInstallException("package contents changed during installation");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 设计 §6.3：先写状态、后动文件。状态写失败时直接中止，旧安装完全不受影响；
            // 状态已写但随后移动失败时，旧版本目录仍在，catalog 忽略不存在的目录，残留条目无害。
            await _catalog.SetEnabledAsync(manifest.Id, true, cancellationToken).ConfigureAwait(false);

            Commit(staging, manifest);
            committed = true;
        }
        catch (PluginInstallException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new PluginInstallException(
                $"failed to install plugin package '{packagePath}': {exception.Message}", exception);
        }
        finally
        {
            if (!committed)
            {
                TryDeleteDirectory(staging);
            }
        }

        await _catalog.ReloadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var installation in _catalog.Installed)
        {
            if (string.Equals(installation.Manifest.Id, manifest.Id, StringComparison.Ordinal))
            {
                return installation;
            }
        }

        // 理论上不可达（刚写入并重载）；构造等价描述兜底。
        var directory = Path.Combine(_paths.PluginsDirectory, manifest.Id, manifest.Version);
        return new PluginInstallation(
            manifest,
            directory,
            Path.Combine(directory, manifest.Icon),
            Enabled: true);
    }

    /// <summary>保留名 = 构造时传入的内置 Agent 名 + 当前已安装插件的显示名（排除正在升级的自身）。</summary>
    private HashSet<string> BuildReservedNames(string? installingPluginId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _reservedAgentNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        foreach (var installation in _catalog.Installed)
        {
            if (installingPluginId is not null
                && string.Equals(installation.Manifest.Id, installingPluginId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(installation.Manifest.DisplayName))
            {
                names.Add(installation.Manifest.DisplayName);
            }
        }

        return names;
    }

    private static void Extract(string packagePath, string stagingDirectory)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(packagePath);

        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0)
            {
                continue;
            }

            var relative = entry.FullName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                // 校验器已拒绝，这里是纵深防御。
                throw new PluginInstallException(
                    $"package entry '{entry.FullName}' escapes the staging directory");
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private void Commit(string staging, PluginManifest manifest)
    {
        var pluginDirectory = Path.Combine(_paths.PluginsDirectory, manifest.Id);
        Directory.CreateDirectory(pluginDirectory);

        var target = Path.Combine(pluginDirectory, manifest.Version);
        string? backup = null;

        if (Directory.Exists(target))
        {
            backup = Path.Combine(
                _paths.PluginsDirectory, StagingFolderName, Guid.NewGuid().ToString("N") + ".backup");
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            // 移动失败：恢复旧版本，保持「失败不留半个插件」。
            if (backup is not null && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        // 新版本已就位：清理同 id 的其他版本目录与旧版本备份（尽力而为，失败不破坏新安装）。
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(pluginDirectory))
            {
                if (!IsSamePath(directory, target))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        if (backup is not null)
        {
            TryDeleteDirectory(backup);
        }
    }

    /// <summary>
    /// 清理 <c>plugins/.staging</c> 下的遗留内容（上次安装中途退出 / 删除失败留下的解压目录与 .backup）。
    /// 只动点目录内的暂存物；任何 I/O / 权限失败都静默跳过，绝不影响启动，也不会触碰已安装版本。
    /// </summary>
    internal static void SweepStaleStaging(PluginPaths paths)
    {
        var staging = Path.Combine(paths.PluginsDirectory, StagingFolderName);
        try
        {
            if (!Directory.Exists(staging))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 残留不影响使用，下次启动再清。
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // staging 目录不可读就当没有残留。
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
