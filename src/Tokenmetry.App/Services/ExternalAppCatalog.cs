using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Services;

/// <summary>右键菜单里的外部应用分组：编辑器/IDE 与 Git 客户端。终端由 <see cref="TerminalLauncher"/> 单独命名。</summary>
public enum ExternalAppKind
{
    Editor,
    GitClient,
}

/// <summary>一台本机已探测到、可启动的外部应用（菜单项 = 「在 {DisplayName} 中打开」）。</summary>
public sealed record ExternalApp(
    string Id,
    string DisplayName,
    ExternalAppKind Kind,
    Func<string, bool> TryOpen,
    string? IconSource = null);

/// <summary>
/// 外部应用目录：数据驱动 + 本机探测，只在菜单里放**真的装了的应用**（同 dsh-plugin-file-actions
/// 的「官方探测 ∩ 本机解析」原则，避免菜单里有、点了却报错）。
///
/// 每个应用声明各平台的定位方式（macOS `.app` bundle 目录、Windows 已知安装路径/PATH/JetBrains
/// 版本目录扫描、Linux PATH 与 JetBrains Toolbox 脚本），探测只做文件系统查询，不启动进程；
/// 启动计划（<see cref="AppLaunchPlan"/>）与执行分离，测试可全平台注入假 probe 断言。
/// 图标源随入口一起解析（macOS = `.app` 路径），由 <see cref="AppIconProvider"/> 在开菜单时提取；
/// 提取失败菜单保持纯文字。Fork / SourceTree / GitHub Desktop 等 Git GUI 与编辑器同一机制；
/// 新应用只需加一条目录行。
/// </summary>
public static class ExternalAppCatalog
{
    /// <summary>探测当前平台已安装的外部应用（菜单按 Editor → GitClient 分组）。</summary>
    public static IReadOnlyList<ExternalApp> Detect()
    {
        var probe = new SystemExternalAppProbe();
        var entries = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? BuildWindowsEntries(probe)
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? BuildMacEntries(probe)
                : BuildLinuxEntries(probe);

        return entries
            .Select(entry => new ExternalApp(
                entry.Id,
                entry.DisplayName,
                entry.Kind,
                directory => AppLaunchProcess.TryStart(entry.PlanFor(directory)),
                entry.IconSource))
            .ToList();
    }

    internal sealed record ExternalAppEntry(
        string Id,
        string DisplayName,
        ExternalAppKind Kind,
        Func<string, AppLaunchPlan> PlanFor,
        string? IconSource);

    private sealed record LaunchTarget(
        string FileName,
        IReadOnlyList<string> PrefixArguments,
        string? IconSource);

    /// <summary>
    /// 一条目录记录：macOS bundle 名、Windows 可执行文件名/已知路径/JetBrains 产品名、
    /// Linux 命令与是否走 JetBrains Toolbox 脚本。空值表示该平台不支持或不需要。
    /// </summary>
    private sealed record CatalogApp(
        string Id,
        string DisplayName,
        ExternalAppKind Kind,
        string[] MacBundles,
        string WindowsExe,
        string[] WindowsKnownPaths,
        string? WindowsJetBrainsProduct,
        string? LinuxCommand,
        bool LinuxJetBrainsToolbox);

    /// <summary>
    /// 目录顺序即菜单内同组顺序。macOS 的 bundle 名覆盖直装与 JetBrains Toolbox 常见拼写；
    /// 重命名或移出 /Applications、~/Applications 的安装不在探测范围（同 dsh 目录的已知限制）。
    /// </summary>
    private static readonly CatalogApp[] Catalog =
    [
        new("vscode", "VS Code", ExternalAppKind.Editor,
            ["Visual Studio Code.app"], "Code.exe",
            ["$LOCALAPPDATA/Programs/Microsoft VS Code/Code.exe", "$PROGRAMFILES/Microsoft VS Code/Code.exe"],
            null, "code", false),
        new("cursor", "Cursor", ExternalAppKind.Editor,
            ["Cursor.app"], "Cursor.exe",
            ["$LOCALAPPDATA/Programs/cursor/Cursor.exe"],
            null, "cursor", false),
        new("intellij", "IntelliJ IDEA", ExternalAppKind.Editor,
            ["IntelliJ IDEA.app", "IntelliJ IDEA Ultimate.app", "IntelliJ IDEA CE.app"],
            "idea64.exe", [], "IntelliJ IDEA", "idea", true),
        new("pycharm", "PyCharm", ExternalAppKind.Editor,
            ["PyCharm.app", "PyCharm Professional.app", "PyCharm CE.app", "PyCharm Community.app"],
            "pycharm64.exe", [], "PyCharm", "pycharm", true),
        new("webstorm", "WebStorm", ExternalAppKind.Editor,
            ["WebStorm.app"], "webstorm64.exe", [], "WebStorm", "webstorm", true),
        new("goland", "GoLand", ExternalAppKind.Editor,
            ["GoLand.app"], "goland64.exe", [], "GoLand", "goland", true),
        new("phpstorm", "PhpStorm", ExternalAppKind.Editor,
            ["PhpStorm.app"], "phpstorm64.exe", [], "PhpStorm", "phpstorm", true),
        new("rustrover", "RustRover", ExternalAppKind.Editor,
            ["RustRover.app"], "rustrover64.exe", [], "RustRover", "rustrover", true),
        new("rider", "Rider", ExternalAppKind.Editor,
            ["Rider.app", "JetBrains Rider.app"], "rider64.exe", [], "Rider", "rider", true),
        new("sublime", "Sublime Text", ExternalAppKind.Editor,
            ["Sublime Text.app"], "sublime_text.exe",
            ["$PROGRAMFILES/Sublime Text/sublime_text.exe"],
            null, "subl", false),
        new("zed", "Zed", ExternalAppKind.Editor,
            ["Zed.app", "Zed Preview.app"], "", [], null, "zed", false),
        new("fork", "Fork", ExternalAppKind.GitClient,
            ["Fork.app"], "Fork.exe",
            ["$LOCALAPPDATA/Fork/Fork.exe", "$PROGRAMFILES/Fork/Fork.exe"],
            null, null, false),
        new("githubdesktop", "GitHub Desktop", ExternalAppKind.GitClient,
            ["GitHub Desktop.app"], "", [], null, null, false),
        new("sourcetree", "Sourcetree", ExternalAppKind.GitClient,
            ["Sourcetree.app"], "SourceTree.exe",
            ["$LOCALAPPDATA/SourceTree/SourceTree.exe", "$PROGRAMFILES/Atlassian/SourceTree/SourceTree.exe"],
            null, null, false),
        new("sublimemerge", "Sublime Merge", ExternalAppKind.GitClient,
            ["Sublime Merge.app"], "sublime_merge.exe",
            ["$PROGRAMFILES/Sublime Merge/sublime_merge.exe"],
            null, "smerge", false),
    ];

    internal static IReadOnlyList<ExternalAppEntry> BuildMacEntries(IExternalAppProbe probe) =>
        Catalog
            .Select(app => MacTarget(probe, app) is { } target ? Entry(app, target) : null)
            .Where(entry => entry is not null)
            .Cast<ExternalAppEntry>()
            .ToList();

    internal static IReadOnlyList<ExternalAppEntry> BuildWindowsEntries(IExternalAppProbe probe) =>
        Catalog
            .Select(app => WindowsTarget(probe, app) is { } target ? Entry(app, target) : null)
            .Where(entry => entry is not null)
            .Cast<ExternalAppEntry>()
            .ToList();

    internal static IReadOnlyList<ExternalAppEntry> BuildLinuxEntries(IExternalAppProbe probe) =>
        Catalog
            .Select(app => LinuxTarget(probe, app) is { } target ? Entry(app, target) : null)
            .Where(entry => entry is not null)
            .Cast<ExternalAppEntry>()
            .ToList();

    private static ExternalAppEntry Entry(CatalogApp app, LaunchTarget target) =>
        new(
            app.Id,
            app.DisplayName,
            app.Kind,
            directory => new AppLaunchPlan(
                target.FileName,
                [.. target.PrefixArguments, directory],
                directory),
            target.IconSource);

    private static LaunchTarget? MacTarget(IExternalAppProbe probe, CatalogApp app)
    {
        foreach (var bundleName in app.MacBundles)
        {
            var bundle = FindMacBundle(probe, bundleName);
            if (bundle is not null)
            {
                return new LaunchTarget("open", ["-a", bundle], bundle);
            }
        }

        return null;
    }

    private static string? FindMacBundle(IExternalAppProbe probe, string bundleName)
    {
        var system = Path.Combine("/Applications", bundleName);
        if (probe.DirectoryExists(system))
        {
            return system;
        }

        if (probe.UserProfile is { Length: > 0 } profile)
        {
            var user = Path.Combine(profile, "Applications", bundleName);
            if (probe.DirectoryExists(user))
            {
                return user;
            }
        }

        return null;
    }

    private static LaunchTarget? WindowsTarget(IExternalAppProbe probe, CatalogApp app)
    {
        if (app.Id == "githubdesktop")
        {
            return GitHubDesktopTarget(probe);
        }

        if (string.IsNullOrEmpty(app.WindowsExe))
        {
            return null;
        }

        foreach (var path in ExpandWindowsPaths(probe, app.WindowsKnownPaths))
        {
            if (probe.FileExists(path))
            {
                return new LaunchTarget(path, [], path);
            }
        }

        if (app.WindowsJetBrainsProduct is { } product
            && FindJetBrainsWindows(probe, product, app.WindowsExe, app.LinuxCommand) is { } jetBrains)
        {
            return WindowsCommandTarget(jetBrains);
        }

        if (app.LinuxCommand is { Length: > 0 } command
            && probe.ResolveCommand(command) is { } resolved)
        {
            return WindowsCommandTarget(resolved);
        }

        return null;
    }

    private static LaunchTarget? LinuxTarget(IExternalAppProbe probe, CatalogApp app)
    {
        if (app.LinuxCommand is not { Length: > 0 } command)
        {
            return null;
        }

        if (probe.ResolveCommand(command) is { } resolved)
        {
            return new LaunchTarget(resolved, [], resolved);
        }

        if (app.LinuxJetBrainsToolbox && probe.Home is { Length: > 0 } home)
        {
            var script = ToolboxScript(home, command);
            if (probe.FileExists(script))
            {
                return new LaunchTarget(script, [], script);
            }
        }

        return null;
    }

    private static LaunchTarget? GitHubDesktopTarget(IExternalAppProbe probe)
    {
        if (probe.LocalAppData is not { Length: > 0 } localAppData)
        {
            return null;
        }

        var root = Path.Combine(localAppData, "GitHubDesktop");
        var direct = Path.Combine(root, "GitHubDesktop.exe");
        if (probe.FileExists(direct))
        {
            return new LaunchTarget(direct, [], direct);
        }

        // 安装器把可执行文件放在 app-<version>\ 下：取版本目录序数降序的第一份。
        var versioned = probe.ListDirectories(root)
            .Where(directory => Path.GetFileName(directory).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in versioned)
        {
            var executable = Path.Combine(directory, "GitHubDesktop.exe");
            if (probe.FileExists(executable))
            {
                return new LaunchTarget(executable, [], executable);
            }
        }

        return null;
    }

    /// <summary>
    /// Windows 上 find 解析到的可能是 PATH 里的 .cmd/.bat 垫片（如 VS Code 的 code.cmd）；
    /// 这类必须经 cmd.exe 执行，直接 CreateProcess 会失败。
    /// </summary>
    private static LaunchTarget WindowsCommandTarget(string path)
    {
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchTarget("cmd.exe", ["/c", path], null);
        }

        return new LaunchTarget(path, [], path);
    }

    private static IEnumerable<string> ExpandWindowsPaths(IExternalAppProbe probe, IEnumerable<string> templates)
    {
        foreach (var template in templates)
        {
            var path = template;
            if (path.StartsWith("$LOCALAPPDATA/", StringComparison.Ordinal) && probe.LocalAppData is { Length: > 0 } local)
            {
                path = Path.Combine(
                    local,
                    path["$LOCALAPPDATA/".Length..].Replace('/', Path.DirectorySeparatorChar));
            }
            else if (path.StartsWith("$PROGRAMFILES/", StringComparison.Ordinal) && probe.ProgramFiles is { Length: > 0 } programFiles)
            {
                path = Path.Combine(
                    programFiles,
                    path["$PROGRAMFILES/".Length..].Replace('/', Path.DirectorySeparatorChar));
            }

            yield return path.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    private static string? FindJetBrainsWindows(
        IExternalAppProbe probe,
        string product,
        string executableName,
        string? toolCommand)
    {
        var roots = new List<string>();
        if (probe.LocalAppData is { Length: > 0 } localAppData)
        {
            roots.Add(Path.Combine(localAppData, "Programs"));
        }

        if (probe.ProgramFiles is { Length: > 0 } programFiles)
        {
            roots.Add(Path.Combine(programFiles, "JetBrains"));
        }

        foreach (var root in roots)
        {
            var candidates = probe.ListDirectories(root)
                .Where(directory => Path.GetFileName(directory).StartsWith(product, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase);
            foreach (var directory in candidates)
            {
                var executable = Path.Combine(directory, "bin", executableName);
                if (probe.FileExists(executable))
                {
                    return executable;
                }
            }
        }

        if (toolCommand is { Length: > 0 } && probe.LocalAppData is { Length: > 0 } appData)
        {
            var script = Path.Combine(appData, "JetBrains", "Toolbox", "scripts", toolCommand + ".cmd");
            if (probe.FileExists(script))
            {
                return script;
            }
        }

        return null;
    }

    private static string ToolboxScript(string home, string command) =>
        Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "scripts", command);
}

/// <summary>应用探测所需的文件系统/PATH 查询（真实实现与测试假实现共用）。</summary>
internal interface IExternalAppProbe
{
    string? UserProfile { get; }

    string? LocalAppData { get; }

    string? ProgramFiles { get; }

    string? Home { get; }

    bool FileExists(string path);

    bool DirectoryExists(string path);

    string? ResolveCommand(string commandName);

    IReadOnlyList<string> ListDirectories(string path);
}

/// <summary>真实探测：PATH/PATHEXT 解析 + 目录枚举；所有查询失败都按「不存在」处理。</summary>
internal sealed class SystemExternalAppProbe : IExternalAppProbe
{
    public string? UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string? Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> ListDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch
        {
            return [];
        }
    }

    public string? ResolveCommand(string commandName)
    {
        if (Path.IsPathRooted(commandName))
        {
            return FileExists(commandName) ? commandName : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, commandName + extension);
                if (FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
