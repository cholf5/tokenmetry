namespace Tokenmetry.Infrastructure.Tests.JsonSource;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

/// <summary>
/// FileStampMemo（文件型适配器零变更轮记忆，I-042 同构）单元测试：
/// 戳集一致才复用；stat 失败（null 戳）恒未命中；null 戳落账清空记忆（保守全量）。
/// </summary>
public class FileStampMemoTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Capture_missing_file_returns_null()
    {
        var dir = CreateTempDir();
        var existing = Path.Combine(dir, "a.json");
        File.WriteAllText(existing, "{}");

        FileStampMemo.Capture([existing, Path.Combine(dir, "missing.json")]).Should().BeNull();
    }

    [Fact]
    public void Capture_is_ordered_by_path_so_enumeration_order_does_not_affect_reuse()
    {
        var dir = CreateTempDir();
        var b = Path.Combine(dir, "b.json");
        var a = Path.Combine(dir, "a.json");
        File.WriteAllText(b, "{}");
        File.WriteAllText(a, "{}");

        var forward = FileStampMemo.Capture([b, a]);
        var backward = FileStampMemo.Capture([a, b]);

        forward.Should().NotBeNull();
        backward.Should().NotBeNull();
        forward.Should().BeEquivalentTo(backward);
    }

    [Fact]
    public void TryReuse_requires_matching_stamp_sets()
    {
        var memo = new FileStampMemo();
        var records = new List<UsageRecord> { new() { SessionId = "s1" } };
        var diagnostics = new JsonlScanDiagnostics { Status = JsonlScanStatus.Ok, MissingCostRecords = 1 };
        var stamps = new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 10) };
        memo.Update(stamps, records, diagnostics);

        // 相同戳集 → 复用（零读取诊断，FilesDiscovered 取本轮清单数）。
        var reused = memo.TryReuse(stamps, fileCount: 1, roots: ["/x"], out var result);
        reused.Should().BeTrue();
        result.Records.Should().BeSameAs(records);
        result.Diagnostics.FilesRead.Should().Be(0);
        result.Diagnostics.FilesChanged.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        result.Diagnostics.MissingCostRecords.Should().Be(1, "累计诊断沿用上轮");
        result.Diagnostics.FilesDiscovered.Should().Be(1);

        // 戳集不同（文件内容/集合变化）→ 未命中。
        var changed = new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 11) };
        memo.TryReuse(changed, 1, ["/x"], out _).Should().BeFalse();

        // null 戳（本轮存在 stat 失败）→ 未命中。
        memo.TryReuse(null, 1, ["/x"], out _).Should().BeFalse();
    }

    [Fact]
    public void Update_with_null_stamps_clears_memory()
    {
        var memo = new FileStampMemo();
        var stamps = new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 10) };
        memo.Update(stamps, new List<UsageRecord>(), new JsonlScanDiagnostics());
        memo.TryReuse(stamps, 1, [], out _).Should().BeTrue();

        // 读取失败轮（调用方以 null 落账）：记忆清空，同戳也不得复用（下轮保守全量重试）。
        memo.Update(null, new List<UsageRecord>(), new JsonlScanDiagnostics());
        memo.TryReuse(stamps, 1, [], out _).Should().BeFalse();
    }

    [Fact]
    public void With_config_makes_parse_time_configuration_part_of_the_fingerprint()
    {
        // 文件集与文件戳完全不变、仅解析期配置变化（pi-agent 的 store 改名 / 作用域迁移）
        // 必须判为变化，否则复用的是旧归属记录。
        var memo = new FileStampMemo();
        var files = new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 10) };
        var first = FileStampMemo.WithConfig(files, "store-one\u001f/store/sessions");
        var renamed = FileStampMemo.WithConfig(files, "store-two\u001f/store/sessions");

        memo.Update(first, new List<UsageRecord>(), new JsonlScanDiagnostics());

        memo.TryReuse(first, 1, ["/x"], out _).Should().BeTrue("同一配置 → 照常复用");
        memo.TryReuse(renamed, 1, ["/x"], out _).Should().BeFalse("配置签名变化 → 必重扫");
    }

    [Fact]
    public void With_config_keeps_null_stamps_null()
    {
        // 本轮 stat 失败（null 戳）不能被伪造出可用指纹。
        FileStampMemo.WithConfig(null, "any").Should().BeNull();
    }

    [Fact]
    public void Reuse_payload_zeroes_content_diagnostics_but_keeps_cumulative_ones()
    {
        // 与 IncrementalJsonlScanner 零变更轮同口径：本轮没有解析事件 → Malformed / Invalid 归零；
        // 累计四项与 Status 沿用上轮（Partial 不得因诊断归零而消失）。
        var memo = new FileStampMemo();
        var stamps = new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 10) };
        var diagnostics = new JsonlScanDiagnostics
        {
            Status = JsonlScanStatus.Partial,
            MalformedLines = 2,
            InvalidRecords = 3,
            DuplicateRecords = 4,
            MissingCostRecords = 5,
            UnknownModelRecords = 6,
        };
        memo.Update(stamps, new List<UsageRecord>(), diagnostics);

        memo.TryReuse(stamps, 1, [], out var result, out var previous).Should().BeTrue();

        result.Diagnostics.MalformedLines.Should().Be(0);
        result.Diagnostics.InvalidRecords.Should().Be(0);
        result.Diagnostics.LinesSkipped.Should().Be(0);
        result.Diagnostics.DuplicateRecords.Should().Be(4);
        result.Diagnostics.MissingCostRecords.Should().Be(5);
        result.Diagnostics.UnknownModelRecords.Should().Be(6);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);

        // 上轮全量诊断原样透出（双侧适配器要自己重算状态时用）。
        previous.Should().BeSameAs(diagnostics);
    }

    [Fact]
    public void TryReuse_exposes_no_previous_diagnostics_when_it_misses()
    {
        var memo = new FileStampMemo();
        memo.Update(new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 10) }, new List<UsageRecord>(), new JsonlScanDiagnostics());

        memo.TryReuse(new[] { new FileStamp("/x/a.json", DateTime.UtcNow, 11) }, 1, [], out _, out var previous)
            .Should().BeFalse();
        previous.Should().BeNull();
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-stampmemo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
