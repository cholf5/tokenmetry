namespace Tokenmetry.Infrastructure.Tests.Jsonl;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// I-042: 增量扫描器零变更轮缓存——文件账面未动时复用去重产物（不重跑全历史 SelectMany +
/// 去重委托），变更/消失如实计 FilesChanged。共享引擎的解析/去重管道语义由各适配器测试与
/// LocalIngestionDataSourceTests 覆盖，本文件只锁缓存与变更旗标行为本身。
/// </summary>
public class IncrementalJsonlScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ccusage-scanner-{Guid.NewGuid():N}");

    public IncrementalJsonlScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SecondUnchangedScan_ReusesDedupedRecords_WithoutRerunningDedupe()
    {
        var file = Path.Combine(_root, "a.jsonl");
        await File.WriteAllTextAsync(file, Line("m1") + "\n" + Line("m2") + "\n");
        var dedupeCalls = 0;
        var scanner = new IncrementalJsonlScanner(dedupe: records =>
        {
            dedupeCalls++;
            return records.ToList();
        });

        var first = await scanner.ScanAsync([file], ParseFile, CancellationToken.None);
        first.Diagnostics.FilesChanged.Should().Be(1, "首轮账面从无到有");
        first.Records.Should().HaveCount(2);
        dedupeCalls.Should().Be(1);

        var second = await scanner.ScanAsync([file], ParseFile, CancellationToken.None);

        second.Diagnostics.FilesChanged.Should().Be(0, "文件账面未动");
        second.Records.Should().BeSameAs(first.Records, "零变更轮复用缓存去重产物");
        second.Diagnostics.FilesRead.Should().Be(0, "本轮无解析事件");
        second.Diagnostics.FilesDiscovered.Should().Be(1);
        second.Diagnostics.DuplicateRecords.Should().Be(first.Diagnostics.DuplicateRecords);
        second.Diagnostics.MissingCostRecords.Should().Be(first.Diagnostics.MissingCostRecords);
        dedupeCalls.Should().Be(1, "全历史去重不重跑（常态分配大头）");
    }

    [Fact]
    public async Task ChangedFile_RerunsDedupe_AndReportsFilesChanged()
    {
        var file = Path.Combine(_root, "a.jsonl");
        await File.WriteAllTextAsync(file, Line("m1") + "\n");
        var dedupeCalls = 0;
        var scanner = new IncrementalJsonlScanner(dedupe: records =>
        {
            dedupeCalls++;
            return records.ToList();
        });
        await scanner.ScanAsync([file], ParseFile, CancellationToken.None);

        // 追加行：长度变化保证 stamp 变化（mtime 粒度不可依赖）。
        await File.AppendAllTextAsync(file, Line("m2") + "\n");
        var second = await scanner.ScanAsync([file], ParseFile, CancellationToken.None);

        second.Diagnostics.FilesChanged.Should().Be(1);
        second.Records.Should().HaveCount(2);
        dedupeCalls.Should().Be(2, "账面变了必须重建去重产物");
        second.Diagnostics.FilesRead.Should().Be(1, "只有变更文件被重新解析");
    }

    [Fact]
    public async Task RemovedFile_ReportsFilesChanged_AndDropsItsRecords()
    {
        var fileA = Path.Combine(_root, "a.jsonl");
        var fileB = Path.Combine(_root, "b.jsonl");
        await File.WriteAllTextAsync(fileA, Line("m1") + "\n");
        await File.WriteAllTextAsync(fileB, Line("m2") + "\n");
        var scanner = new IncrementalJsonlScanner(dedupe: records => records.ToList());
        await scanner.ScanAsync([fileA, fileB], ParseFile, CancellationToken.None);

        File.Delete(fileB);
        var second = await scanner.ScanAsync([fileA], ParseFile, CancellationToken.None);

        second.Diagnostics.FilesChanged.Should().Be(1, "文件消失计入账面变化");
        second.Records.Should().ContainSingle("消失文件的记录从账面移除");
        second.Records[0].MessageId.Should().Be("m1");
    }

    [Fact]
    public async Task EmptyFileList_BothRounds_ReportNoChange()
    {
        var scanner = new IncrementalJsonlScanner(dedupe: records => records.ToList());

        var first = await scanner.ScanAsync([], ParseFile, CancellationToken.None);
        var second = await scanner.ScanAsync([], ParseFile, CancellationToken.None);

        first.Diagnostics.FilesChanged.Should().Be(0);
        first.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
        second.Diagnostics.FilesChanged.Should().Be(0, "空清单两连轮稳定无变化");
        second.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    private Task<JsonlFileParseOutcome> ParseFile(string path, TextReader reader, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            records.Add(new UsageRecord
            {
                Timestamp = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
                Agent = "ScannerTest",
                SessionId = "session-1",
                ProjectPath = "/proj",
                Model = "test-model",
                MessageId = line,
                SourceFile = path,
            });
        }

        return Task.FromResult(new JsonlFileParseOutcome(
            records, FilesRead: 1, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0, Interrupted: false));
    }

    private static string Line(string messageId) => messageId;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
