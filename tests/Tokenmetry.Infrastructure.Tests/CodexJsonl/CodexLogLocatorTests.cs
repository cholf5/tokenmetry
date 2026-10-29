namespace Tokenmetry.Infrastructure.Tests.CodexJsonl;

using Tokenmetry.Infrastructure.CodexJsonl;

public class CodexLogLocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Resolves_sessions_and_archived_from_codex_home_env()
    {
        var home = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
        var locator = NewLocator(("CODEX_HOME", home));

        var sources = locator.ResolveSources(null);

        sources.Should().BeEquivalentTo(
        [
            new CodexSource(Path.Combine(home, "sessions"), home),
            new CodexSource(Path.Combine(home, "archived_sessions"), home),
        ]);
    }

    [Fact]
    public void Falls_back_to_home_itself_when_no_sessions_dirs()
    {
        var home = CreateTempHome(); // 空目录：无 sessions/ 也无 archived_sessions/
        var locator = NewLocator(("CODEX_HOME", home));

        var sources = locator.ResolveSources(null);

        sources.Should().BeEquivalentTo([new CodexSource(home, home)]);
    }

    [Fact]
    public void Defaults_to_codex_dir_under_userprofile()
    {
        var locator = NewLocator(("USERPROFILE", "/Users/fixture"), ("HOME", "/home/fixture-lowprio"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
            [Path.Combine("/Users/fixture", ".codex")]);
    }

    [Fact]
    public void Comma_separated_codex_home_supports_tilde_expansion()
    {
        var locator = NewLocator(("CODEX_HOME", "/data/codex, ~/codex-profile"), ("HOME", "/Users/fixture"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
        [
            Path.Combine("/data/codex"),
            Path.Combine("/Users/fixture", "codex-profile"),
        ]);
    }

    [Fact]
    public void Blank_codex_home_falls_back_to_default()
    {
        var locator = NewLocator(("CODEX_HOME", "  , "), ("USERPROFILE", "/Users/fixture"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
            [Path.Combine("/Users/fixture", ".codex")]);
    }

    [Fact]
    public void Explicit_home_takes_precedence_and_supports_tilde()
    {
        var home = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var locator = NewLocator(("CODEX_HOME", "/elsewhere/codex"), ("HOME", home));

        var tildeSources = locator.ResolveSources("~/codex-home");
        tildeSources.Should().BeEmpty(); // ~/codex-home 不存在 → 无数据源

        var sources = locator.ResolveSources("~");
        sources.Should().BeEquivalentTo([new CodexSource(Path.Combine(home, "sessions"), home)]);
    }

    [Fact]
    public void Missing_homes_yield_no_sources()
    {
        var locator = NewLocator(("CODEX_HOME", "/does/not/exist"));

        locator.ResolveSources(null).Should().BeEmpty();
    }

    [Fact]
    public void Collect_deduped_files_keeps_active_sessions_copy()
    {
        var home = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
        var sessionsDup = Path.Combine(home, "sessions", "dup.jsonl");
        var archivedDup = Path.Combine(home, "archived_sessions", "dup.jsonl");
        var archivedOnly = Path.Combine(home, "archived_sessions", "only.jsonl");
        File.WriteAllText(sessionsDup, "");
        File.WriteAllText(archivedDup, "");
        File.WriteAllText(archivedOnly, "");
        var sources = new CodexLogLocator(_ => null).ResolveSources(home);

        var files = CodexLogLocator.CollectDedupedFiles(sources);

        files.Should().BeEquivalentTo(
        [
            (Path.Combine(home, "sessions"), sessionsDup),
            (Path.Combine(home, "archived_sessions"), archivedOnly),
        ]);
    }

    [Fact]
    public void Collect_deduped_files_enumerates_nested_session_paths()
    {
        var home = CreateTempHome();
        var sessionsDir = Path.Combine(home, "sessions");
        var nested = Path.Combine(sessionsDir, "2026", "05", "25");
        Directory.CreateDirectory(nested);
        var nestedFile = Path.Combine(nested, "rollout-a.jsonl");
        File.WriteAllText(nestedFile, "");
        var sources = new CodexLogLocator(_ => null).ResolveSources(home);

        var files = CodexLogLocator.CollectDedupedFiles(sources);

        files.Should().BeEquivalentTo([(sessionsDir, nestedFile)]);
    }

    [Fact]
    public void Collects_plain_and_compressed_rollouts_without_double_counting()
    {
        var home = CreateTempHome();
        WriteFile(home, "sessions/both.jsonl", "{}");
        WriteFile(home, "sessions/both.jsonl.zst", "stub");
        WriteFile(home, "sessions/compressed.jsonl.zst", "stub");
        WriteFile(home, "sessions/other.zst", "stub");
        WriteFile(home, "archived_sessions/compressed.jsonl", "{}");

        var sources = new CodexLogLocator(name => null).ResolveSources(home);
        var files = CodexLogLocator.CollectDedupedFiles(sources);

        files.Should().BeEquivalentTo([
            (Path.Combine(home, "sessions"), Path.Combine(home, "sessions", "both.jsonl")),
            (Path.Combine(home, "sessions"), Path.Combine(home, "sessions", "compressed.jsonl.zst")),
        ], "明文优先于压缩、sessions/ 优先于 archived_sessions/，x.zst 不是 rollout");
    }

    private static void WriteFile(string home, string relativePath, string contents)
    {
        var path = Path.Combine(home, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
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

    private static CodexLogLocator NewLocator(params (string Key, string? Value)[] variables)
        => new(key => variables.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal)).Value);

    private string CreateTempHome()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccusage-codex-locator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }
}
