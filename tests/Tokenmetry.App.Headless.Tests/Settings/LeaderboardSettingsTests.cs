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
/// 设置页「排行榜」子区（leaderboard 计划 Task 5）：同步已连接时可见、开关与展示名投影 GET 偏好、
/// 拨关立即 PUT optIn=false 并清展示、保存 PUT 带修整后的名字；未连接时不渲染（随已启用分支隐藏）。
/// 断言语言无关（引用 Translations.Instance 同源属性）。
/// </summary>
public sealed class LeaderboardSettingsTests
{
    private sealed class FakeAutoStartManager : IAutoStartManager
    {
        public void SetEnabled(bool enabled)
        {
        }
    }

    /// <summary>同步出站 fake：仅记录排行榜偏好往返（协议语义 Task 4 已覆盖）。PUT 响应镜像真实
    /// 服务端语义（Relay LeaderboardEndpoints）：响应携带生效名回落、永非 null——opt-in =
    /// 修整后的自定义名 ?? 账号默认名；opt-out = 关闭即删（不留自定义名）→ 账号默认名。
    /// 账号默认名取 "self-hosted"，与中继 shared 账号的 accounts.display_name 默认同值。
    /// 诚实性关键：opt-out 回落名 ≠ null，客户端若把响应名无条件回填输入框（违背设计 §4.4
    /// 「拨关清展示」），ToggleOff 用例即红。</summary>
    private sealed class FakeSyncBackend : ISyncBackend
    {
        public const string AccountDefaultDisplayName = "self-hosted";

        public LeaderboardPreferenceResponse Preference { get; set; } = new(false, null);

        public List<LeaderboardPreferenceRequest> PreferenceRequests { get; } = new();

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

        public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAccountAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
            Task.FromResult(Preference);

        public Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default)
        {
            PreferenceRequests.Add(request);
            // 镜像服务端 PUT（LeaderboardEndpoints）：生效名 = opt-in 自定义名（trim） ?? 账号默认名；
            // opt-out 关闭即删 → 只有账号默认名回落。响应 DisplayName 永非 null，同真服务端。
            var custom = request.OptIn && !string.IsNullOrWhiteSpace(request.DisplayName)
                ? request.DisplayName!.Trim()
                : null;
            Preference = new LeaderboardPreferenceResponse(request.OptIn, custom ?? AccountDefaultDisplayName);
            return Task.FromResult(Preference);
        }
    }

    private sealed class LeaderboardFixture : IDisposable
    {
        public AppSettings Settings { get; } = new();
        public FakeSyncBackend Backend { get; } = new();
        public SqliteUsageStore Store { get; }
        public SyncStateService SyncState { get; }
        public SettingsViewModel ViewModel { get; }
        private readonly string _directory;

        public LeaderboardFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-settings-leaderboard-{Guid.NewGuid():N}");
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

        /// <summary>启用同步并显式装载（子区内容依赖 GET 偏好，直调 VM 的统一装载入口——
        /// Changed 的 Dispatcher.Post 路径时序对断言不够确定）。</summary>
        public async Task EnableSyncAsync()
        {
            await SyncState.EnableAsync("http://127.0.0.1:8080", "relay-token");
            await ViewModel.RefreshSyncStateAsync();
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

    private static TextBlock HintText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);

    private static Button ContentButton(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);

    [AvaloniaFact]
    public async Task SyncEnabled_ShowsSection_ProjectsOptInAndName_LinkAndConstantPresent()
    {
        using var fixture = new LeaderboardFixture();
        fixture.Backend.Preference = new LeaderboardPreferenceResponse(true, "alice");
        await fixture.EnableSyncAsync();
        var window = Show(fixture.ViewModel);

        // 子区标题与说明可见（语言无关：与 XAML 绑定同源 Translations.Instance）
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(HintText(window, Translations.Instance.LeaderboardSection)), "同步已连接时子区可见");
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(HintText(window, Translations.Instance.LeaderboardOptInHint)), "隐私说明可见");
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(HintText(window, Translations.Instance.LeaderboardDisplayName)), "展示名标签可见");

        // 开关与名字框投影 GET 偏好（生效名直入输入框）
        Assert.True(fixture.ViewModel.LeaderboardOptIn, "开关投影 opt-in=true");
        Assert.Equal("alice", fixture.ViewModel.LeaderboardDisplayNameInput);

        // 保存按钮与「在网页查看」链接按钮存在且可见
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(ContentButton(window, Translations.Instance.LeaderboardSave)), "保存按钮可见");
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(ContentButton(window, Translations.Instance.LeaderboardViewOnWeb)), "在网页查看按钮可见");

        // 官网榜单 URL 常量（Task 6 站点页同一路径）
        Assert.Equal("https://tokenmetry.app/leaderboard", SettingsViewModel.LeaderboardUrl);
    }

    [AvaloniaFact]
    public async Task SyncNotEnabled_SectionHiddenAndInputCleared()
    {
        using var fixture = new LeaderboardFixture();
        fixture.Backend.Preference = new LeaderboardPreferenceResponse(true, "alice");
        await fixture.SyncState.InitializeAsync();
        await fixture.ViewModel.RefreshSyncStateAsync();
        var window = Show(fixture.ViewModel);

        // 子区节点仍在树里（随「已启用」分支整体隐藏），但不可见
        var section = HintText(window, Translations.Instance.LeaderboardSection);
        Assert.False(ChartTestHelpers.IsEffectivelyVisible(section), "同步未连接时子区不可见（G3 之外的诚实空态）");

        // 关闭时清空输入（偏好唯一事实源在中继，无后端即无意义）
        Assert.Equal(string.Empty, fixture.ViewModel.LeaderboardDisplayNameInput);
        Assert.False(fixture.ViewModel.LeaderboardOptIn);
        Assert.Empty(fixture.Backend.PreferenceRequests); // 未连接不出站
    }

    [AvaloniaFact]
    public async Task ToggleOff_PutsOptOutImmediately_AndClearsDisplay()
    {
        using var fixture = new LeaderboardFixture();
        fixture.Backend.Preference = new LeaderboardPreferenceResponse(true, "alice");
        await fixture.EnableSyncAsync();
        Show(fixture.ViewModel);

        fixture.ViewModel.LeaderboardOptIn = false;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // 拨关 = 立即 PUT optIn=false（服务端「关闭即删」语义的入口），不带名字
        var request = Assert.Single(fixture.Backend.PreferenceRequests);
        Assert.False(request.OptIn);
        Assert.Null(request.DisplayName);

        // 清展示（设计 §4.4）：fake 响应已镜像服务端回落（optIn=false → 生效名 = 账号默认名
        // "self-hosted"，永非 null）——客户端语义是清空输入框而非回填回落名；防抖结束。
        Assert.Equal(string.Empty, fixture.ViewModel.LeaderboardDisplayNameInput);
        Assert.False(fixture.ViewModel.LeaderboardOptIn);
        Assert.False(fixture.ViewModel.LeaderboardBusy);
    }

    [AvaloniaFact]
    public async Task ToggleOn_UnlocksSave_SavePutsTrimmedName_AndWritesBackEffectiveName()
    {
        using var fixture = new LeaderboardFixture();
        fixture.Backend.Preference = new LeaderboardPreferenceResponse(false, null);
        await fixture.EnableSyncAsync();
        Show(fixture.ViewModel);

        // 拨开 = 仅解锁名字输入与保存按钮，不出站
        fixture.ViewModel.LeaderboardOptIn = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fixture.Backend.PreferenceRequests);
        Assert.True(fixture.ViewModel.SaveLeaderboardCommand.CanExecute(null), "拨开后保存按钮解锁");

        // 保存 = PUT {OptIn=true, DisplayName=trim 或 null}；响应生效名回写输入框
        fixture.ViewModel.LeaderboardDisplayNameInput = "  Alice 的工厂  ";
        await fixture.ViewModel.SaveLeaderboardCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(fixture.Backend.PreferenceRequests);
        Assert.True(request.OptIn);
        Assert.Equal("Alice 的工厂", request.DisplayName);
        Assert.Equal("Alice 的工厂", fixture.ViewModel.LeaderboardDisplayNameInput);
        Assert.False(fixture.ViewModel.LeaderboardBusy);
    }
}
