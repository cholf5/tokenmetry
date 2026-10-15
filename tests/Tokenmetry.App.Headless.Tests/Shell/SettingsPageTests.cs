using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// S-002 数据源与设置页的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. 数据源表绑定：行 = 采集扫描注册表快照（stub 注入），状态三态文案与 status-* 着色类、
///    记录数千分位、未扫描源的诚实"尚未扫描 / —"降级；
/// 2. 统计口径区：逐项定案的说明样式渲染（4 始终生效 + 1 未提供），无假开关（口径区无 CheckBox）；
/// 3. TC-001 重新扫描（全局）：按钮扫描态（禁用 + "扫描中…"）→ 完成后行刷新 + toast；
/// 4. 主题三态组合框迁移：选项与选中绑定（持久化语义在 SettingsViewModelTests / ThemeService 测试）；
/// 5. 真实 DI 单例冒烟：AppServices.Get&lt;SettingsViewModel&gt;() 可实例化视图（19 个注册数据源）；
/// 6. 明暗两套离屏截图留档（docs/design/settings-*.png）。
/// </summary>
public class SettingsPageTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_SETTINGS_PREVIEW_DIR");

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 30, 0, TimeSpan.Zero);

    // ── stub（同 SessionsTests 纪律：只承担数据回传，不伪造采集能力）──

    private sealed class StubToast : IToastService
    {
        public List<string> Messages { get; } = new();

        public void Show(string message, Visual? host = null) => Messages.Add(message);
    }

    /// <summary>Stub 查询服务：只真实提供原始模型名候选（模型聚合卡候选清单），其余成员空实现。</summary>
    private sealed class StubAliasQuery : IUsageQueryService
    {
        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["ark/glm-5.3-flash", "glm-5.3-flash"]);

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult());

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult());

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    private sealed class StubScanReader : IIngestionScanReader
    {
        public List<string> Registered { get; } = new();
        private readonly Dictionary<string, IngestionSourceScanSnapshot> _scans = new();

        public IReadOnlyList<string> RegisteredSources => Registered;

        public IngestionSourceScanSnapshot? GetLastScan(string agent) => _scans.GetValueOrDefault(agent);

        public void Register(string agent, IngestionSourceScanSnapshot snapshot)
        {
            if (!Registered.Contains(agent))
            {
                Registered.Add(agent);
            }

            _scans[agent] = snapshot;
        }
    }

    private static SettingsViewModel CreateViewModel(
        StubScanReader? reader = null,
        StubToast? toast = null,
        Func<Task<bool>>? refreshAction = null,
        IUsageQueryService? query = null,
        ModelCatalog? catalog = null)
    {
        return new SettingsViewModel(
            new AppSettings(),
            new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests")),
            new JsonAppSettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests"))),
            new ThemeService(),
            reader ?? new StubScanReader(),
            new BackgroundRefreshService(refreshAction ?? (() => Task.FromResult(true))),
            toast ?? new StubToast(),
            query,
            catalog);
    }

    [AvaloniaFact]
    public void Source_table_renders_snapshot_status_counts_and_honest_pending_rows()
    {
        var reader = new StubScanReader();
        reader.Registered.AddRange(new[] { "Claude Code", "Copilot CLI", "Gemini CLI", "ZCode" });
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 2184, Now));
        reader.Register("Copilot CLI", new IngestionSourceScanSnapshot("Copilot CLI", JsonlScanStatus.Partial, 128, Now));
        reader.Register("Gemini CLI", new IngestionSourceScanSnapshot("Gemini CLI", JsonlScanStatus.NoData, 0, Now));
        // ZCode 已注册未扫描 → 诚实"尚未扫描"

        var vm = CreateViewModel(reader: reader);
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        // 面板与表头
        Assert.Contains("已识别的数据源", texts);
        Assert.Contains("数据源", texts);
        Assert.Contains("状态", texts);
        Assert.Contains("最近扫描", texts);
        Assert.Contains("记录数", texts);
        Assert.Contains("重新扫描（全部源）", texts);

        // 行：状态文案 / 记录数千分位 / 未扫描降级
        Assert.Contains("Claude Code", texts);
        Assert.Contains("● 已连接", texts);
        Assert.Contains("2,184", texts);
        Assert.Contains("● 部分数据", texts);
        Assert.Contains("● 未检出", texts);
        Assert.Contains("0", texts);
        Assert.Contains("● 尚未扫描", texts);
        Assert.Contains("—", texts);

        // 状态着色类（status-*，复用既有强调色令牌）
        var okText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "● 已连接");
        Assert.Contains("status-ok", okText.Classes);
        var partialText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "● 部分数据");
        Assert.Contains("status-partial", partialText.Classes);
        var nodataText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "● 未检出");
        Assert.Contains("status-nodata", nodataText.Classes);
        var pendingText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "● 尚未扫描");
        Assert.Contains("status-pending", pendingText.Classes);

        // 行序 = 注册序（管道扫描序）
        Assert.Equal(
            new[] { "Claude Code", "Copilot CLI", "Gemini CLI", "ZCode" },
            vm.SourceRows.Select(r => r.Agent).ToArray());

        // 行首 Agent 品牌图标（I-034 补）：变体名从 Agent 名派生（未覆盖的源落 generic → 散列字形）
        Assert.Equal("claudecode", vm.SourceRows[0].LogoVendor);
        Assert.Equal("copilot", vm.SourceRows[1].LogoVendor);
        Assert.Equal("geminicli", vm.SourceRows[2].LogoVendor);
        Assert.Equal("zai", vm.SourceRows[3].LogoVendor);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Caliber_area_renders_explanatory_rows_without_fake_switches()
    {
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("统计口径", texts);
        foreach (var item in SettingsViewModel.CaliberItems)
        {
            Assert.Contains(item.Text, texts);
            Assert.Contains(item.Badge, texts);
        }

        // 假开关红线：口径区 5 项没有一个是 CheckBox（页面上仅存的 CheckBox 属于外观与通用区）
        var checkboxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.All(checkboxes, cb => Assert.DoesNotContain("去重", cb.Content?.ToString()));
        Assert.All(checkboxes, cb => Assert.DoesNotContain("自动合并", cb.Content?.ToString()));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Rescan_button_enters_scanning_state_then_reloads_and_toasts()
    {
        // TC-001（全局重扫定案）：按钮进入扫描态 → 完成后记录数刷新 + toast
        var reader = new StubScanReader();
        reader.Registered.Add("Claude Code");
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, Now));

        var gate = new TaskCompletionSource();
        var toast = new StubToast();
        var vm = CreateViewModel(reader: reader, toast: toast, refreshAction: async () =>
        {
            await gate.Task;
            reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 42, Now));
            return true;
        });

        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rescanButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, vm.RescanCommand));
        Assert.True(rescanButton.IsEnabled);
        Assert.Equal("重新扫描（全部源）", rescanButton.Content);

        var run = vm.RescanCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsScanning);
        Assert.False(rescanButton.IsEnabled, "扫描态防重复触发");
        Assert.Equal("扫描中…", rescanButton.Content);

        gate.SetResult();
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsScanning);
        Assert.Equal("重新扫描（全部源）", rescanButton.Content);
        Assert.True(rescanButton.IsEnabled);
        Assert.Equal("42", vm.SourceRows.Single().RecordCountText);
        Assert.Contains("扫描完成", toast.Messages);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Theme_combo_is_migrated_with_three_options_and_current_selection()
    {
        var vm = CreateViewModel();
        vm.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Value == "dark");
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.ItemsSource is IReadOnlyList<ThemeOption>); // 设置页现有主题 + 语言两个下拉
        Assert.Equal(3, combo.ItemCount);
        Assert.Same(SettingsViewModel.ThemeOptions.Single(o => o.Value == "dark"), combo.SelectedItem);

        // 三态选项文案（迁移自旧视图，标签不变）
        var optionTexts = SettingsViewModel.ThemeOptions.Select(o => o.Label).ToList();
        Assert.Equal(new[] { "日间", "夜间", "跟随系统" }, optionTexts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Language_combo_offers_three_options_and_switching_retranslates_live()
    {
        // 界面语言（zh/en/system）：三选项 + 切换不重启实时重译（对标 stupid-dict 的语言切换回归测试）。
        // 下拉选项为稳定实例 + RefreshLabel——重建 ItemsSource 会丢选中（Avalonia ComboBox 坑，见 VM 注释）。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var languageCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.ItemsSource is IReadOnlyList<LanguageOption>);
        Assert.Equal(3, languageCombo.ItemCount);
        Assert.Same(SettingsViewModel.LanguageOptions.Single(o => o.Value == "system"), languageCombo.SelectedItem);

        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            vm.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");
            Dispatcher.UIThread.RunJobs();

            // 页面直绑 Translations 的串实时重译（无重启）
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Recognized data sources", texts);
            Assert.Contains("Appearance & general", texts);
            Assert.Contains("Saved automatically; takes effect the next time the background refresh service starts.", texts);
            // 页头 Page.Title 实时重译（多级绑定路径 Page.Title——叶子通知由 PageDescriptor 发，
            // 真机实测缺失时标题保持旧语言、导航切换回来才更新）
            var pageTitle = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("page-title")).Text;
            Assert.Equal("Data & settings", pageTitle);
            // 选中保持（切换后仍为同一选项实例），选项 Label 已刷新为英文
            Assert.Same(SettingsViewModel.LanguageOptions.Single(o => o.Value == "en"), languageCombo.SelectedItem);
            Assert.Equal("English", SettingsViewModel.LanguageOptions.Single(o => o.Value == "en").Label);
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Di_singleton_settings_viewmodel_smoke_renders_registered_sources()
    {
        // 真实服务冒烟：DI 单例 VM（真实注册表，19 个注册数据源；扫描快照随本机 facts 而定）
        var vm = Tokenmetry.App.DependencyInjection.AppServices.Get<SettingsViewModel>();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(19, vm.SourceRows.Count);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("已识别的数据源", texts);
        Assert.Contains("统计口径", texts);
        Assert.Contains("外观与通用", texts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Di_settings_rescan_drives_real_dashboard_refresh_line()
    {
        // P0 回归锁（审查修复轮）：真实 DI 解析的 SettingsViewModel 其「重新扫描」必须走通真实
        // 采集线——容器内同一 BackgroundRefreshService → BackgroundRefreshWiring →
        // DashboardViewModel.RefreshCommand（facts 直解析）→ 外壳时间戳回写。此前 late map 不拦截
        // 嵌套构造注入，设置页 VM 拿到的是兜底空转实例（重扫假成功）。
        // 说明：本用例触发一轮真实扫描（读真实 agent 日志目录 + 写真实 facts 存储，与 App 启动
        // 首刷同语义）；断言全部环境无关。
        var wiring = AppServices.Get<BackgroundRefreshWiring>();
        var dashboard = AppServices.Get<DashboardViewModel>();
        var shell = AppServices.Get<MainWindowViewModel>();
        wiring.Attach(dashboard, shell); // App 启动序列等价物（headless 不执行桌面生命周期块）

        var vm = AppServices.Get<SettingsViewModel>();
        var run = vm.RescanCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        await run;
        Dispatcher.UIThread.RunJobs();

        // 走通真实采集线的三个可观测点：
        Assert.Equal("scan finished", vm.Status);                       // 1. 接线层上报成功（非兜底空转假成功路径）
        Assert.StartsWith("最近更新", shell.LastUpdatedText);           // 2. 外壳时间戳被本轮回写
        Assert.DoesNotContain(vm.SourceRows, r => r.IsPending);         // 3. 一轮真实刷新后 19 源全部有快照
        Assert.Equal(19, vm.SourceRows.Count);
    }

    [AvaloniaFact]
    public void Alias_panel_renders_and_autocomplete_text_binds_to_add_rule()
    {
        // 模型聚合卡端到端（headless）：面板渲染 → 候选清单 → AutoCompleteBox 键入
        // （Text 逐键双向绑定到 VM——本功能交互的关键路径）→ 添加规则落行 + 空态消失。
        var vm = CreateViewModel(query: new StubAliasQuery());

        var window = new Window { Width = 980, Height = 900, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("模型聚合（别名映射）", texts);
        Assert.Contains("原始模型名", texts);
        Assert.Contains("规范名（统计显示名）", texts);
        Assert.Contains("＋ 添加映射", texts);
        Assert.Contains("尚无映射规则", texts);

        // 候选清单异步装载（构造 fire-and-forget）→ RunJobs 排空后到位
        Dispatcher.UIThread.RunJobs();

        // 按占位文案定位（I-035 起页面上有 4 个 AutoCompleteBox：模型目录的表单 ID / 关联目标 +
        // 本卡的原始名 / 目标；按下标取会在加面板时静默错位）。
        var boxes = window.GetVisualDescendants().OfType<AutoCompleteBox>().ToList();
        var sourceBox = boxes.Single(b => b.PlaceholderText == Translations.Instance.AliasSourcePlaceholder);
        var targetBox = boxes.Single(b => b.PlaceholderText == Translations.Instance.AliasTargetPlaceholder);

        sourceBox.Focus();
        window.KeyTextInput("ark/glm-5.3-flash");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ark/glm-5.3-flash", vm.NewAliasSource);

        targetBox.Focus();
        window.KeyTextInput("glm-5.3-flash");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("glm-5.3-flash", vm.NewAliasTarget);

        vm.AddAliasRuleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.AliasRules);
        Assert.Empty(vm.NewAliasSource);
        // 可视树里 IsVisible=false 的元素仍在（空态 TextBlock 只是隐藏），按可见过滤后再断言。
        texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Contains("ark/glm-5.3-flash", texts);
        Assert.Contains("glm-5.3-flash", texts);
        Assert.DoesNotContain("尚无映射规则", texts);
    }

    [AvaloniaFact]
    public void Settings_page_renders_light_and_dark_screenshots_for_archive()
    {
        var reader = new StubScanReader();
        reader.Registered.AddRange(new[]
        {
            "Claude Code", "Codex", "ZCode", "DSH", "Copilot CLI", "OpenCode", "Goose",
        });
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 2184, Now));
        reader.Register("Codex", new IngestionSourceScanSnapshot("Codex", JsonlScanStatus.Ok, 946, Now));
        reader.Register("ZCode", new IngestionSourceScanSnapshot("ZCode", JsonlScanStatus.Ok, 622, Now));
        reader.Register("DSH", new IngestionSourceScanSnapshot("DSH", JsonlScanStatus.Ok, 307, Now));
        reader.Register("Copilot CLI", new IngestionSourceScanSnapshot("Copilot CLI", JsonlScanStatus.Partial, 128, Now));
        reader.Register("OpenCode", new IngestionSourceScanSnapshot("OpenCode", JsonlScanStatus.Ok, 210, Now));
        reader.Register("Goose", new IngestionSourceScanSnapshot("Goose", JsonlScanStatus.NoData, 0, Now));

        var vm = CreateViewModel(reader: reader);

        foreach (var (variant, fileName) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "settings-light.png"),
                     (ThemeVariant.Dark, "settings-dark.png"),
                 })
        {
            var window = new Window
            {
                Width = 980,
                Height = 900,
                Content = new SettingsView { DataContext = vm },
                RequestedThemeVariant = variant,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // headless 渲染定时器不随 RunJobs 走帧，须手动触发一帧才能截到合成结果
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            if (!string.IsNullOrWhiteSpace(ArchiveDir))
            {
                Directory.CreateDirectory(ArchiveDir);
                var path = Path.Combine(ArchiveDir, fileName);
                frame.Save(path, new PngBitmapEncoderOptions());
                Assert.True(File.Exists(path), $"截图未生成：{path}");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// I-035 模型目录卡的端到端（headless）：条目表渲染出目录内容 + 表单能把 ID / 价格写进 VM
    /// （TextBox 双向绑定是这一功能的关键交互路径，绑定写错的话价格永远保存不进去）。
    /// </summary>
    [AvaloniaFact]
    public void Model_catalog_panel_renders_rows_and_form_binds_to_view_model()
    {
        var vm = CreateViewModel(catalog: new ModelCatalog());

        var window = new Window { Width = 980, Height = 900, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsVisible)
            .Select(text => text.Text)
            .ToList();
        Assert.Contains("模型目录", texts);
        Assert.Contains(vm.ModelRows[0].Id, texts);
        Assert.Contains("模型 ID", texts);
        Assert.Contains("单价（输入 / 输出）", texts);

        // 表单绑定：厂商输入写回 VM（未绑上的话保存会得到空表单）。按 x:Name 定位——
        // 页面上还有 ccusage 命令 / 刷新间隔等输入框，按下标取会静默错位
        // （AutoCompleteBox 也是 TextBox 的子类，OfType<TextBox> 会把它一起捞进来）。
        var vendorBox = window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.Name == "ModelFormVendorBox");
        vendorBox.Focus();
        window.KeyTextInput("zai");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("zai", vm.ModelFormVendor);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
