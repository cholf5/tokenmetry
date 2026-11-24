using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 外部应用目录（编辑器 / Git GUI）的探测与启动计划：假 probe 覆盖 mac / Windows / Linux
/// 三套定位链，不真启动进程。重点是「只出现本机真的装了的应用」与路径/参数不被拼坏。
/// </summary>
public sealed class ExternalAppCatalogTests
{
    [Fact]
    public void Mac_detects_installed_bundles_and_builds_open_plan()
    {
        var probe = new FakeProbe { UserProfile = "/home/test" };
        var vscode = Path.Combine("/Applications", "Visual Studio Code.app");
        var fork = Path.Combine("/Applications", "Fork.app");
        probe.Directories.Add(vscode);
        probe.Directories.Add(fork);

        var entries = ExternalAppCatalog.BuildMacEntries(probe);

        Assert.Equal(new[] { "vscode", "fork" }, entries.Select(entry => entry.Id)); // 编辑器在前、Git 客户端在后
        Assert.Equal(vscode, entries[0].IconSource);
        var plan = entries[0].PlanFor("/work/alpha");
        Assert.Equal("open", plan.FileName);
        Assert.Equal(new[] { "-a", vscode, "/work/alpha" }, plan.Arguments);
    }

    [Fact]
    public void Mac_checks_user_applications_and_omits_missing_apps()
    {
        var userProfile = "/home/test";
        var probe = new FakeProbe { UserProfile = userProfile };
        var rider = Path.Combine(userProfile, "Applications", "Rider.app");
        probe.Directories.Add(rider);

        var entries = ExternalAppCatalog.BuildMacEntries(probe);

        Assert.Equal(new[] { "rider" }, entries.Select(entry => entry.Id));
        Assert.DoesNotContain(entries, entry => entry.Id == "vscode");
    }

    [Fact]
    public void Windows_uses_known_paths_and_appends_directory()
    {
        var localAppData = @"C:\work\AppData\Local";
        var probe = new FakeProbe { LocalAppData = localAppData };
        var vscode = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
        var fork = Path.Combine(localAppData, "Fork", "Fork.exe");
        probe.Files.Add(vscode);
        probe.Files.Add(fork);

        var entries = ExternalAppCatalog.BuildWindowsEntries(probe);
        var plan = entries.Single(entry => entry.Id == "vscode").PlanFor(@"C:\work\alpha");

        Assert.Equal(new[] { "vscode", "fork" }, entries.Select(entry => entry.Id));
        Assert.Equal(vscode, entries.Single(entry => entry.Id == "vscode").IconSource);
        Assert.Equal(vscode, plan.FileName);
        Assert.Equal(new[] { @"C:\work\alpha" }, plan.Arguments);
        Assert.Equal(@"C:\work\alpha", plan.WorkingDirectory);
    }

    [Fact]
    public void Windows_cmd_shim_runs_through_cmd_exe()
    {
        var probe = new FakeProbe();
        var shim = @"C:\VS Code\bin\code.cmd";
        probe.Commands["code"] = shim;

        var entries = ExternalAppCatalog.BuildWindowsEntries(probe);
        var plan = entries.Single(entry => entry.Id == "vscode").PlanFor(@"C:\work\alpha");

        Assert.Equal("cmd.exe", plan.FileName);
        Assert.Equal(new[] { "/c", shim, @"C:\work\alpha" }, plan.Arguments);
        Assert.Null(entries.Single(entry => entry.Id == "vscode").IconSource); // .cmd 垫片没有应用 exe 图标源
    }

    [Fact]
    public void Windows_scans_newest_jetbrains_install_directory()
    {
        var programFiles = @"C:\Program Files";
        var probe = new FakeProbe { ProgramFiles = programFiles };
        var jetBrains = Path.Combine(programFiles, "JetBrains");
        var older = Path.Combine(jetBrains, "Rider 2025.3");
        var newer = Path.Combine(jetBrains, "Rider 2026.1");
        probe.Children[jetBrains] = [older, newer];
        probe.Files.Add(Path.Combine(older, "bin", "rider64.exe"));
        probe.Files.Add(Path.Combine(newer, "bin", "rider64.exe"));

        var entries = ExternalAppCatalog.BuildWindowsEntries(probe);
        var plan = entries.Single(entry => entry.Id == "rider").PlanFor(@"C:\work\alpha");

        Assert.Equal(Path.Combine(newer, "bin", "rider64.exe"), plan.FileName);
    }

    [Fact]
    public void Windows_resolves_versioned_github_desktop()
    {
        var localAppData = @"C:\work\AppData\Local";
        var probe = new FakeProbe { LocalAppData = localAppData };
        var root = Path.Combine(localAppData, "GitHubDesktop");
        var versioned = Path.Combine(root, "app-3.4.0");
        probe.Children[root] = [versioned];
        probe.Files.Add(Path.Combine(versioned, "GitHubDesktop.exe"));

        var entries = ExternalAppCatalog.BuildWindowsEntries(probe);
        var plan = entries.Single(entry => entry.Id == "githubdesktop").PlanFor(@"C:\work\alpha");

        Assert.Equal(Path.Combine(versioned, "GitHubDesktop.exe"), plan.FileName);
    }

    [Fact]
    public void Linux_uses_path_commands_and_toolbox_scripts()
    {
        var probe = new FakeProbe { Home = "/home/test" };
        probe.Commands["code"] = "/usr/bin/code";
        var toolboxRider = Path.Combine(
            "/home/test", ".local", "share", "JetBrains", "Toolbox", "scripts", "rider");
        probe.Files.Add(toolboxRider);

        var entries = ExternalAppCatalog.BuildLinuxEntries(probe);

        Assert.Equal(new[] { "vscode", "rider" }, entries.Select(entry => entry.Id));
        Assert.Equal("/usr/bin/code", entries[0].PlanFor("/work/alpha").FileName);
        Assert.Equal("/usr/bin/code", entries[0].IconSource);
        Assert.Equal(toolboxRider, entries[1].PlanFor("/work/alpha").FileName);
        Assert.Equal(toolboxRider, entries[1].IconSource);
    }

    [Fact]
    public void Missing_apps_never_appear_in_the_menu_catalog()
    {
        var probe = new FakeProbe();

        Assert.Empty(ExternalAppCatalog.BuildMacEntries(probe));
        Assert.Empty(ExternalAppCatalog.BuildWindowsEntries(probe));
        Assert.Empty(ExternalAppCatalog.BuildLinuxEntries(probe));
    }

    private sealed class FakeProbe : IExternalAppProbe
    {
        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.Ordinal);

        public string? UserProfile { get; set; }

        public string? LocalAppData { get; set; }

        public string? ProgramFiles { get; set; }

        public string? Home { get; set; }

        public bool FileExists(string path) => Files.Contains(path);

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public string? ResolveCommand(string commandName) => Commands.GetValueOrDefault(commandName);

        public IReadOnlyList<string> ListDirectories(string path) =>
            Children.TryGetValue(path, out var children) ? children : [];
    }
}
