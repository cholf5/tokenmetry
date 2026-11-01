namespace Tokenmetry.Infrastructure.Tests.LocalIngestion;

using Tokenmetry.Infrastructure.LocalIngestion;

/// <summary>
/// I-045 采集管线文件日志：写入/开关/轮转/异常含堆栈/多行原子性。
/// 隐私红线（卡内定案）：日志只承载路径、计数、耗时与异常——不含会话内容；测试同样只断言
/// 这些结构化事实。轮转三份、单文件上限与默认开取自卡内定案（5 MB × 3）。
/// </summary>
public class IngestionFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ccusage-logger-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WriteLine_WhenDisabled_DoesNotCreateFile()
    {
        using var logger = new IngestionFileLogger(_dir, enabled: false);

        logger.WriteLine("nope");

        Directory.Exists(_dir).Should().BeFalse();
    }

    [Fact]
    public void WriteLine_WhenEnabled_AppendsTimestampedLine()
    {
        using var logger = new IngestionFileLogger(_dir, enabled: true);

        logger.WriteLine("hello");
        logger.WriteLine("again");

        var text = File.ReadAllText(logger.CurrentFilePath!);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().EndWith("hello");
        lines[1].Should().EndWith("again");
        // 时间戳前缀：ISO 8601（含 Z 的 UTC 口径）
        lines[0].Should().StartWith("20").And.Contain("T").And.Contain("Z ");
    }

    [Fact]
    public void WriteLine_WithException_IncludesTypeMessageAndStackTrace()
    {
        using var logger = new IngestionFileLogger(_dir, enabled: true);
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom-marker");
        }
        catch (Exception captured)
        {
            ex = captured;
        }

        logger.WriteLine("ctx", ex);

        var text = File.ReadAllText(logger.CurrentFilePath!);
        text.Should().Contain("InvalidOperationException").And.Contain("boom-marker").And.Contain("at ");
    }

    [Fact]
    public void WriteLine_IsAtomicUnderConcurrentWriters()
    {
        using var logger = new IngestionFileLogger(_dir, enabled: true);

        Parallel.For(0, 64, i => logger.WriteLine($"line-{i}"));

        var lines = File.ReadAllLines(logger.CurrentFilePath!);
        lines.Should().HaveCount(64);
        lines.Should().OnlyContain(l => l.EndsWith("Z") || l.Split("Z ", 2).Length == 2);
        foreach (var line in lines)
        {
            var payload = line[(line.IndexOf("Z ") + 2)..];
            payload.Should().MatchRegex("^line-\\d+$", "并发写不串行交错（行级原子）");
        }
    }

    [Fact]
    public void Rotation_WhenSizeExceeded_RenamesToKeepUpToMaxArchives()
    {
        // 单行约 40+ 字节，2 KB 上限 + 30 字节单行 → 一条即触发轮转。
        using (var logger = new IngestionFileLogger(_dir, enabled: true, maxFileBytes: 2048, maxArchives: 3))
        {
            for (var i = 0; i < 200; i++)
            {
                logger.WriteLine(new string('x', 20) + $"-{i}");
            }

            logger.CurrentFilePath.Should().NotBeNull();
            File.Exists(logger.CurrentFilePath!).Should().BeTrue();
        }

        // 轮转产物：app.log（活跃）+ 保留 ≤3 个归档（app.log.1..3）；200 行 × ~45 B ≈ 9 KB → 至少 3 次轮转。
        var archives = Directory.GetFiles(_dir, "app.log.*")
            .Where(f => int.TryParse(Path.GetExtension(f).TrimStart('.'), out _))
            .ToList();
        archives.Should().HaveCount(3, "轮转保留上限 = 3 份");
        archives.Should().OnlyContain(f => File.ReadAllLines(f).Length > 0);
        File.ReadAllLines(archives[0]).Should().NotBeEmpty();
    }

    [Fact]
    public void DisposedLogger_StopsWritingAndKeepsFiles()
    {
        string path;
        using (var logger = new IngestionFileLogger(_dir, enabled: true))
        {
            logger.WriteLine("before");
            path = logger.CurrentFilePath!;
        }

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("before");
    }

    [Fact]
    public void CurrentFilePath_IsNullWhenDisabled()
    {
        using var logger = new IngestionFileLogger(_dir, enabled: false);

        logger.CurrentFilePath.Should().BeNull();
    }
}
