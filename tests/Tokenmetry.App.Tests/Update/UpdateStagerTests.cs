using System;
using System.IO;
using System.IO.Compression;
using Tokenmetry.App.Update;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 便携形态自替换（P2）：「解包到同目录暂存区 → rename 旧目标 → rename 新载荷」。
/// 测试全部落在临时目录的真实文件上，唯一被替换掉的是「启动新版本」这个动作（绝不能真起进程）。
/// </summary>
public class UpdateStagerTests : IDisposable
{
    private readonly string _directory;

    public UpdateStagerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-stager-{Guid.NewGuid():N}");
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

    [Fact]
    public void ResolveTarget_walks_up_to_the_macos_app_bundle()
    {
        var bundle = Path.Combine(_directory, "Tokenmetry.app");
        var binary = Path.Combine(bundle, "Contents", "MacOS", "Tokenmetry");

        UpdateStager.ResolveTarget(binary).Should().Be(bundle);
        // 非 bundle 路径原样返回（Windows / Linux 的进程路径就是目标文件）
        UpdateStager.ResolveTarget(Path.Combine(_directory, "Tokenmetry.exe"))
            .Should().Be(Path.Combine(_directory, "Tokenmetry.exe"));
    }

    [Fact]
    public void LocatePayload_refuses_a_bundle_with_two_apps()
    {
        var extracted = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(Path.Combine(extracted, "A.app"));
        Directory.CreateDirectory(Path.Combine(extracted, "B.app"));

        var act = () => UpdateStager.LocatePayload(extracted, targetIsDirectory: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*one .app*");
    }

    [Fact]
    public void LocatePayload_refuses_a_payload_that_is_not_an_executable()
    {
        var extracted = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "Tokenmetry"), "#!/bin/sh\nrm -rf /\n");

        var act = () => UpdateStager.LocatePayload(extracted, targetIsDirectory: false);

        act.Should().Throw<InvalidDataException>().WithMessage("*not a Windows PE or Linux ELF*");
    }

    [Fact]
    public void PreparePortable_stages_the_payload_beside_the_target_and_Apply_swaps_it()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var archive = WriteZip("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1, 2, 3]);
        var stager = new UpdateStager(os: "Linux", processId: 1);

        var prepared = stager.PreparePortable("v0.4.0", archive, target);

        prepared.Mode.Should().Be(UpdateApplyMode.PortableSwap);
        prepared.TargetPath.Should().Be(target);
        prepared.BackupPath.Should().Be(target + ".old");
        // 暂存区必须与目标同目录（跨卷 rename 会退化成拷贝，失去原子性）
        Path.GetDirectoryName(prepared.StagingDirectory!).Should().Be(_directory);
        File.Exists(prepared.StagedPayloadPath!).Should().BeTrue();

        stager.Apply(prepared);

        File.ReadAllBytes(target).Should().Equal([0x7F, 0x45, 0x4C, 0x46, 1, 2, 3]);
        File.ReadAllText(target + ".old").Should().Be("old-binary");
    }

    [Fact]
    public void Apply_puts_the_old_binary_back_when_the_new_payload_cannot_be_moved()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var archive = WriteZip("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1]);
        var stager = new UpdateStager(os: "Linux", processId: 1);
        var prepared = stager.PreparePortable("v0.4.0", archive, target);

        // 模拟「暂存区在两次 rename 之间消失」：载荷被删，第二次 rename 必然失败。
        File.Delete(prepared.StagedPayloadPath!);

        var act = () => stager.Apply(prepared);

        act.Should().Throw<IOException>();
        File.ReadAllText(target).Should().Be("old-binary"); // 用户还在旧版本上，不是半装状态
    }

    [Fact]
    public void Apply_replaces_a_whole_app_bundle_on_macos()
    {
        var bundle = Path.Combine(_directory, "Tokenmetry.app");
        var binary = Path.Combine(bundle, "Contents", "MacOS", "Tokenmetry");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "old");

        var archive = Path.Combine(_directory, "mac.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Tokenmetry.app/Contents/MacOS/Tokenmetry");
            using var stream = entry.Open();
            stream.Write("new"u8);
        }

        var stager = new UpdateStager(os: "OSX", processId: 1);
        var prepared = stager.PreparePortable("v0.4.0", archive, binary);
        prepared.TargetPath.Should().Be(bundle);

        stager.Apply(prepared);

        File.ReadAllText(binary).Should().Be("new");
        Directory.Exists(bundle + ".old").Should().BeTrue();
    }

    [Fact]
    public void Relaunch_reports_failure_when_the_process_cannot_start()
    {
        var stager = new UpdateStager(os: "Linux", processId: 1, relaunchOverride: _ => false);
        var prepared = new PreparedUpdate("v0.4.0", UpdateApplyMode.PortableSwap, "pkg.zip",
            Path.Combine(_directory, "Tokenmetry"), Path.Combine(_directory, "Tokenmetry.old"),
            Path.Combine(_directory, "payload"), null);

        stager.Relaunch(prepared).Should().BeFalse();
    }

    private string WriteZip(string entryName, byte[] content)
    {
        var path = Path.Combine(_directory, "pkg.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(content);
        return path;
    }
}
