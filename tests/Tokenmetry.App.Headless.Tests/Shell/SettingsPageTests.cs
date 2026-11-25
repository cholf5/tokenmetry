using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
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
/// 1. 主题两维选择器：画板项的档位徽标（仅深色/仅浅色）+ 模式三张预览卡
///    （选中环、单档压暗、原因说明；持久化语义在 SettingsViewModelTests / ThemeService 测试）；
/// 2. 界面语言三选项 + 切换不重启实时重译；
/// 3. 真实 DI 单例冒烟（真实 AppPaths 的路径面板）；
/// 4. 明暗两套离屏截图留档（docs/design/settings-*.png）。
/// </summary>
public class SettingsPageTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_SETTINGS_PREVIEW_DIR");

    private static SettingsViewModel CreateViewModel(FakeAutoStartManager? autoStartManager = null,
        Action<string>? openDataFolder = null)
    {
        return new SettingsViewModel(
            new AppSettings(),
            new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests")),
            new JsonAppSettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "ccusage-settings-tests"))),
            new ThemeService(),
            autoStartManager ?? new FakeAutoStartManager(),
            openDataFolderOverride: openDataFolder);
    }

    [AvaloniaFact]
    public void Local_paths_panel_opens_data_directory_and_keeps_paths_selectable()
    {
        // 本地路径区从「只能看」补上出口：标题行右侧一个打开按钮（目标 = 两文件共同目录）、
        // 两行路径改为可选中（原先裸 TextBlock 连复制都做不到）。打开动作注入记录器，不真弹 Finder。
        string? opened = null;
        var vm = CreateViewModel(openDataFolder: path => opened = path);
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var selectableTexts = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(t => t.Text).ToList();
        Assert.Contains(vm.CacheDbPath, selectableTexts);
        Assert.Contains(vm.SettingsFilePath, selectableTexts);

        var openButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content as string == Translations.Instance.SettingsOpenDataFolder);
        Assert.NotNull(openButton.Command);
        openButton.Command!.Execute(openButton.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(vm.AppDataDirectory, opened);

        window.Close();
        Dispatcher.UIThread.RunJobs();
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
    public void Theme_picker_shows_palette_mode_badges_and_mode_cards()
    {
        var vm = CreateViewModel();
        var appThemeService = new ThemeService(); // 复原专用：测试里切画板会污染共享 Application 状态
        try
        {
            vm.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Value == "dark");
            var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 画板下拉（PaletteOption）：默认 + 九套生成画板，每项带 5 条画板本色色带
            var paletteCombo = window.GetVisualDescendants().OfType<ComboBox>()
                .First(c => c.ItemsSource is IReadOnlyList<PaletteOption>);
            Assert.Equal(ThemePaletteCatalog.All.Count, paletteCombo.ItemCount);
            Assert.All(SettingsViewModel.PaletteOptions, option => Assert.Equal(5, option.Swatches.Count));
            Assert.Same(SettingsViewModel.PaletteOptions[0], paletteCombo.SelectedItem); // 默认画板

            // 档位徽标提前说明缺口：只有夜间的画板带「仅深色」，多档画板没有徽标
            var nordOption = SettingsViewModel.PaletteOptions.First(o => o.Value == "nord");
            Assert.True(nordOption.HasModeBadge);
            Assert.Equal(Translations.Instance.ThemeOnlyDarkBadge, nordOption.ModeBadgeText);
            Assert.False(SettingsViewModel.PaletteOptions[0].HasModeBadge);

            // 模式是三张常驻图卡（不再是下拉）：默认画板三张都可用，当前模式对应卡被选中
            var modeCards = window.GetVisualDescendants().OfType<RadioButton>()
                .Where(card => card.Classes.Contains("theme-mode-card")).ToList();
            Assert.Equal(3, modeCards.Count);
            Assert.All(modeCards, card => Assert.True(card.IsEnabled));
            Assert.Single(modeCards, card => card.IsChecked == true);
            Assert.Null(window.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.ItemsSource is IReadOnlyList<ThemeOption>));

            // 卡片行与画板下拉同宽同左缘（三张卡曾经合计 360px，越出 300px 的下拉）
            var comboRight = paletteCombo.TranslatePoint(
                new Point(paletteCombo.Bounds.Width, 0), window)!.Value.X;
            var cardsRight = modeCards.Max(card => card.TranslatePoint(
                new Point(card.Bounds.Width, 0), window)!.Value.X);
            Assert.True(cardsRight <= comboRight + 1,
                $"模式卡行右缘 {cardsRight:F1}px 超出了画板下拉右缘 {comboRight:F1}px");
            var cardWidths = modeCards.Select(card => card.Bounds.Width).ToArray();
            Assert.True(cardWidths.All(width => width >= 80),
                $"模式卡宽度 {string.Join(", ", cardWidths.Select(width => width.ToString("F1")))}（三张应等宽铺满所在列）");

            // 选中卡的描边是强调色环，未选中卡透明（:checked 后代选择器确实生效）
            var cardByCaption = modeCards.ToDictionary(
                card => card.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Classes.Contains("mode-caption")).Text!);
            Border FrameOf(string caption) => cardByCaption[caption]
                .GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("mode-frame"));
            Assert.True(Assert.IsAssignableFrom<ISolidColorBrush>(FrameOf(Translations.Instance.ThemeDark).BorderBrush).Color.A > 0);
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(FrameOf(Translations.Instance.ThemeLight).BorderBrush).Color.A);

            // 单档画板（Nord）：不可用的日间/系统卡压暗但仍在原位，原因写在卡下方
            vm.SelectedPalette = nordOption;
            Dispatcher.UIThread.RunJobs();

            // 收起后的画板下拉用同一模板：选中单档画板时徽标直接可见，不用展开
            var closedBadge = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("chip")
                    && (border.Child as TextBlock)?.Text == Translations.Instance.ThemeOnlyDarkBadge);
            Assert.NotNull(closedBadge);
            Assert.True(closedBadge!.IsVisible);
            Assert.False(cardByCaption[Translations.Instance.ThemeLight].IsEnabled);
            Assert.True(cardByCaption[Translations.Instance.ThemeDark].IsEnabled);
            Assert.False(cardByCaption[Translations.Instance.ThemeSystem].IsEnabled);
            Assert.True(vm.HasSingleModeNote);
            var note = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == vm.SingleModeNote);
            Assert.True(note.IsVisible);
            Assert.Contains("Nord", note.Text!);

            // 切回多档画板：三张卡恢复可用，说明行消失
            vm.SelectedPalette = SettingsViewModel.PaletteOptions.First(o => o.Value == "solarized");
            Dispatcher.UIThread.RunJobs();
            Assert.All(modeCards, card => Assert.True(card.IsEnabled));
            Assert.False(note.IsVisible);

            // 模式三态文案（经 Translations 引用，语言无关）
            var captions = modeCards.Select(card =>
                card.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Classes.Contains("mode-caption")).Text).ToList();
            Assert.Equal(
                new[]
                {
                    Translations.Instance.ThemeLight,
                    Translations.Instance.ThemeDark,
                    Translations.Instance.ThemeSystem,
                },
                captions);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            appThemeService.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
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
            Assert.Contains(Translations.Instance.SharePosterSettingsHint, texts);
            // 逐项说明已改为悬停选项名：ToolTip 里的文案同样随语言实时重译（收益于同一条绑定链）
            var tipTexts = window.GetVisualDescendants().OfType<Control>()
                .Select(ToolTip.GetTip).OfType<ToolTip>()
                .Select(t => (t.Content as TextBlock)?.Text).ToList();
            Assert.Contains("Saved automatically; takes effect the next time the background refresh service starts.", tipTexts);
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
    public void About_panel_star_appeal_links_inline_to_repository()
    {
        // 求 Star 行：整句文案里的「Star」是句中链接（LinkTextBlock 拆句渲染 + 命中测试开仓库，
        // Avalonia 无内联 Hyperlink）。断言语言无关——全文与链接词都取自同一出处，链接目标取自 VM。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var starLine = window.GetVisualDescendants().OfType<LinkTextBlock>().Single();
        Assert.Equal(Translations.Instance.AboutStarHint, starLine.SourceText);
        Assert.Equal(vm.AboutRepositoryUrl, starLine.LinkUrl);

        // 拆句正确：渲染出的全文 = 整句，链接段恰好圈住「Star」一词
        var rendered = string.Concat(starLine.Inlines!.OfType<Run>().Select(r => r.Text));
        Assert.Equal(Translations.Instance.AboutStarHint, rendered);
        Assert.Equal("Star", rendered.Substring(starLine.LinkStart, starLine.LinkLength));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void About_panel_sits_last_and_version_value_is_selectable()
    {
        // 版面收口：「关于」按直觉落到设置页最下面（此前夹在本地路径与跨设备同步之间）；
        // 版本行改 SelectableTextBlock——报障时能直接选中复制版本号，不再靠手抄。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Classes.Contains("page-root"));
        var root = Assert.IsType<StackPanel>(scroll.Content);

        static string? PanelTitle(Border panel) => panel.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("panel-title"))?.Text;

        var panels = root.Children.OfType<Border>().ToList();
        Assert.Equal(Translations.Instance.AboutTitle, PanelTitle(panels[^1]));

        var version = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .SingleOrDefault(t => t.Text == vm.UpdateAppVersion);
        Assert.NotNull(version);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Per_item_explanations_hover_on_the_option_name_instead_of_standing_small_text()
    {
        // S-004 版面收口：逐项说明从小字常驻改为悬停选项名本身（无图标）。断言——每项说明都挂在
        // 对应标签/开关的 ToolTip 上，且页面上不再有同文案的常驻 TextBlock（确实退场）。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var labels = window.GetVisualDescendants().OfType<TextBlock>().ToList();
        var checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();

        (string Label, string Hint)[] labelHints =
        {
            (Translations.Instance.LabelTheme, Translations.Instance.ThemeHint),
            (Translations.Instance.ThemeModeLabel, Translations.Instance.ThemeModeHint),
            (Translations.Instance.LabelLanguage, Translations.Instance.LanguageHint),
            (Translations.Instance.LabelRefreshInterval, Translations.Instance.RefreshHint),
            (Translations.Instance.SharePosterDefaultMask, Translations.Instance.SharePosterMaskHint),
        };
        foreach (var (label, hint) in labelHints)
        {
            var host = labels.Single(t => t.Classes.Contains("setting-label") && t.Text == label);
            Assert.Equal(hint, TipText(host));
        }

        (string Option, string Hint)[] checkHints =
        {
            (Translations.Instance.CheckMinimizeToTrayOnClose, Translations.Instance.CheckMinimizeToTrayOnCloseHint),
            (Translations.Instance.CheckAutoStartOnBoot, Translations.Instance.CheckAutoStartOnBootHint),
            (Translations.Instance.CheckFileLogging, Translations.Instance.CheckFileLoggingHint),
        };
        foreach (var (option, hint) in checkHints)
        {
            var host = checks.Single(c => c.Content as string == option);
            Assert.Equal(hint, TipText(host));
        }

        var standingTexts = labels.Select(t => t.Text).ToHashSet();
        foreach (var hint in labelHints.Select(x => x.Hint).Concat(checkHints.Select(x => x.Hint)))
        {
            Assert.DoesNotContain(hint, standingTexts);
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Setting_labels_align_vertically_with_the_control_in_the_same_row()
    {
        // 标签列 + 控件列同行的表单（132px 标签列、右侧 ComboBox/TextBox 高约 32px）：单行标签
        // 只有 17px 行高，默认 Stretch 让 TextBlock 拉满整行、文字贴行顶——盒子居中但字形不在
        // 控件文字那一轴上。断言每行标签纵向居中（不拉伸），且盒子中心与同行控件中心对齐。
        var vm = CreateViewModel();
        var window = new Window { Width = 960, Height = 720, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var pairs = new List<(TextBlock Label, Control Control)>();
        foreach (var label in window.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("setting-label")))
        {
            if (label.GetVisualParent() is not Grid grid)
            {
                continue; // 标签在控件上方的堆叠用法（非同行对比，不在本断言范围）
            }

            var row = Grid.GetRow(label);
            var control = grid.Children.OfType<Control>()
                .FirstOrDefault(c => c != label && Grid.GetRow(c) == row && Grid.GetColumn(c) == 1);
            if (control is not null)
            {
                pairs.Add((label, control));
            }
        }

        Assert.NotEmpty(pairs);
        foreach (var (label, control) in pairs)
        {
            if (label.Bounds.Height <= 0 || control.Bounds.Height <= 0)
            {
                continue; // 折叠区（未启用同步等）没有布局，无从比较
            }

            // Stretch 会把 TextBlock 拉满整行，字形贴顶；只有居中才与控件同轴
            Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);

            var delta = Math.Abs(label.Bounds.Center.Y - control.Bounds.Center.Y);
            Assert.True(delta <= 2,
                $"标签「{label.Text}」纵向中心与同行控件相差 {delta:F1}px（应 ≤2px）");
        }

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

    /// <summary>取控件上挂的悬停说明文案（设置项说明直挂标签/开关的 ToolTip 内 TextBlock）。</summary>
    private static string TipText(Control host)
    {
        var tip = Assert.IsType<ToolTip>(ToolTip.GetTip(host));
        return Assert.IsType<TextBlock>(tip.Content).Text ?? string.Empty;
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
