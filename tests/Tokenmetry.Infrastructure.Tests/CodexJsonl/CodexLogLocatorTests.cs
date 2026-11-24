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
        var locator = NewLocator(("USERPROFILE", "C:/fixtures/fixture"), ("HOME", "/home/fixture-lowprio"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
            [Path.Combine("C:/fixtures/fixture", ".codex")]);
    }

    [Fact]
    public void Comma_separated_codex_home_supports_tilde_expansion()
    {
        var locator = NewLocator(("CODEX_HOME", "/data/codex, ~/codex-profile"), ("HOME", "C:/fixtures/fixture"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
        [
            Path.Combine("/data/codex"),
            Path.Combine("C:/fixtures/fixture", "codex-profile"),
        ]);
    }

    [Fact]
    public void Blank_codex_home_falls_back_to_default()
    {
        var locator = NewLocator(("CODEX_HOME", "  , "), ("USERPROFILE", "C:/fixtures/fixture"));

        locator.ResolveHomeCandidates().Should().BeEquivalentTo(
            [Path.Combine("C:/fixtures/fixture", ".codex")]);
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

    [Fact]
    public void Additional_home_candidates_are_appended_after_auto_discovery()
    {
        var envHome = CreateTempHome();
        var cindyHome = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(envHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(cindyHome, "sessions"));
        var locator = new CodexLogLocator(
            key => key == "CODEX_HOME" ? envHome : null,
            () => [cindyHome]);

        locator.ResolveHomeCandidates().Should().Equal(envHome, cindyHome);
        locator.ResolveSources(null).Should().BeEquivalentTo(
        [
            new CodexSource(Path.Combine(envHome, "sessions"), envHome),
            new CodexSource(Path.Combine(cindyHome, "sessions"), cindyHome),
        ]);
    }

    [Fact]
    public void Additional_home_candidates_do_not_expand_explicit_home()
    {
        var explicitHome = CreateTempHome();
        var cindyHome = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(explicitHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(cindyHome, "sessions"));
        var locator = new CodexLogLocator(
            key => key == "CODEX_HOME" ? cindyHome : null,
            () => [cindyHome]);

        var sources = locator.ResolveSources(explicitHome);

        sources.Should().BeEquivalentTo(
            [new CodexSource(Path.Combine(explicitHome, "sessions"), explicitHome)],
            "显式配置是调用方钉定，附加发现根不参与");
    }

    [Fact]
    public void Collect_deduped_files_prefers_the_newest_adopted_copy_across_homes()
    {
        // 宿主收养外部会话时会把同一 rollout 原样复制进私有 home；两个副本不能各计一次。
        var externalHome = CreateTempHome();
        var cindyHome = CreateTempHome();
        const string relative =
            "sessions/2026/05/25/rollout-2026-05-25T16-00-18-11111111-1111-4111-8111-111111111111.jsonl";
        WriteFile(externalHome, relative, "");
        WriteFile(cindyHome, relative, "");
        var externalFile = Path.Combine(externalHome, relative.Replace('/', Path.DirectorySeparatorChar));
        var cindyFile = Path.Combine(cindyHome, relative.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(externalFile, new DateTime(2026, 5, 25, 16, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(cindyFile, new DateTime(2026, 5, 25, 17, 0, 0, DateTimeKind.Utc));
        var locator = new CodexLogLocator(
            key => key == "CODEX_HOME" ? externalHome : null,
            () => [cindyHome]);

        var files = CodexLogLocator.CollectDedupedFiles(locator.ResolveSources(null));

        files.Should().Equal(
            [(Path.Combine(cindyHome, "sessions"), cindyFile)],
            "同名 rollout 只保留最新写入的副本");
    }

    [Fact]
    public void Collect_deduped_files_prefers_canonical_over_cindy_recovery_artifact()
    {
        // Cindy 的 rollout-cindy-* 是恢复/合成产物；原文件在场时不能因为写入更晚就取代它。
        var externalHome = CreateTempHome();
        var cindyHome = CreateTempHome();
        const string original = "rollout-2026-05-25T16-00-18-11111111-1111-4111-8111-111111111111.jsonl";
        WriteFile(externalHome, $"sessions/2026/05/25/{original}", "");
        WriteFile(cindyHome, $"sessions/2026/05/25/rollout-cindy-preserved-empty-2-{original}", "");
        var canonicalFile = Path.Combine(externalHome, "sessions", "2026", "05", "25", original);
        var artifactFile = Path.Combine(cindyHome, "sessions", "2026", "05", "25",
            $"rollout-cindy-preserved-empty-2-{original}");
        File.SetLastWriteTimeUtc(canonicalFile, new DateTime(2026, 5, 25, 16, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(artifactFile, new DateTime(2026, 5, 25, 18, 0, 0, DateTimeKind.Utc));
        var locator = new CodexLogLocator(
            key => key == "CODEX_HOME" ? externalHome : null,
            () => [cindyHome]);

        var files = CodexLogLocator.CollectDedupedFiles(locator.ResolveSources(null));

        files.Should().Equal((Path.Combine(externalHome, "sessions"), canonicalFile));
    }

    [Fact]
    public void Collect_deduped_files_keeps_distinct_native_rollouts_for_the_same_thread()
    {
        // 原生 revert 会产生 <threadId>_<rolloutId> 的独立历史：不能按 threadId 合并掉。
        var externalHome = CreateTempHome();
        var cindyHome = CreateTempHome();
        const string first =
            "sessions/2026/05/25/rollout-2026-05-25T16-00-18-11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222.jsonl";
        const string second =
            "sessions/2026/05/25/rollout-2026-05-25T16-00-18-11111111-1111-4111-8111-111111111111_33333333-3333-4333-8333-333333333333.jsonl";
        WriteFile(externalHome, first, "");
        WriteFile(cindyHome, second, "");
        var locator = new CodexLogLocator(
            key => key == "CODEX_HOME" ? externalHome : null,
            () => [cindyHome]);

        var files = CodexLogLocator.CollectDedupedFiles(locator.ResolveSources(null));

        files.Should().HaveCount(2);
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
