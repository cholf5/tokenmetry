namespace Tokenmetry.App.Headless.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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

        /// <summary>设置同步 fake 的服务端桶（key → 行）；updatedAt 用递增时钟模拟服务端赋值。</summary>
        public Dictionary<string, SyncedSetting> Settings { get; } = new(StringComparer.Ordinal);

        public int SettingsPutCount;

        private DateTimeOffset _settingsClock = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public Task<IReadOnlyList<SyncedSetting>> GetSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncedSetting>>(
                Settings.Values.OrderBy(row => row.Key, StringComparer.Ordinal).ToList());

        public Task<IReadOnlyList<SyncedSetting>> PutSettingsAsync(IReadOnlyList<SyncedSettingUpdate> settings, CancellationToken ct = default)
        {
            SettingsPutCount++;
            _settingsClock = _settingsClock.AddSeconds(1);
            var written = settings
                .Select(update => new SyncedSetting(update.Key, update.Value, _settingsClock))
                .ToList();
            foreach (var row in written)
            {
                Settings[row.Key] = row;
            }

            return Task.FromResult<IReadOnlyList<SyncedSetting>>(written);
        }
    }

    private sealed class SyncFixture : IDisposable
    {
        public AppSettings Settings { get; } = new();
        public FakeSyncBackend Backend { get; } = new();
        public SqliteUsageStore Store { get; }
        public SyncStateService SyncState { get; }
        public SettingsViewModel ViewModel { get; }
        private readonly string _directory;

        public SyncFixture(Action<AppSettings>? configure = null)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"tokenmetry-settings-sync-{Guid.NewGuid():N}");
            configure?.Invoke(Settings);
            var paths = new AppPaths(_directory);
            Directory.CreateDirectory(paths.AppDataDirectory);
            Store = new SqliteUsageStore(paths.CacheDbPath);
            Store.InitializeAsync().GetAwaiter().GetResult();
            SyncState = new SyncStateService(
                Store,
                Settings,
                new JsonAppSettingsStore(paths),
                backendFactory: (_, _) => Backend,
                timerPeriod: Timeout.InfiniteTimeSpan,
                settingsSyncStateStore: Store);
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

    /// <summary>「同步应用设置」开关仅随「已启用」分支出现，且默认关（settings-sync 设计 S1）。</summary>
    [AvaloniaFact]
    public async Task SettingsSyncToggle_HiddenUntilSyncEnabled_AndDefaultsOff()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        var window = Show(fixture.ViewModel);

        var label = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SettingsSyncSection);
        Assert.False(ChartTestHelpers.IsEffectivelyVisible(label), "同步未启用时开关不可见");
        Assert.False(fixture.ViewModel.SettingsSyncEnabled, "默认关");

        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(ChartTestHelpers.IsEffectivelyVisible(label), "同步启用后开关可见");
        Assert.False(fixture.ViewModel.SettingsSyncEnabled, "启用同步不会自动打开设置同步");
        Assert.True(fixture.Backend.SettingsPutCount == 0, "开关关闭时零设置出站");
    }

    /// <summary>拨开开关：落盘并立即跑一轮，把本机偏好推到中继（首同步远端为空 → 全量推出）。</summary>
    [AvaloniaFact]
    public async Task SettingsSyncToggle_TurnOn_PersistsAndPushesLocalPreferences()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        fixture.ViewModel.SettingsSyncEnabled = true;
        // 拨开是 fire-and-forget 的一轮同步：账面读写走真实 SQLite（真异步），等它落地再断言。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fixture.Backend.SettingsPutCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(fixture.Settings.SettingsSyncEnabled, "开关即存");
        Assert.True(fixture.Backend.SettingsPutCount == 1, "拨开立即跑一轮设置同步");
        Assert.Contains(SettingsSyncKeys.Theme, fixture.Backend.Settings.Keys);
        Assert.Contains(SettingsSyncKeys.Language, fixture.Backend.Settings.Keys);
    }

    /// <summary>settings-sync 设计 §6：远端语言到达后被采纳，设置页选择项跟随（热应用回流）。</summary>
    [AvaloniaFact]
    public async Task RemoteLanguage_AppliedAfterPull_SettingsPageSelectionFollows()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        // 与生产一致（AppServices 里那行接线在 headless 宿主不执行）：runner 采纳后回流设置页重投影。
        fixture.SyncState.SettingsApplied += keys =>
            Dispatcher.UIThread.Post(() => fixture.ViewModel.ApplySyncedSettings(keys));

        fixture.Backend.Settings[SettingsSyncKeys.Language] =
            new SyncedSetting(SettingsSyncKeys.Language, "ja", DateTimeOffset.UtcNow);

        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // EnableSync 换账号会把首同步裁决复位为 unresolved；本用例验证「采用远端」路径，先裁决再开开关。
        fixture.Settings.SettingsSyncFirstSyncPolicy = SettingsSyncFirstSyncPolicies.UseRemote;

        try
        {
            fixture.ViewModel.SettingsSyncEnabled = true;

            await WaitForAsync(() => fixture.ViewModel.SelectedLanguage.Value == "ja");

            Assert.Equal("ja", fixture.Settings.Language);
            Assert.True(fixture.ViewModel.SelectedLanguage.Value == "ja", "远端语言到达后设置页选择项跟随");
        }
        finally
        {
            // 语言是进程级单例：用例自行恢复基线（TestCulture 固定 zh-Hans）。
            fixture.ViewModel.SelectedLanguage = SettingsViewModel.LanguageOptions.Single(o => o.Value == "system");
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>settings-sync 审查 #3：首同步远端与本机不同 → 显示三选一面板；选「云端」后应用并消失。</summary>
    [AvaloniaFact]
    public async Task FirstSyncConflict_ShowsChoicePanel_AndUseRemoteResolves()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        fixture.Backend.Settings[SettingsSyncKeys.Theme] =
            new SyncedSetting(SettingsSyncKeys.Theme, "dark", DateTimeOffset.UtcNow);

        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var window = Show(fixture.ViewModel);
        fixture.ViewModel.SettingsSyncEnabled = true;

        await WaitForAsync(() => fixture.ViewModel.IsSettingsSyncConflictVisible);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(Translations.Instance.SettingsSyncConflictTitle, fixture.ViewModel.SyncStatusText);
        // 状态行也会显示同一句，取面板标题（panel-title）这一处。
        var title = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SettingsSyncConflictTitle
                && t.Classes.Contains("panel-title"));
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(title), "首同步冲突显示三选一面板");
        Assert.True(fixture.Settings.Theme == "system", "裁决前不应用远端");

        await fixture.ViewModel.UseRemoteSyncedSettingsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.Settings.Theme == "dark", "采用云端设置");
        Assert.False(fixture.ViewModel.IsSettingsSyncConflictVisible, "裁决后面板消失");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task OfficialInstance_IsShownAsNotOpen_SelfHostRemainsDefault()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        var window = Show(fixture.ViewModel);

        var official = window.GetVisualDescendants().OfType<RadioButton>()
            .Single(r => Equals(r.Content, Translations.Instance.SyncModeOfficial));
        var selfHosted = window.GetVisualDescendants().OfType<RadioButton>()
            .Single(r => Equals(r.Content, Translations.Instance.SyncModeSelfHosted));

        Assert.False(official.IsEnabled, "官方实例暂未开放：入口置灰");
        Assert.False(official.IsChecked, "不能选中未开放的官方实例");
        Assert.True(selfHosted.IsChecked, "默认选中自建中继");

        var hint = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SyncOfficialUnavailableHint);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(hint), "显示自建中继引导");

        fixture.ViewModel.IsOfficialMode = true;
        Assert.False(fixture.ViewModel.IsOfficialMode, "代码路径也不能切到未开放的官方通道");
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

    /// <summary>
    /// 构造必须从已保存的凭据播种输入框（回归：token 输入框曾无初值）。关闭同步后配置区重现，
    /// 空的 token 框再启用一次就会把持久化令牌覆盖成空串——之后所有请求 401，表现为
    /// 「展示名改了不生效、别的设备也不同步」。
    /// </summary>
    [AvaloniaFact]
    public async Task Constructor_SeedsSyncInputsFromStoredSettings()
    {
        using var fixture = new SyncFixture(settings =>
        {
            settings.SyncEndpoint = "http://127.0.0.1:8080";
            settings.SyncToken = "relay-token";
        });
        await fixture.SyncState.InitializeAsync();

        Assert.Equal("http://127.0.0.1:8080", fixture.ViewModel.SyncEndpointInput);
        Assert.Equal("relay-token", fixture.ViewModel.SyncTokenInput);
    }

    /// <summary>关闭 → 再启用应原样保留已保存的 token（不得把输入框的空值写回 settings）。</summary>
    [AvaloniaFact]
    public async Task DisableThenReenable_KeepsStoredToken()
    {
        using var fixture = new SyncFixture(settings =>
        {
            settings.SyncEndpoint = "http://127.0.0.1:8080";
            settings.SyncToken = "relay-token";
        });
        await fixture.SyncState.InitializeAsync();

        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.ViewModel.IsSyncEnabled);

        await fixture.ViewModel.DisableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.ViewModel.IsSyncEnabled);

        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.ViewModel.IsSyncEnabled);
        Assert.Equal("relay-token", fixture.Settings.SyncToken);
    }

    /// <summary>空凭据不得启用：开关回弹、持久化令牌保持原值、状态行给出必填提示。</summary>
    [AvaloniaFact]
    public async Task Enable_WithBlankToken_StaysDisabledAndKeepsStoredToken()
    {
        using var fixture = new SyncFixture(settings =>
        {
            settings.SyncEndpoint = "http://127.0.0.1:8080";
            settings.SyncToken = "relay-token";
        });
        await fixture.SyncState.InitializeAsync();
        Dispatcher.UIThread.RunJobs(); // 清掉 InitializeAsync Changed 排队的装载，避免回写状态行

        fixture.ViewModel.SyncTokenInput = string.Empty;
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(fixture.ViewModel.IsSyncEnabled, "空 token 必须拒绝启用");
        Assert.False(fixture.SyncState.IsEnabled);
        Assert.True(fixture.Settings.SyncToken == "relay-token", "不得把已保存令牌覆盖成空串");
        Assert.Equal(Translations.Instance.SyncCredentialsRequired, fixture.ViewModel.SyncStatusText);
    }

    [AvaloniaFact]
    public async Task SelfHostFlow_AfterEnable_ShowsConnectionAddressAndMaskedToken()
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

        // 投影 = 已持久化的通道类型与凭据（启用后配置区隐藏，仍能回答「连到哪」）
        Assert.False(fixture.ViewModel.IsOfficialConnection);
        Assert.Equal("http://127.0.0.1:8080", fixture.ViewModel.SyncEndpointDisplay);
        Assert.Equal("relay-token", fixture.ViewModel.SyncTokenDisplay);

        var visibleTexts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(ChartTestHelpers.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();
        Assert.Contains(Translations.Instance.SyncConnection, visibleTexts);
        Assert.Contains(Translations.Instance.SyncModeSelfHosted, visibleTexts);
        Assert.DoesNotContain(Translations.Instance.SyncModeOfficial, visibleTexts);
        Assert.Contains(Translations.Instance.SyncEndpoint, visibleTexts);
        Assert.Contains(Translations.Instance.SyncToken, visibleTexts);

        // 令牌默认掩码：明文不可见，只有只读掩码框在场
        Assert.DoesNotContain("relay-token", visibleTexts);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(),
            t => ChartTestHelpers.IsEffectivelyVisible(t) && t.Text == "relay-token" && t.PasswordChar == '•');
    }

    [AvaloniaFact]
    public async Task SelfHostFlow_TokenRevealToggle_SwapsMaskForPlainText()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.InitializeAsync();
        var window = Show(fixture.ViewModel);

        fixture.ViewModel.SyncEndpointInput = "http://127.0.0.1:8080";
        fixture.ViewModel.SyncTokenInput = "relay-token";
        await fixture.ViewModel.EnableSyncCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        fixture.ViewModel.ShowSyncTokenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(fixture.ViewModel.IsSyncTokenRevealed);
        var visibleTexts = window.GetVisualDescendants()
            .Where(ChartTestHelpers.IsEffectivelyVisible)
            .Select(v => v switch
            {
                SelectableTextBlock selectable => selectable.Text,
                TextBlock text => text.Text,
                _ => null,
            })
            .Where(t => t is not null)
            .ToList();
        Assert.Contains("relay-token", visibleTexts);
        Assert.Contains(Translations.Instance.SyncHideToken, visibleTexts);
        Assert.DoesNotContain(Translations.Instance.SyncShowToken, visibleTexts);

        fixture.ViewModel.HideSyncTokenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.ViewModel.IsSyncTokenRevealed);
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

    /// <summary>
    /// 状态行承载异常消息（SetSyncError 拼入 ex.Message，含中继地址与 HTTP 栈文案，长度不可控）：
    /// 长错误必须折行并把 Sync now / Disable sync 留在面板内——此前整行是横排 StackPanel，
    /// 状态文本不折行，宽度超出面板后两个按钮被整个挤出面板右缘（真机 401 截图即此）。
    /// </summary>
    [AvaloniaFact]
    public async Task SyncStatusRow_LongErrorMessage_KeepsActionButtonsInsidePanel()
    {
        using var fixture = new SyncFixture();
        await fixture.SyncState.EnableAsync("http://127.0.0.1:8080", "relay-token");
        var window = Show(fixture.ViewModel);
        await fixture.ViewModel.RefreshSyncStateAsync();

        fixture.ViewModel.SyncStatusText =
            $"{Translations.Instance.SyncStatusError}: " +
            string.Join(" ", Enumerable.Repeat(
                "Relay request failed: 401 Unauthorized http://192.168.1.10:8080/api/me/leaderboard", 3));
        fixture.ViewModel.LastSyncText = "2026-11-23 17:55";
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var title = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Translations.Instance.SyncTitle);
        var panel = title.GetVisualAncestors().OfType<Border>()
            .First(b => b.Classes.Contains("panel"));
        Assert.True(panel.Bounds.Width > 0, "同步面板已布局");

        var status = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == fixture.ViewModel.SyncStatusText);
        var statusRight = status.TranslatePoint(new Point(status.Bounds.Width, 0), panel)!.Value.X;

        Button[] actions = window.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, Translations.Instance.SyncNow)
                        || Equals(b.Content, Translations.Instance.SyncDisable))
            .ToArray();
        Assert.Equal(2, actions.Length);
        var actionsLeft = actions.Min(b => b.TranslatePoint(new Point(0, 0), panel)!.Value.X);
        Assert.True(
            statusRight <= actionsLeft + 0.5,
            $"状态文本压到动作按钮：{statusRight:F1} > {actionsLeft:F1}");
        foreach (var button in actions)
        {
            Assert.True(ChartTestHelpers.IsEffectivelyVisible(button), $"「{button.Content}」可见");
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), panel)!.Value.X;
            Assert.True(
                right <= panel.Bounds.Width + 0.5,
                $"「{button.Content}」越出同步面板右缘：{right:F1} > {panel.Bounds.Width:F1}");
        }
    }

    public void Dispose()
    {
    }
}
