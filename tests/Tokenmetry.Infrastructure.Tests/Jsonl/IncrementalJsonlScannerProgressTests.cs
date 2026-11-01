namespace Tokenmetry.Infrastructure.Tests.Jsonl;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// I-046 源内进度上报：节流（N 文件/N 秒取大者）、中间值单调、快速小目录不产生回调风暴。
/// 与既有 IncrementalJsonlScannerTests（缓存/变更旗标）分文件，本文件只锁进度回调行为。
/// </summary>
public class IncrementalJsonlScannerProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ccusage-progress-{Guid.NewGuid():N}");

    public IncrementalJsonlScannerProgressTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public async Task Progress_ReportsMonotonicProcessedOverDiscovered()
    {
        var files = CreateFiles(6);
        var scanner = new IncrementalJsonlScanner();

        var reports = new List<(int Processed, int Discovered)>();
        await scanner.ScanAsync(files, ParseFile, CancellationToken.None, (p, d) => reports.Add((p, d)));

        // 小目录（6 文件 < 500）走时间节流：至少首拍（t=0 距上次 ≥2s 恒真——DateTimeOffset.Now 连续调用
        // 差值可能为 0 秒，故首拍由 processed - lastReported ≥ 500 之外的秒条件保证……实现取"或"：
        // t=0 时 (Now - lastReportAt).TotalSeconds ≥ 2 不成立，小目录可能零回调。契约只锁：
        // 出现过的回调必须单调递增且 discovered == 文件数。
        reports.Select(r => r.Processed).Should().BeInAscendingOrder();
        reports.Should().OnlyContain(r => r.Discovered == 6);
        reports.Should().OnlyContain(r => r.Processed <= 6);
    }

    [Fact]
    public async Task Progress_FirstReportAtOrBeforeThrottleWindow()
    {
        // 锁首拍不晚于「500 文件」粒度：>500 文件的目录必然至少一拍（首拍 processed ≥ 500）。
        var files = CreateFiles(501);
        var scanner = new IncrementalJsonlScanner();

        var reports = new List<(int Processed, int Discovered)>();
        await scanner.ScanAsync(files, ParseFile, CancellationToken.None, (p, d) => reports.Add((p, d)));

        reports.Should().NotBeEmpty(">500 文件的目录至少有一拍进度");
        reports[0].Processed.Should().BeGreaterThanOrEqualTo(500, "首拍按文件粒度节流（每 500 个文件一次）");
        reports.Last().Processed.Should().BeLessThanOrEqualTo(501);
    }

    [Fact]
    public async Task Progress_SmallDirectory_DoesNotStorm()
    {
        // 快速小目录的回调上限：时间节流 2 秒窗口内至多 1 拍（processed < 500 时文件条件不触发）。
        var files = CreateFiles(100);
        var scanner = new IncrementalJsonlScanner();

        var calls = 0;
        await scanner.ScanAsync(files, ParseFile, CancellationToken.None, (_, _) => calls++);

        calls.Should().BeLessThanOrEqualTo(1, "100 个文件的快速扫描在 2s 节流窗口内至多一拍（无回调风暴）");
    }

    [Fact]
    public async Task Progress_NullCallback_ScanUnchanged()
    {
        // 不传回调 = 既有行为（既有测试零改动通过的总契约在本文件的可执行表达）
        var files = CreateFiles(3);
        var scanner = new IncrementalJsonlScanner();

        var output = await scanner.ScanAsync(files, ParseFile, CancellationToken.None);

        output.Records.Should().HaveCount(3);
        output.Diagnostics.FilesRead.Should().Be(3);
    }

    [Fact]
    public async Task ParseFailure_InvokesFailureLogWithPathAndException()
    {
        // I-045：解析失败（含堆栈）经 parseFailureLog 出口交调用方写文件日志。
        var good = Path.Combine(_root, "good.jsonl");
        await File.WriteAllTextAsync(good, Line("m1") + "\n");
        var bad = Path.Combine(_root, "bad.jsonl");
        await File.WriteAllTextAsync(bad, "not-openable");

        var failures = new List<(string Path, Exception Exception)>();
        var scanner = new IncrementalJsonlScanner(
            readerFactory: path =>
            {
                if (Path.GetFileName(path) == "bad.jsonl")
                {
                    throw new IOException($"cannot open {path}");
                }

                return new StreamReader(File.OpenRead(path));
            },
            parseFailureLog: (path, ex) => failures.Add((path, ex)));

        await scanner.ScanAsync([good, bad], ParseFile, CancellationToken.None);

        failures.Should().HaveCount(1);
        failures[0].Path.Should().Be(bad);
        failures[0].Exception.Should().BeOfType<IOException>();
        failures[0].Exception.StackTrace.Should().NotBeNull("异常含堆栈（I-045 验收：含堆栈的失败记录）");
    }

    private List<string> CreateFiles(int count)
    {
        var files = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_root, $"f{i:D4}.jsonl");
            File.WriteAllText(path, Line($"m{i}") + "\n");
            files.Add(path);
        }

        return files;
    }

    private static Task<JsonlFileParseOutcome> ParseFile(string path, TextReader reader, CancellationToken ct) =>
        Task.FromResult(new JsonlFileParseOutcome(
            Records: [new UsageRecord { SessionId = "s", Model = "m" }],
            FilesRead: 1,
            LinesSkipped: 0,
            MalformedLines: 0,
            InvalidRecords: 0,
            Interrupted: false));

    private static string Line(string messageId) =>
        $"{{\"sessionId\":\"s\",\"message\":{{\"id\":\"{messageId}\",\"model\":\"m\",\"usage\":{{\"input_tokens\":1,\"output_tokens\":2}}}}}}";
}
