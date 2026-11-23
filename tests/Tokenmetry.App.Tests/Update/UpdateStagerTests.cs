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
    public void LocatePayload_accepts_the_executable_plus_the_plugin_host_directory()
    {
        var extracted = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(Path.Combine(extracted, "plugin-host"));
        var executable = Path.Combine(extracted, "Tokenmetry");
        File.WriteAllBytes(executable, [0x7F, 0x45, 0x4C, 0x46, 1]);
        File.WriteAllText(Path.Combine(extracted, "plugin-host", "Tokenmetry.PluginHost"), "host");

        var payload = UpdateStager.LocatePayload(extracted, targetIsDirectory: false);

        payload.Should().Be(executable);
    }

    [Fact]
    public void LocatePayload_rejects_an_unknown_top_level_entry_beside_the_executable()
    {
        var extracted = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(extracted);
        File.WriteAllBytes(Path.Combine(extracted, "Tokenmetry"), [0x7F, 0x45, 0x4C, 0x46, 1]);
        File.WriteAllText(Path.Combine(extracted, "extra.txt"), "nope");

        var act = () => UpdateStager.LocatePayload(extracted, targetIsDirectory: false);

        act.Should().Throw<InvalidDataException>().WithMessage("*plus optional plugin-host/*");
    }

    [Fact]
    public void LocatePayload_rejects_an_unknown_top_level_directory_beside_the_executable()
    {
        var extracted = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(Path.Combine(extracted, "other-dir"));
        File.WriteAllBytes(Path.Combine(extracted, "Tokenmetry"), [0x7F, 0x45, 0x4C, 0x46, 1]);

        var act = () => UpdateStager.LocatePayload(extracted, targetIsDirectory: false);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void PreparePortable_captures_the_sibling_plugin_host_directory()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var archive = WriteArchive(
            ("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1]),
            ("plugin-host/Tokenmetry.PluginHost", "host"u8.ToArray()));
        var stager = new UpdateStager(os: "Linux", processId: 1);

        var prepared = stager.PreparePortable("v0.6.0", archive, target);

        prepared.StagedHostDirectory.Should()
            .Be(Path.Combine(prepared.StagingDirectory!, "payload", "plugin-host"));
        prepared.TargetHostDirectory.Should().Be(Path.Combine(_directory, "plugin-host"));
    }

    [Fact]
    public void Apply_swaps_the_executable_and_the_sibling_plugin_host()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var oldHost = Path.Combine(_directory, "plugin-host");
        Directory.CreateDirectory(oldHost);
        File.WriteAllText(Path.Combine(oldHost, "old-host.txt"), "old-host");

        var archive = WriteArchive(
            ("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 9]),
            ("plugin-host/new-host.txt", "new-host"u8.ToArray()));
        var stager = new UpdateStager(os: "Linux", processId: 1);
        var prepared = stager.PreparePortable("v0.6.0", archive, target);

        stager.Apply(prepared);

        File.ReadAllBytes(target).Should().Equal([0x7F, 0x45, 0x4C, 0x46, 9]);
        Directory.EnumerateFiles(oldHost).Select(Path.GetFileName)
            .Should().Equal("new-host.txt");
        File.ReadAllText(Path.Combine(oldHost, "new-host.txt")).Should().Be("new-host");
        File.ReadAllText(Path.Combine(oldHost + ".old", "old-host.txt")).Should().Be("old-host");
        File.ReadAllText(target + ".old").Should().Be("old-binary");
    }

    [Fact]
    public void Apply_restores_both_the_old_binary_and_host_when_the_new_executable_cannot_be_moved()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var oldHost = Path.Combine(_directory, "plugin-host");
        Directory.CreateDirectory(oldHost);
        File.WriteAllText(Path.Combine(oldHost, "old-host.txt"), "old-host");

        var archive = WriteArchive(
            ("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1]),
            ("plugin-host/new-host.txt", "new-host"u8.ToArray()));
        var stager = new UpdateStager(os: "Linux", processId: 1);
        var prepared = stager.PreparePortable("v0.6.0", archive, target);

        // 模拟载荷在两次 rename 之间消失：新 exe 放不进去，宿主已被让位，必须一起回滚。
        File.Delete(prepared.StagedPayloadPath!);

        var act = () => stager.Apply(prepared);

        act.Should().Throw<IOException>();
        File.ReadAllText(target).Should().Be("old-binary");
        File.ReadAllText(Path.Combine(oldHost, "old-host.txt")).Should().Be("old-host");
        Directory.Exists(oldHost + ".old").Should().BeFalse();
    }

    [Fact]
    public void Apply_restores_both_the_old_binary_and_host_when_the_new_host_cannot_be_moved()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var oldHost = Path.Combine(_directory, "plugin-host");
        Directory.CreateDirectory(oldHost);
        File.WriteAllText(Path.Combine(oldHost, "old-host.txt"), "old-host");

        var archive = WriteArchive(
            ("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1]),
            ("plugin-host/new-host.txt", "new-host"u8.ToArray()));
        var stager = new UpdateStager(os: "Linux", processId: 1);
        var prepared = stager.PreparePortable("v0.6.0", archive, target);

        // 新 exe 已就位、轮到宿主时暂存目录不见了：必须把旧 exe 与旧宿主一起放回去。
        Directory.Delete(prepared.StagedHostDirectory!, recursive: true);

        var act = () => stager.Apply(prepared);

        act.Should().Throw<IOException>();
        File.ReadAllText(target).Should().Be("old-binary");
        File.ReadAllText(Path.Combine(oldHost, "old-host.txt")).Should().Be("old-host");
        Directory.Exists(oldHost + ".old").Should().BeFalse();
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
            using (var stream = zip.CreateEntry("Tokenmetry.app/Contents/MacOS/Tokenmetry").Open())
            {
                stream.Write("new"u8);
            }

            // 插件宿主在 .app 内，随整包一起换——不需要单独的宿主交换路径。
            using (var hostStream = zip.CreateEntry("Tokenmetry.app/Contents/MacOS/plugin-host/host.txt").Open())
            {
                hostStream.Write("new-host"u8);
            }
        }

        var stager = new UpdateStager(os: "OSX", processId: 1);
        var prepared = stager.PreparePortable("v0.4.0", archive, binary);
        prepared.TargetPath.Should().Be(bundle);
        prepared.StagedHostDirectory.Should().BeNull();
        prepared.TargetHostDirectory.Should().BeNull();

        stager.Apply(prepared);

        File.ReadAllText(binary).Should().Be("new");
        File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "plugin-host", "host.txt"))
            .Should().Be("new-host");
        Directory.Exists(bundle + ".old").Should().BeTrue();
    }

    [Fact]
    public void Apply_makes_the_nested_plugin_host_executable_on_macos()
    {
        if (OperatingSystem.IsWindows())
        {
            // Unix 文件模式 API 在 Windows 上不支持；该断言只在 macOS / Linux CI 上有意义。
            return;
        }

        var bundle = Path.Combine(_directory, "Tokenmetry.app");
        var binary = Path.Combine(bundle, "Contents", "MacOS", "Tokenmetry");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "old");

        var archive = Path.Combine(_directory, "mac-host.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("Tokenmetry.app/Contents/MacOS/Tokenmetry").Open())
            {
                stream.Write("new"u8);
            }

            using (var hostStream = zip.CreateEntry(
                "Tokenmetry.app/Contents/MacOS/plugin-host/Tokenmetry.PluginHost").Open())
            {
                hostStream.Write("host"u8);
            }
        }

        var stager = new UpdateStager(os: "OSX", processId: 1);
        var prepared = stager.PreparePortable("v0.6.0", archive, binary);

        stager.Apply(prepared);

        // 宿主在 .app 内随整包替换；zip 解包丢了 +x，更新后必须补回来，否则宿主起不来。
        var nestedHost = Path.Combine(
            bundle, "Contents", "MacOS", "plugin-host", "Tokenmetry.PluginHost");
        File.Exists(nestedHost).Should().BeTrue();
        (File.GetUnixFileMode(nestedHost) & UnixFileMode.UserExecute)
            .Should().NotBe((UnixFileMode)0, "嵌套插件宿主必须可执行");
    }

    [Fact]
    public void Apply_rollback_preserves_the_original_error_when_cleanup_and_restore_fail()
    {
        var target = Path.Combine(_directory, "Tokenmetry");
        File.WriteAllText(target, "old-binary");
        var oldHost = Path.Combine(_directory, "plugin-host");
        Directory.CreateDirectory(oldHost);
        File.WriteAllText(Path.Combine(oldHost, "old-host.txt"), "old-host");

        var archive = WriteArchive(
            ("Tokenmetry", [0x7F, 0x45, 0x4C, 0x46, 1]),
            ("plugin-host/new-host.txt", "new-host"u8.ToArray()));
        var stager = new FaultInjectingStager(target)
        {
            FailTargetDelete = true,
            FailHostRestore = true,
        };
        var prepared = stager.PreparePortable("v0.6.0", archive, target);

        // 主失败：新宿主搬不进来（暂存目录消失）→ DirectoryNotFoundException。
        Directory.Delete(prepared.StagedHostDirectory!, recursive: true);

        var act = () => stager.Apply(prepared);

        // 清理新 exe 的 Delete 抛 UnauthorizedAccessException、宿主恢复也失败：
        // 两者都不得替换原始异常（旧代码分别会让 AV 逃逸 / 让恢复异常盖住原因）。
        act.Should().Throw<DirectoryNotFoundException>();
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

    /// <summary>回滚路径的故障注入：模拟杀软锁住新 exe（Delete 抛 AV）与宿主恢复失败。</summary>
    private sealed class FaultInjectingStager(string targetPath) : UpdateStager(os: "Linux", processId: 1)
    {
        public bool FailTargetDelete { get; init; }

        public bool FailHostRestore { get; init; }

        protected override void Delete(string path, bool isDirectory)
        {
            if (FailTargetDelete && !isDirectory
                && string.Equals(path, targetPath, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("simulated AV lock");
            }

            base.Delete(path, isDirectory);
        }

        protected override void Move(string source, string destination, bool isDirectory)
        {
            if (FailHostRestore && isDirectory
                && string.Equals(Path.GetFileName(source), "plugin-host.old", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated host restore failure");
            }

            base.Move(source, destination, isDirectory);
        }
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

    private string WriteArchive(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(_directory, $"pkg-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }
}
