namespace Tokenmetry.App.Headless.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Cache;
using Xunit;

/// <summary>
/// 设置页「跨设备同步」区块（relay 计划 Task 12）：未配置态显示启用提示、
/// 自建通道填地址 + token 即启用并出现设备清单、移除设备走确认流程。
/// 断言语言无关（引用 Translations.Instance 同源属性）。
/// </summary>
public sealed class SettingsSyncBlockTests
{
    private sealed class FakeAutoStartManager : IAutoStartManager
    {
        public void SetEnabled(bool enabled)
        {
        }
    }

    /// <summary>同步出站 fake：仅计数（本套件不验证协议，Task 9 已覆盖引擎语义）。</summary>
    private sealed class FakeSyncBackend : ISyncBackend
    {
        public int RemoveCount;

        public Task PushAsync(SyncPushRequest request, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default) =>
            Task.FromResult(new SyncPullResponse(
                Seq: 1,
                UsageFacts: new List<UsageFact>(),
                SessionFacts: new List<SessionFact>(),
                Devices: new List<SyncDeviceInfo>()));

        public Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult(new SyncDeviceListResponse(new List<SyncDeviceInfo>()));

        public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            RemoveCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAccountAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(false, null));

        public Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(request.OptIn, request.DisplayName));
    }

    private sealed class SyncFixture : IDisposable
    {
        public AppSettings Settings { get; } = new();
        public FakeSyncBackend Backend { get; } = new();
        public SqliteUsageStore Store { get; }
        public SyncStateService SyncState { get; }
        public SettingsViewModel ViewModel { get; }
        private readonly string _directory;

        public SyncFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-settings-sync-{Guid.NewGuid():N}");
            var paths = new AppPaths(_directory);
            Directory.CreateDirectory(paths.AppDataDirectory);
            Store = new SqliteUsageStore(paths.CacheDbPath);
            Store.InitializeAsync().GetAwaiter().GetResult();
            SyncState = new SyncStateService(
                Store,
                Settings,
                new JsonAppSettingsStore(paths),
                backendFactory: (_, _) => Backend,
                timerPeriod: Timeout.InfiniteTimeSpan);
            ViewModel = new SettingsViewModel(
                Settings,
                paths,
                new JsonAppSettingsStore(paths),
                new ThemeService(),
                new FakeAutoStartManager(),
                syncStateService: SyncState);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Window Show(SettingsViewModel vm)
    {
        var window = new Window { Width = 960, Height = 900, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    [AvaloniaFact]
    public async Task SyncBlock_HiddenWhenNeverConfigured_ShowsEnableHint()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        var window = Show(fixture.ViewModel);

        var hint = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SyncDisabledHint);
        Assert.True(hint.IsVisible, "未配置态显示启用提示");
        Assert.Empty(fixture.ViewModel.DeviceRows); // 尚未启用，无设备清单
    }

    [AvaloniaFact]
    public async Task SelfHostFlow_PasteTokenEnable_ShowsDeviceList()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        var window = Show(fixture.ViewModel);

        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        fixture.ViewModel.IsOfficialMode = false;

        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(fixture.ViewModel.IsSyncEnabled, "自建通道启用成功");
        var localDeviceId = await fixture.Store.GetLocalDeviceIdAsync();
        var row = fixture.ViewModel.DeviceRows.Single(r => r.IsLocal);
        Assert.Equal(localDeviceId, row.DeviceId);

        var hint = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SyncDisabledHint);
        Assert.False(ChartTestHelpers.IsEffectivelyVisible(hint), "启用后隐藏启用提示");
    }

    [AvaloniaFact]
    public async Task RemoveDevice_ShowsConfirmationDialog()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        await fixture.SyncState.EnableAsync("http://127.0.0.1:8080", "relay-token");
        var at = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        await fixture.Store.UpsertRemoteDevicesAsync(new List<SyncDeviceInfo> { new("devB", "Laptop B", at) });
        Show(fixture.ViewModel);
        // devB 是 Enable 之后直写 store 的（绕过同步引擎）：手动触发 VM 的状态装载（Changed 内部同款）。
        await fixture.ViewModel.RefreshSyncStateAsync();

        var row = fixture.ViewModel.DeviceRows.Single(r => r.DeviceId == "devB");
        fixture.ViewModel.RemoveDeviceCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.ViewModel.IsRemoveConfirmVisible, "移除前先确认（A16）");
        Assert.Equal(0, fixture.Backend.RemoveCount); // 确认前不出站

        await fixture.ViewModel.ConfirmRemoveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Backend.RemoveCount);
        Assert.False(fixture.ViewModel.IsRemoveConfirmVisible);
        Assert.DoesNotContain("devB", fixture.ViewModel.DeviceRows.Select(r => r.DeviceId));
    }

    public void Dispose()
    {
    }
}
