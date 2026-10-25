namespace Tokenmetry.Infrastructure.Tests.Jsonl;

using Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// 2026-10-23 Windows 冒烟后续回归锁（B-010）：agent 持写句柄追加日志期间采集仍可边写边读。
/// Windows CreateFile 共享规则要求本次申请的 share mode 覆盖已存在句柄的全部 access——旧实现
/// FileShare.Read（StreamReader(String) 内部同款）对写句柄即抛 sharing violation，Unix 上
/// FileShare 是空操作、三平台恒绿，Windows 下旧实现即红（无需平台跳过）。
/// </summary>
public class AgentLogReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ccusage-logreader-{Guid.NewGuid():N}");

    private string TempFile(string content)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "session.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void OpenRead_coexists_with_agent_write_handle()
    {
        var path = TempFile("{\"line\":1}\n");
        // agent 追加日志的典型句柄形态：写访问、允许他人读（FileShare.Read）。
        using var agentHandle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        using var reader = new StreamReader(AgentLogReader.OpenRead(path));

        reader.ReadToEnd().Should().Be("{\"line\":1}\n");
    }

    [Fact]
    public async Task Scanner_default_reader_factory_reads_while_agent_writes()
    {
        var path = TempFile("{\"line\":1}\n{\"line\":2}\n");
        using var agentHandle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var scanner = new IncrementalJsonlScanner();
        var output = await scanner.ScanAsync(
            [path],
            (_, reader, _) =>
            {
                var lines = 0;
                while (reader.ReadLine() is not null)
                {
                    lines++;
                }

                return Task.FromResult(new JsonlFileParseOutcome(
                    Records: [], FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0, Interrupted: false));
            },
            CancellationToken.None);

        // 旧实现打不开即计 LinesSkipped 不落账（下轮重试）——持写句柄期间永远采不到。
        output.Diagnostics.FilesRead.Should().Be(1);
        output.Diagnostics.LinesSkipped.Should().Be(0);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
