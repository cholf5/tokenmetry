using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 更新服务状态机：检查 → Available → 下载 + SHA-256 校验 + 解包 → ReadyToRestart → 落地并重启；
/// 以及启动时对更新日志的解读（成功提示 / 上次未生效）。IAppSettingsStore 用内存 fake，不触盘。
/// </summary>
public class UpdateServiceTests
{
    private static (UpdateService Service, FakeStore Store) Create(
        Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> responder,
        Action<string>? openUrl = null,
        UpdateDownloadService? downloader = null)
    {
        var settings = new AppSettings();
        var store = new FakeStore();
        var service = new UpdateService(
            new UpdateChecker(new FakeUpdateHttpHandler(responder), currentVersion: "v0.2.0"),
            settings,
            store,
            downloader,
            openUrl ?? (_ => { }));
        return (service, store);
    }

    [Fact]
    public async Task CheckNowAsync_available_sets_state_result_and_persists_timestamp()
    {
        var changed = 0;
        var (service, store) = Create(_ =>
            FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0"));
        service.Changed += () => changed++;

        var result = await service.CheckNowAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        service.State.Should().Be(UpdateState.Available);
        service.LastResult!.LatestVersion.Should().Be("v0.3.0");
        store.SaveCalls.Should().Be(1);
        store.Settings!.LastUpdateCheckUtc.Should().NotBeNull();
        changed.Should().BeGreaterThanOrEqualTo(2); // Checking + Available 各一次
    }

    [Fact]
    public async Task CheckNowAsync_up_to_date_and_failure_states()
    {
        var (upToDate, _) = Create(_ =>
            FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.2.0"));
        (await upToDate.CheckNowAsync()).Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
        upToDate.State.Should().Be(UpdateState.UpToDate);

        var (failed, _) = Create(_ => FakeUpdateHttpHandler.Status(System.Net.HttpStatusCode.Forbidden));
        (await failed.CheckNowAsync()).Outcome.Should().Be(UpdateCheckOutcome.Failed);
        failed.State.Should().Be(UpdateState.Failed);
    }

    [Fact]
    public async Task OpenReleasePage_uses_checked_release_url_then_falls_back_to_list_page()
    {
        string? opened = null;
        var (service, _) = Create(_ =>
            FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0"),
            openUrl: url => opened = url);

        service.OpenReleasePage(); // 未检查过：回落列表页
        opened.Should().Be(UpdateChecker.ReleasesPageUrl);

        await service.CheckNowAsync();
        service.OpenReleasePage(); // 已发现新版本：直达该 Release 页
        opened.Should().Be("https://github.com/cholf5/tokenmetry/releases/tag/v0.3.0");
    }

    [Fact]
    public async Task DownloadAsync_requires_available_state()
    {
        var (service, _) = Create(_ => FakeUpdateHttpHandler.Empty());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync());
    }

    [Fact]
    public async Task DownloadAsync_verifies_checksum_stages_the_payload_and_reaches_ready()
    {
        using var fixture = new SwapFixture();
        var service = fixture.CreateService();

        await service.CheckNowAsync();
        var reported = new List<double>();
        await service.DownloadAsync(new ImmediateProgress(reported.Add));

        service.State.Should().Be(UpdateState.ReadyToRestart);
        service.Prepared.Should().NotBeNull();
        service.Prepared!.Mode.Should().Be(UpdateApplyMode.PortableSwap);
        File.Exists(service.Prepared!.StagedPayloadPath!).Should().BeTrue();
        service.Prepared!.StagedPayloadPath.Should().EndWith("Tokenmetry.exe");
        reported.Should().NotBeEmpty();
        reported.Should().Contain(100d);
    }

    [Fact]
    public async Task DownloadAsync_rejects_a_payload_whose_hash_does_not_match_and_deletes_it()
    {
        using var fixture = new SwapFixture();
        var path = Path.Combine(fixture.DownloadDirectory, UpdateAssets.AssetFileName("v0.3.0"));
        var (service, _) = Create(
            _ => FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0"),
            downloader: fixture.Downloader(fixture.ValidZipBytes(), new string('0', 64)));

        await service.CheckNowAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync());
        service.State.Should().Be(UpdateState.Available); // 可重试，不是死局
        File.Exists(path).Should().BeFalse("攻击载荷不能留在下载文件夹里等人双击");
    }

    [Fact]
    public async Task DownloadAsync_fails_when_no_checksum_is_published()
    {
        using var fixture = new SwapFixture();
        var (service, _) = Create(
            _ => FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0"),
            downloader: fixture.Downloader(fixture.ValidZipBytes(), checksumOverride: "<html>not a checksum</html>"));

        await service.CheckNowAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync());
    }

    // ---- 落地 ----

    [Fact]
    public async Task DownloadAsync_falls_back_to_revealing_the_package_when_the_folder_is_not_writable()
    {
        using var fixture = new SwapFixture();
        var revealed = new List<string>();
        var service = new UpdateService(
            new UpdateChecker(
                new FakeUpdateHttpHandler(_ =>
                    FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0")),
                "v0.2.0"),
            new AppSettings(),
            new FakeStore(),
            fixture.Downloader(fixture.ValidZipBytes(), reveal: revealed.Add),
            _ => { },
            installer: null,
            windowsInstalledCopy: false,
            stager: new ReadOnlyStager(),
            journal: fixture.Journal,
            processPath: fixture.Target);

        await service.CheckNowAsync();

        var act = async () => await service.DownloadAsync();
        (await act.Should().ThrowAsync<UpdateStageUnavailableException>())
            .Which.ArchivePath.Should().Be(Path.Combine(fixture.DownloadDirectory, UpdateAssets.AssetFileName("v0.3.0")));
        revealed.Should().ContainSingle(); // 「目录不可写」退回到下载 + 打开所在位置，不是死路
        service.State.Should().Be(UpdateState.Available);
    }

    [Fact]
    public async Task RestartAndApply_installed_copy_runs_the_silent_installer_and_signals_restart()
    {
        using var fixture = new SwapFixture();
        var started = new List<(string Path, string Args)>();
        var service = fixture.CreateService(windowsInstalledCopy: true, installer: new UpdateInstaller((path, args) =>
        {
            started.Add((path, args));
            return true;
        }));
        var restarted = false;
        service.RestartRequested += () => restarted = true;

        await service.CheckNowAsync();
        await service.DownloadAsync();
        service.RestartAndApply();

        service.State.Should().Be(UpdateState.Restarting);
        started.Should().ContainSingle();
        Path.GetFileName(started[0].Path).Should().Be("Tokenmetry-0.3.0-win-x64-setup.exe");
        started[0].Args.Should().Be(UpdateInstaller.SilentArguments);
        restarted.Should().BeTrue();
    }

    [Fact]
    public async Task RestartAndApply_portable_swaps_the_files_and_signals_restart()
    {
        using var fixture = new SwapFixture();
        var stager = fixture.CreateStager();
        var service = fixture.CreateService(stager: stager);
        var restarted = false;
        service.RestartRequested += () => restarted = true;

        await service.CheckNowAsync();
        await service.DownloadAsync();
        service.RestartAndApply();

        stager.Applied.Should().BeTrue();
        stager.Relaunched.Should().BeTrue();
        restarted.Should().BeTrue();
        // 目标已被新版本替换，旧版本留在 .old 里（下次成功启动后清理）。
        File.ReadAllBytes(fixture.Target).Should().Equal(fixture.NewPayload);
        File.Exists(fixture.Target + ".old").Should().BeTrue();
        File.ReadAllBytes(fixture.Target + ".old").Should().Equal(fixture.OldPayload);
    }

    [Fact]
    public async Task RestartAndApply_failure_keeps_the_old_binary_and_the_ready_state()
    {
        using var fixture = new SwapFixture();
        var stager = fixture.CreateStager(failApply: true);
        var service = fixture.CreateService(stager: stager);
        var restarted = false;
        service.RestartRequested += () => restarted = true;

        await service.CheckNowAsync();
        await service.DownloadAsync();

        Assert.ThrowsAny<IOException>(service.RestartAndApply);

        restarted.Should().BeFalse();
        service.State.Should().Be(UpdateState.ReadyToRestart);
        File.ReadAllBytes(fixture.Target).Should().Equal(fixture.OldPayload);
        // 替换没发生 → 日志必须清掉，否则下次启动会误报「上次更新未生效」。
        fixture.Journal.Load().Should().BeNull();
    }

    [Fact]
    public void RestartAndApply_requires_a_prepared_update()
    {
        using var fixture = new SwapFixture();
        var service = fixture.CreateService();

        Assert.Throws<InvalidOperationException>(service.RestartAndApply);
    }

    // ---- 启动解读更新日志 ----

    [Fact]
    public async Task Startup_notice_reports_success_and_cleans_the_journal_and_backup()
    {
        using var fixture = new SwapFixture();
        var journal = fixture.Journal;
        journal.Save(new UpdateJournalRecord
        {
            PendingVersion = "v0.3.0",
            BackupPath = fixture.Backup,
            TargetPath = fixture.Target,
        });
        File.WriteAllText(fixture.Backup, "old");

        var service = fixture.CreateService(currentVersion: "v0.3.0");

        service.StartupNotice.Outcome.Should().Be(UpdateStartupOutcome.Upgraded);
        service.StartupNotice.Version.Should().Be("v0.3.0");
        journal.Load().Should().BeNull();
        File.Exists(fixture.Backup).Should().BeFalse();
    }

    [Fact]
    public void Startup_notice_reports_a_failed_swap_and_keeps_the_backup_path()
    {
        using var fixture = new SwapFixture();
        fixture.Journal.Save(new UpdateJournalRecord
        {
            PendingVersion = "v0.3.0",
            BackupPath = fixture.Backup,
            TargetPath = fixture.Target,
        });
        File.WriteAllText(fixture.Backup, "old");

        var service = fixture.CreateService(currentVersion: "v0.2.0");

        service.StartupNotice.Outcome.Should().Be(UpdateStartupOutcome.ApplyFailed);
        service.StartupNotice.BackupPath.Should().Be(fixture.Backup);
        fixture.Journal.Load().Should().NotBeNull(); // 保留：用户可能还要手动恢复
        File.Exists(fixture.Backup).Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoCheck_throttles_by_persisted_timestamp()
    {
        var settings = new AppSettings();
        var service = new UpdateService(
            new UpdateChecker(),
            settings,
            new FakeStore());

        service.ShouldAutoCheck(TimeSpan.FromDays(7)).Should().BeTrue(); // 从未检查过

        settings.LastUpdateCheckUtc = DateTime.UtcNow.AddDays(-1);
        service.ShouldAutoCheck(TimeSpan.FromDays(7)).Should().BeFalse(); // 一周内查过

        settings.LastUpdateCheckUtc = DateTime.UtcNow.AddDays(-8);
        service.ShouldAutoCheck(TimeSpan.FromDays(7)).Should().BeTrue(); // 过期
    }

    // ---- 夹具 ----

    /// <summary>真文件系统的便携形态夹具：临时目录里有「旧 exe」、合法的更新 zip、更新日志，全用完即删。</summary>
    private sealed class SwapFixture : IDisposable
    {
        private readonly string _directory;

        public SwapFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-swap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            DownloadDirectory = Path.Combine(_directory, "downloads");
            Target = Path.Combine(_directory, "Tokenmetry.exe");
            Backup = Target + ".old";
            OldPayload = "old-binary"u8.ToArray();
            NewPayload = [0x4D, 0x5A, .. "new-binary"u8.ToArray()];
            File.WriteAllBytes(Target, OldPayload);
            Journal = new UpdateJournal(Path.Combine(_directory, "update-state.json"));
        }

        public string DownloadDirectory { get; }

        public string Target { get; }

        public string Backup { get; }

        public byte[] OldPayload { get; }

        public byte[] NewPayload { get; }

        public UpdateJournal Journal { get; }

        /// <summary>合法的便携包：单个 <c>Tokenmetry.exe</c>，PE 头（解包器按魔数校验平台产物）。</summary>
        /// <summary>安装器形态的合法载荷：PE 头（下载器按资产后缀分流校验）。</summary>
        public static byte[] PePayload() => [0x4D, 0x5A, 1, 2, 3];

        public byte[] ValidZipBytes()
        {
            var zipPath = Path.Combine(_directory, "pkg.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Tokenmetry.exe");
                using var stream = entry.Open();
                stream.Write(NewPayload);
            }

            return File.ReadAllBytes(zipPath);
        }

        public UpdateDownloadService Downloader(byte[] payload, string? checksumOverride = null,
            Action<string>? reveal = null) =>
            new(UpdateServiceTestsHelper.AssetAndChecksum(payload, checksumOverride),
                downloadDirectory: DownloadDirectory,
                revealOverride: reveal ?? (_ => { }));

        public RecordingStager CreateStager(bool failApply = false) => new(failApply);

        public UpdateService CreateService(
            bool? windowsInstalledCopy = null,
            UpdateInstaller? installer = null,
            RecordingStager? stager = null,
            string currentVersion = "v0.2.0")
        {
            var payload = windowsInstalledCopy == true ? PePayload() : ValidZipBytes();
            var service = new UpdateService(
                new UpdateChecker(
                    new FakeUpdateHttpHandler(_ =>
                        FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0")),
                    currentVersion),
                new AppSettings(),
                new FakeStore(),
                Downloader(payload),
                _ => { },
                installer,
                windowsInstalledCopy ?? false,
                stager ?? new RecordingStager(),
                Journal,
                Target);
            return service;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不构成测试失败（Windows 文件锁抖动）。
            }
        }
    }

    /// <summary>内存版设置存储：记录保存次数与本进程写入的对象（不触盘）。</summary>
    private sealed class FakeStore : IAppSettingsStore
    {
        public int SaveCalls { get; private set; }

        public AppSettings? Settings { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCalls++;
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>同步回传的进度（<see cref="Progress{T}"/> 在没有同步上下文时会异步投递，断言会跑在回调前面）。</summary>
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>目标目录不可写（/Applications 只读、企业策略）：PreparePortable 抛 UnauthorizedAccessException。</summary>
    private sealed class ReadOnlyStager() : UpdateStager(os: "Linux", processId: 1)
    {
        public override PreparedUpdate PreparePortable(string tag, string archivePath, string processPath) =>
            throw new UnauthorizedAccessException($"read-only: {Path.GetDirectoryName(processPath)}");
    }

    /// <summary>真替换、假重启：测试里绝不能真去启动一个进程。</summary>
    private sealed class RecordingStager(bool failApply = false) : UpdateStager(os: "Linux", processId: 1)
    {
        public bool Applied { get; private set; }

        public bool Relaunched { get; private set; }

        public override void Apply(PreparedUpdate update)
        {
            if (failApply)
            {
                throw new IOException("swap failed");
            }

            base.Apply(update);
            Applied = true;
        }

        public override bool Relaunch(PreparedUpdate update)
        {
            Relaunched = true;
            return true;
        }
    }
}
