using System;
using System.IO;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 更新日志：跨进程的唯一凭据（旧进程写、重启后的新进程读）。读写都必须 best-effort——
/// 这文件坏了只该让「已更新到 vX」的提示消失，不能挡住启动。
/// </summary>
public class UpdateJournalTests : IDisposable
{
    private readonly string _directory;

    public UpdateJournalTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows 文件锁抖动。
        }
    }

    private string JournalPath => Path.Combine(_directory, "update-state.json");

    [Fact]
    public void Save_Load_Clear_round_trip()
    {
        var journal = new UpdateJournal(JournalPath);
        journal.Load().Should().BeNull();

        journal.Save(new UpdateJournalRecord
        {
            PendingVersion = "v0.4.0",
            BackupPath = "/tmp/Tokenmetry.old",
            TargetPath = "/tmp/Tokenmetry",
            StagedUtc = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        });

        var loaded = journal.Load();
        loaded.Should().NotBeNull();
        loaded!.PendingVersion.Should().Be("v0.4.0");
        loaded.BackupPath.Should().Be("/tmp/Tokenmetry.old");
        loaded.TargetPath.Should().Be("/tmp/Tokenmetry");

        journal.Clear();
        File.Exists(JournalPath).Should().BeFalse();
        journal.Load().Should().BeNull();
    }

    [Fact]
    public void Corrupt_file_reads_as_no_journal_and_still_can_be_overwritten()
    {
        File.WriteAllText(JournalPath, "{ this is not json");
        var journal = new UpdateJournal(JournalPath);

        journal.Load().Should().BeNull();

        journal.Save(new UpdateJournalRecord { PendingVersion = "v0.4.0" });
        journal.Load()!.PendingVersion.Should().Be("v0.4.0");
    }

    [Fact]
    public void Save_is_a_no_op_when_the_directory_cannot_be_created()
    {
        // 把父路径占成一个文件：创建目录必然失败。写日志失败不该冒泡到更新流程。
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "x");
        var journal = new UpdateJournal(Path.Combine(blocked, "update-state.json"));

        var act = () => journal.Save(new UpdateJournalRecord { PendingVersion = "v0.4.0" });

        act.Should().NotThrow();
        journal.Load().Should().BeNull();
    }
}
