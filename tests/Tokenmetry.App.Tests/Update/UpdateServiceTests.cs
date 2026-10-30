using System;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 更新服务状态机（I-039）：检查 → 状态/结果/时间戳落盘 → Changed 通知；启动周检门槛
/// ShouldAutoCheck；下载仅在 Available 态放行。IAppSettingsStore 用内存 fake，不触盘。
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
    public async Task DownloadLatestAsync_requires_available_state()
    {
        var (service, _) = Create(_ => FakeUpdateHttpHandler.Empty());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadLatestAsync());
    }

    [Fact]
    public async Task DownloadLatestAsync_in_available_state_uses_downloader()
    {
        var (service, _) = Create(
            _ => FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0"),
            downloader: new UpdateDownloadService(
                // 载荷需形似 zip：下载器现在校验 ZIP 魔数（HTML 限流页不能被当安装包）。
                new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Bytes(new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 })),
                downloadDirectory: Path.Combine(Path.GetTempPath(), $"tokenmetry-svc-tests-{Guid.NewGuid():N}")));

        await service.CheckNowAsync();

        var path = await service.DownloadLatestAsync();
        Path.GetFileName(path).Should().Be(UpdateAssets.AssetFileName("v0.3.0"));
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

    // ---- Windows 安装器静默升级（P-008）----

    /// <summary>已安装副本 / 便携版两条分支同一构造，差别只在 windowsInstalledCopy。</summary>
    private static async Task<(UpdateService Service, List<(string Path, string Args)> Started)> CreateForInstall(
        bool installedCopy)
    {
        var started = new List<(string Path, string Args)>();
        var service = new UpdateService(
            new UpdateChecker(
                new FakeUpdateHttpHandler(_ =>
                    FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0")),
                "v0.2.0"),
            new AppSettings(),
            new FakeStore(),
            new UpdateDownloadService(
                // 已安装副本下的是安装器（PE 头），便携版下的是 zip（ZIP 头）；分支各用各的合法载荷。
                new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Bytes(installedCopy
                    ? new byte[] { 0x4D, 0x5A, 1, 2, 3 }
                    : new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 })),
                downloadDirectory: Path.Combine(Path.GetTempPath(), $"tokenmetry-install-tests-{Guid.NewGuid():N}")),
            _ => { },
            new UpdateInstaller((path, args) =>
            {
                started.Add((path, args));
                return true;
            }),
            windowsInstalledCopy: installedCopy);
        await service.CheckNowAsync();
        return (service, started);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_installed_copy_runs_silent_install_and_signals_shutdown()
    {
        var (service, started) = await CreateForInstall(installedCopy: true);
        var shutdownRequested = false;
        service.InstallStarted += () => shutdownRequested = true;

        var result = await service.DownloadAndInstallAsync();

        result.InstallStarted.Should().BeTrue();
        Path.GetFileName(result.Path).Should().Be("Tokenmetry-0.3.0-win-x64-setup.exe");
        started.Should().ContainSingle();
        started[0].Path.Should().Be(result.Path);
        started[0].Args.Should().Be(UpdateInstaller.SilentArguments);
        // 安装器要替换正在跑的文件：不发这个信号就等于让旧实例和安装器抢锁。
        shutdownRequested.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadAndInstallAsync_portable_copy_just_downloads_the_zip()
    {
        var (service, started) = await CreateForInstall(installedCopy: false);
        var shutdownRequested = false;
        service.InstallStarted += () => shutdownRequested = true;

        var result = await service.DownloadAndInstallAsync();

        result.InstallStarted.Should().BeFalse();
        Path.GetFileName(result.Path).Should().Be(UpdateAssets.AssetFileName("v0.3.0"));
        started.Should().BeEmpty();          // 便携版永远不拉起安装器（不静默改变用户的安装形态）
        shutdownRequested.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadAndInstallAsync_falls_back_to_the_download_when_the_installer_will_not_start()
    {
        var started = new List<string>();
        var service = new UpdateService(
            new UpdateChecker(
                new FakeUpdateHttpHandler(_ =>
                    FakeUpdateHttpHandler.Redirect("/cholf5/tokenmetry/releases/tag/v0.3.0")),
                "v0.2.0"),
            new AppSettings(),
            new FakeStore(),
            new UpdateDownloadService(
                new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Bytes(new byte[] { 0x4D, 0x5A, 1, 2, 3 })),
                downloadDirectory: Path.Combine(Path.GetTempPath(), $"tokenmetry-install-tests-{Guid.NewGuid():N}")),
            _ => { },
            // 安装器拉不起来（UAC / 杀软拦截 / 文件被删）：必须退回「下载完在文件管理器里显示」，
            // 不能把已下好的包白白丢掉，更不能照样 Shutdown。
            new UpdateInstaller((_, _) => false),
            windowsInstalledCopy: true);
        await service.CheckNowAsync();
        var shutdownRequested = false;
        service.InstallStarted += () => { shutdownRequested = true; started.Add("shutdown"); };

        var result = await service.DownloadAndInstallAsync();

        result.InstallStarted.Should().BeFalse(); // 退回「下载完在文件管理器里显示」
        result.Path.Should().NotBeNull();
        shutdownRequested.Should().BeFalse();
    }

    private sealed class FakeStore : IAppSettingsStore
    {
        public AppSettings? Settings { get; private set; }
        public int SaveCalls { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Settings ?? new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            Settings = settings;
            SaveCalls++;
            return Task.CompletedTask;
        }
    }
}
