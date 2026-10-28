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
