using System;
using System.IO;
using System.Threading.Tasks;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Update;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 主窗更新横幅的状态映射（状态机 → 「用户看到什么」）：发现新版本 / 下载失败 / 更新成功收尾 /
/// 上次更新未生效。视图本身只绑属性，所以这里是「提醒是否到位」的唯一证据。
/// </summary>
public class UpdateBannerViewModelTests : IDisposable
{
    private readonly string _directory;

    public UpdateBannerViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-banner-{Guid.NewGuid():N}");
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

    private UpdateBannerViewModel CreateBanner(UpdateService service)
    {
        var banner = new UpdateBannerViewModel(service) { UiPost = action => action() };
        return banner;
    }

    [Fact]
    public async Task Available_state_shows_the_new_version_with_an_update_button()
    {
        var service = UpdateServiceTestsHelper.CreateService("v0.3.0", "v0.2.0");
        var banner = CreateBanner(service);

        banner.IsVisible.Should().BeFalse(); // Idle：什么都不显示

        await service.CheckNowAsync();

        banner.IsVisible.Should().BeTrue();
        banner.Title.Should().Contain("0.3.0");
        banner.PrimaryLabel.Should().Be(Translations.Instance.ActionUpdateNow);
        banner.SecondaryLabel.Should().Be(Translations.Instance.ActionLater);
        banner.IsProgressVisible.Should().BeFalse();
        banner.IsToneDanger.Should().BeFalse();
    }

    [Fact]
    public async Task Later_hides_the_banner_and_the_same_version_stays_quiet()
    {
        var service = UpdateServiceTestsHelper.CreateService("v0.3.0", "v0.2.0");
        var banner = CreateBanner(service);
        await service.CheckNowAsync();

        banner.SecondaryCommand.Execute(null);

        banner.IsVisible.Should().BeFalse();

        await service.CheckNowAsync(); // 同一个版本再查：不再打扰
        banner.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Download_failure_surfaces_in_the_banner_and_keeps_the_retry_button()
    {
        var downloader = new UpdateDownloadService(
            UpdateServiceTestsHelper.AssetAndChecksum([0x50, 0x4B, 0x03, 0x04, 1], new string('a', 64)),
            downloadDirectory: Path.Combine(_directory, "downloads"),
            revealOverride: _ => { });
        var service = UpdateServiceTestsHelper.CreateService("v0.3.0", "v0.2.0", downloader: downloader);
        var banner = CreateBanner(service);
        await service.CheckNowAsync();

        banner.PrimaryCommand.Execute(null);
        await WaitForAsync(() => banner.IsToneDanger);

        banner.IsVisible.Should().BeTrue();
        banner.HasDetail.Should().BeTrue();
        banner.PrimaryLabel.Should().Be(Translations.Instance.ActionUpdateNow);
    }

    [Fact]
    public void Successful_upgrade_notice_shows_a_success_banner_that_can_be_dismissed()
    {
        var journalPath = Path.Combine(_directory, "update-state.json");
        new UpdateJournal(journalPath).Save(new UpdateJournalRecord { PendingVersion = "v0.3.0" });
        var service = new UpdateService(
            new UpdateChecker(currentVersion: "v0.3.0"),
            new AppSettings(),
            new UpdateServiceTestsHelper.FakeStore(),
            journal: new UpdateJournal(journalPath));

        var banner = CreateBanner(service);

        banner.IsVisible.Should().BeTrue();
        banner.IsToneSuccess.Should().BeTrue();
        banner.Title.Should().Contain("0.3.0");

        banner.SecondaryCommand.Execute(null);
        banner.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Failed_swap_notice_shows_a_warning_with_the_backup_path()
    {
        var journalPath = Path.Combine(_directory, "update-state.json");
        var backup = Path.Combine(_directory, "Tokenmetry.old");
        new UpdateJournal(journalPath).Save(new UpdateJournalRecord
        {
            PendingVersion = "v0.3.0",
            BackupPath = backup,
        });
        var service = new UpdateService(
            new UpdateChecker(currentVersion: "v0.2.0"),
            new AppSettings(),
            new UpdateServiceTestsHelper.FakeStore(),
            journal: new UpdateJournal(journalPath));

        var banner = CreateBanner(service);

        banner.IsVisible.Should().BeTrue();
        banner.IsToneWarning.Should().BeTrue();
        banner.Title.Should().Be(Translations.Instance.BannerApplyFailedTitle);
        banner.Detail.Should().Contain(backup);
        banner.SecondaryLabel.Should().Be(Translations.Instance.ActionRevealBackup);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
