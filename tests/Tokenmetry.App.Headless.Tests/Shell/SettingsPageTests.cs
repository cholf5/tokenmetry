using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 设置页（应用偏好）的自动化验证（headless，真实 App.axaml + Semi；S-004 起收缩为
/// 主题 / 语言 / 刷新间隔 / ccusage 命令 / 本地路径——数据源 / 口径 / 目录 / 别名在
/// DataPricingPageTests）：
/// 1. 主题三态组合框迁移：选项与选中绑定（持久化语义在 SettingsViewModelTests / ThemeService 测试）；
/// 2. 界面语言三选项 + 切换不重启实时重译；
/// 3. 真实 DI 单例冒烟（真实 AppPaths 的路径面板）；
/// 4. 明暗两套离屏截图留档（docs/design/settings-*.png）。
/// </summary>
public class SettingsPageTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_SETTINGS_PREVIEW_DIR");

    private static SettingsViewModel CreateViewModel(FakeAutoStartManager? autoStartManager = null)
    {
        return new SettingsViewModel(
            new AppSettings(),
            new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests")),
            new JsonAppSettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests"))),
            new ThemeService(),
            autoStartManager ?? new FakeAutoStartManager());
    }

    [AvaloniaFact]
    public void AutoStart_checkbox_defaults_off_and_reports_to_os_manager_on_toggle()
    {
        // S-006：开机自启动默认关；勾选即 OS 注册调用（fake 顶替真实管理器，绝不触碰真实 OS 状态）
        // + settings 自动保存（持久化语义在 SettingsViewModelTests）。
        var manager = new FakeAutoStartManager();
        var vm = CreateViewModel(manager);
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.AutoStartOnBoot);
        var checkbox = window.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Content as string == Translations.Instance.CheckAutoStartOnBoot);
        Assert.False(checkbox.IsChecked == true);

        checkbox.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.AutoStartOnBoot);
        Assert.Equal(new[] { true }, manager.Calls);

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
    public void Language_combo_offers_all_options_and_switching_retranslates_live()
    {
        // 界面语言（D-024 起八选项：system/zh/zh-hant/en/ja/ko/pt-br/ru）+ 切换不重启实时重译
        // （对标 stupid-dict 的语言切换回归测试）。
        // 下拉选项为稳定实例 + RefreshLabel——重建 ItemsSource 会丢选中（Avalonia ComboBox 坑，见 VM 注释）。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var languageCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.ItemsSource is IReadOnlyList<LanguageOption>);
        Assert.Equal(SettingsViewModel.LanguageOptions.Count, languageCombo.ItemCount);
        Assert.Same(SettingsViewModel.LanguageOptions.Single(o => o.Value == "system"), languageCombo.SelectedItem);

        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            vm.SelectedLanguage = SettingsViewModel.LanguageOptions.First(o => o.Value == "en");
            Dispatcher.UIThread.RunJobs();

            // 页面直绑 Translations 的串实时重译（无重启）
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Appearance & general", texts);
            Assert.Contains("Local paths", texts);
            Assert.Contains("Saved automatically; takes effect the next time the background refresh service starts.", texts);
            // 页头 Page.Title 实时重译（多级绑定路径 Page.Title——叶子通知由 PageDescriptor 发，
            // 真机实测缺失时标题保持旧语言、导航切换回来才更新）
            var pageTitle = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("page-title")).Text;
            Assert.Equal("Settings", pageTitle);
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
    public void Di_singleton_settings_viewmodel_smoke_renders_preference_panels()
    {
        // 真实服务冒烟：DI 单例 VM（真实 AppPaths；偏好页不含数据源表 / 目录——那是数据与计价页）
        var vm = AppServices.Get<SettingsViewModel>();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("外观与通用", texts);
        Assert.Contains("本地路径", texts);
        Assert.DoesNotContain("已识别的数据源", texts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Settings_page_renders_light_and_dark_screenshots_for_archive()
    {
        var vm = CreateViewModel();

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

    /// <summary>OS 注册 fake（headless 宿主绝不调用真实 <see cref="AutoStartManager"/>——其 SetEnabled
    /// 操作真实注册表 / 全局 LaunchAgents 目录）。</summary>
    private sealed class FakeAutoStartManager : IAutoStartManager
    {
        public List<bool> Calls { get; } = new();

        public void SetEnabled(bool enabled)
        {
            Calls.Add(enabled);
        }
    }
}
