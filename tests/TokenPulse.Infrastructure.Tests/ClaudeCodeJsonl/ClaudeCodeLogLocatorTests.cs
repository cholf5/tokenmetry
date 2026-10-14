namespace TokenPulse.Infrastructure.Tests.ClaudeCodeJsonl;

using TokenPulse.Infrastructure.ClaudeCodeJsonl;

public class ClaudeCodeLogLocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Resolves_comma_separated_config_dirs_and_dedupes()
    {
        var rootA = CreateClaudeRoot();
        var rootB = CreateClaudeRoot();
        var locator = NewLocator(("CLAUDE_CONFIG_DIR", $"{rootA},{rootA},  {rootB} "));

        locator.ResolveRoots(null).Should().BeEquivalentTo(new[] { rootA, rootB });
    }

    [Fact]
    public void Expands_home_prefix_in_config_dirs()
    {
        var home = CreateTempDir();
        var configDir = Path.Combine(home, "claude-config");
        Directory.CreateDirectory(Path.Combine(configDir, "projects"));
        var locator = NewLocator(("HOME", home), ("CLAUDE_CONFIG_DIR", "~/claude-config"));

        locator.ResolveRoots(null).Should().BeEquivalentTo(new[] { configDir });
    }

    [Fact]
    public void Config_dir_env_disables_auto_detection_entirely()
    {
        // XDG / HOME 下都存在合法候选，但 CLAUDE_CONFIG_DIR 存在时不回退默认路径。
        var invalidConfigDir = CreateTempDir(); // 没有 projects 子目录，归一化失败
        var xdg = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(xdg, "claude", "projects"));
        var home = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects"));
        var locator = NewLocator(
            ("HOME", home),
            ("XDG_CONFIG_HOME", xdg),
            ("CLAUDE_CONFIG_DIR", invalidConfigDir));

        locator.ResolveRoots(null).Should().BeEmpty();
    }

    [Fact]
    public void Auto_detection_uses_xdg_config_home_then_home_claude()
    {
        var home = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects"));
        var xdg = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(xdg, "claude", "projects"));
        var locator = NewLocator(("HOME", home), ("XDG_CONFIG_HOME", xdg));

        locator.ResolveRoots(null).Should().BeEquivalentTo(new[]
        {
            Path.Combine(xdg, "claude"),
            Path.Combine(home, ".claude"),
        });
    }

    [Fact]
    public void Without_xdg_falls_back_to_home_config_claude()
    {
        var home = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(home, ".config", "claude", "projects"));
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects"));
        var locator = NewLocator(("HOME", home));

        locator.ResolveRoots(null).Should().BeEquivalentTo(new[]
        {
            Path.Combine(home, ".config", "claude"),
            Path.Combine(home, ".claude"),
        });
    }

    [Fact]
    public void Explicit_root_takes_precedence_over_environment()
    {
        var envRoot = CreateClaudeRoot();
        var explicitRoot = CreateClaudeRoot();
        var locator = NewLocator(("CLAUDE_CONFIG_DIR", envRoot));

        locator.ResolveRoots(explicitRoot).Should().BeEquivalentTo(new[] { explicitRoot });
    }

    [Fact]
    public void Normalizes_projects_directory_candidate()
    {
        var root = CreateClaudeRoot();
        var noProjects = CreateTempDir();

        ClaudeCodeLogLocator.NormalizeConfigCandidate(root).Should().Be(root);
        ClaudeCodeLogLocator.NormalizeConfigCandidate(Path.Combine(root, "projects")).Should().Be(root);
        ClaudeCodeLogLocator.NormalizeConfigCandidate(noProjects).Should().BeNull();
    }

    [Fact]
    public void Enumerates_jsonl_files_recursively_and_sorts_by_path()
    {
        var root = CreateClaudeRoot();
        WriteFile(Path.Combine(root, "projects", "project-a", "session-a", "file.jsonl"), "");
        WriteFile(Path.Combine(root, "projects", "project-a", "session-a", "ignored.txt"), "");
        WriteFile(Path.Combine(root, "projects", "project-b", "session-b.jsonl"), "");

        var files = ClaudeCodeLogLocator.EnumerateUsageFiles(new[] { root });

        files.Should().HaveCount(2);
        files.Should().BeInAscendingOrder(StringComparer.Ordinal);
        files.Should().AllSatisfy(file => Path.GetExtension(file).Should().Be(".jsonl"));
    }

    [Fact]
    public void Enumeration_tolerates_missing_projects_directory()
    {
        var root = CreateTempDir();

        ClaudeCodeLogLocator.EnumerateUsageFiles(new[] { root }).Should().BeEmpty();
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static ClaudeCodeLogLocator NewLocator(params (string Key, string? Value)[] variables)
        => new(key => variables.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal)).Value);

    private string CreateClaudeRoot()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        return root;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccusage-locator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
