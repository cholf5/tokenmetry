using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 生成对话框（P5）：选项驱动预览重建、信息卡样式禁用横版（样式与时间范围解耦后的唯一竖版锁）、
/// 窗口能真的把海报渲染出来。断言不涉及任何界面文案（语言无关纪律）。
/// </summary>
public class SharePosterDialogTests
{
    private static DimensionUsageRow Row() => new()
    {
        Key = "ZCode",
        TotalTokens = 1_000_000,
        Cost = new AggregatedCost(1m, false),
    };

    /// <summary>一轮可渲染数据（范围取自请求；无补查、KPI 缺位走维度行求和回退）。</summary>
    private static SharePosterDataBundle Bundle(SharePosterRequest request) => new(
        request.RangeStart, request.RangeEnd,
        ChartTestHelpers.DailyPoints30(), [Row()], [Row()], [Row()], SessionCount: 3,
        Kpi: null, Hourly: null, HourlyCutoffHour: null, WeeklyRhythm: null);

    /// <summary>
    /// 标准构造：绑定一份真实 FilterStateService（对话框里的数据范围行就是它，与顶栏共享同一实例），
    /// reload 缺省恒返同一份 Bundle（不改筛选的用例不会触发重查，改了的用例自带 reload）。
    /// </summary>
    private static SharePosterDialogViewModel NewVm(
        SharePosterRequest request,
        Action<SharePosterStyle, SharePosterLayout, SharePosterTheme, SharePosterProjectMask>? onChoiceChanged = null,
        FilterStateService? filters = null,
        Func<CancellationToken, Task<SharePosterDataBundle>>? reload = null,
        Action<Func<Task>>? reloadScheduler = null)
    {
        var filterState = filters ?? new FilterStateService();
        var bundle = Bundle(request);
        return new SharePosterDialogViewModel(
            bundle, request.Style, request.Layout, request.Theme, request.ProjectMask,
            filterState,
            new TimeRangePickerViewModel(filterState, null),
            reload ?? (_ => Task.FromResult(bundle)),
            onChoiceChanged: onChoiceChanged,
            reloadScheduler: reloadScheduler);
    }

    private static SharePosterRequest Request(bool singleDay, SharePosterStyle? style = null) => new(
        SharePosterLayout.Portrait,
        SharePosterTheme.Dark,
        SharePosterProjectMask.Off,
        new DateOnly(2026, 10, 17),
        singleDay ? new DateOnly(2026, 10, 17) : new DateOnly(2026, 11, 2),
        style ?? (singleDay ? SharePosterStyle.Tray : SharePosterStyle.Overview));

    [Fact]
    public void Supported_layouts_are_declared_per_style()
    {
        // 能力表 = 版式合法性的唯一出处：每样式登记一次，对话框按它启用/禁用版式组并收口非法组合
        Assert.Equal(
            [SharePosterLayout.Portrait],
            SharePosterOptions.SupportedLayouts(SharePosterStyle.Tray));
        Assert.Equal(
            [SharePosterLayout.Portrait, SharePosterLayout.Landscape],
            SharePosterOptions.SupportedLayouts(SharePosterStyle.Overview));
        Assert.Equal(
            [SharePosterLayout.Landscape],
            SharePosterOptions.SupportedLayouts(SharePosterStyle.Heatmap));
        Assert.Equal(
            [SharePosterLayout.Landscape],
            SharePosterOptions.SupportedLayouts(SharePosterStyle.Rhythm));
        Assert.Equal(
            [SharePosterLayout.Portrait],
            SharePosterOptions.SupportedLayouts(SharePosterStyle.Milestone));
    }

    [Fact]
    public void Heatmap_style_locks_landscape_and_shows_its_own_notice()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Heatmap));
        Assert.False(vm.CanChooseLayout);
        Assert.True(vm.ShowLayoutNotice);
        Assert.Equal(
            Tokenmetry.App.Localization.Translations.Instance.SharePosterHeatmapLandscapeOnly,
            vm.LayoutNoticeText);

        // 竖版写入被收口回横版（与信息卡锁竖版同一机制，方向相反）
        vm.Layout = SharePosterLayout.Portrait;

        Assert.Equal(SharePosterLayout.Landscape, vm.Layout);
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Milestone_style_locks_portrait_and_collapses_a_landscape_start()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Milestone));
        Assert.False(vm.CanChooseLayout);
        Assert.True(vm.ShowLayoutNotice);
        Assert.Equal(
            Tokenmetry.App.Localization.Translations.Instance.SharePosterMilestonePortraitOnly,
            vm.LayoutNoticeText);

        // 横版写入被收口回竖版（与信息卡锁竖版同一机制）
        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);

        // 起值越界（里程碑 × 横版）同样收口
        var landscapeStart = NewVm(
            new SharePosterRequest(
                SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
                new DateOnly(2026, 10, 17), new DateOnly(2026, 11, 2), SharePosterStyle.Milestone));
        Assert.Equal(SharePosterLayout.Portrait, landscapeStart.CurrentRequest.Layout);
    }

    [Fact]
    public void Rhythm_style_locks_landscape_and_shows_its_own_notice()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Rhythm));
        Assert.False(vm.CanChooseLayout);
        Assert.True(vm.ShowLayoutNotice);
        Assert.Equal(
            Tokenmetry.App.Localization.Translations.Instance.SharePosterRhythmLandscapeOnly,
            vm.LayoutNoticeText);

        vm.Layout = SharePosterLayout.Portrait;

        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Heatmap_style_collapses_a_portrait_start_to_landscape()
    {
        var request = new SharePosterRequest(
            SharePosterLayout.Portrait,
            SharePosterTheme.Dark,
            SharePosterProjectMask.Off,
            new DateOnly(2026, 10, 17),
            new DateOnly(2026, 11, 2),
            SharePosterStyle.Heatmap);
        var vm = NewVm(request);

        Assert.Equal(SharePosterLayout.Landscape, vm.Layout);
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
        Assert.Equal("landscape", vm.SelectedLayout.Value);
    }

    [Fact]
    public void Style_chips_cover_the_registered_styles()
    {
        var vm = NewVm(Request(singleDay: false));

        Assert.Equal(5, vm.StyleOptions.Count);
        Assert.Equal(
            ["tray", "overview", "heatmap", "rhythm", "milestone"],
            vm.StyleOptions.Select(o => o.Value).ToArray());
        Assert.All(vm.StyleOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }

    [Fact]
    public void Tray_style_refuses_landscape_even_for_multi_day_ranges()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Tray));
        Assert.False(vm.CanChooseLayout);

        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Overview_style_allows_landscape_even_for_single_day_ranges()
    {
        var vm = NewVm(Request(singleDay: true, SharePosterStyle.Overview));
        Assert.True(vm.CanChooseLayout);

        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Initial_layout_outside_the_style_capability_is_collapsed()
    {
        // 真实路径：单日范围缺省信息卡 + 设置默认横版 → 起值就收口到能力表里首个受支持版式，
        // 不让对话框一进来就展示"选项与版面不一致"的预览
        var request = new SharePosterRequest(
            SharePosterLayout.Landscape,
            SharePosterTheme.Dark,
            SharePosterProjectMask.Off,
            new DateOnly(2026, 10, 17),
            new DateOnly(2026, 10, 17),
            SharePosterStyle.Tray);
        var vm = NewVm(request);

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
        Assert.Equal("portrait", vm.SelectedLayout.Value);
    }

    [Fact]
    public void Switching_style_back_to_tray_pulls_the_layout_back_to_portrait()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Overview));
        vm.Layout = SharePosterLayout.Landscape;
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);

        vm.Style = SharePosterStyle.Tray;

        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
        // 分段控件同步：信息卡样式选中、版式只剩竖版勾着
        Assert.Equal("tray", vm.SelectedStyle.Value);
        Assert.True(vm.LayoutOptions.Single(o => o.IsSelected).Value == "portrait");
    }

    [Fact]
    public void Style_option_drives_the_request_and_the_preview()
    {
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Tray));
        var before = vm.Preview;

        vm.Style = SharePosterStyle.Overview;

        Assert.NotSame(before, vm.Preview);
        Assert.Equal(SharePosterStyle.Overview, vm.Preview.Data.Request.Style);
        Assert.Equal(SharePosterStyle.Overview, vm.CurrentRequest.Style);
    }

    [Fact]
    public void Segmented_options_start_on_the_current_choice_and_drive_the_enum()
    {
        var vm = NewVm(Request(singleDay: false));

        // 起值：每个分组恰好一项选中，且选中项与枚举一致
        Assert.Single(vm.StyleOptions, o => o.IsSelected);
        Assert.Single(vm.LayoutOptions, o => o.IsSelected);
        Assert.Single(vm.ThemeOptions, o => o.IsSelected);
        Assert.Single(vm.MaskOptions, o => o.IsSelected);
        Assert.True(vm.StyleOptions.Single(o => o.IsSelected).Value == "overview");
        Assert.True(vm.LayoutOptions.Single(o => o.IsSelected).Value == "portrait");

        // 分段控件勾选 = 把 IsSelected 置 true（RadioButton 双向绑定的写入面）
        var light = vm.ThemeOptions.Single(o => o.Value == "light");
        light.IsSelected = true;

        Assert.Equal(SharePosterTheme.Light, vm.Theme);
        Assert.Equal("light", vm.SelectedTheme.Value);
        Assert.Single(vm.ThemeOptions, o => o.IsSelected);
        Assert.Same(light, vm.ThemeOptions.Single(o => o.IsSelected));
    }

    [Fact]
    public void Choice_changes_report_to_the_persistence_hook_once_construction_stays_silent()
    {
        // 选项即存：对话框里任一选项实际变化都回报持有方（真实路径回写 settings.json），
        // 构造期只赋起值不回报——起值越界被收口（真实路径：设置默认横版 + 信息卡锁竖版）也不算"用户选过"
        var reports = new List<(SharePosterStyle Style, SharePosterLayout Layout, SharePosterTheme Theme, SharePosterProjectMask Mask)>();
        var vm = NewVm(
            Request(singleDay: false, SharePosterStyle.Overview),
            onChoiceChanged: (style, layout, theme, mask) => reports.Add((style, layout, theme, mask)));

        Assert.Empty(reports);

        vm.Theme = SharePosterTheme.Light;

        Assert.Single(reports);
        Assert.Equal(
            (SharePosterStyle.Overview, SharePosterLayout.Portrait, SharePosterTheme.Light, SharePosterProjectMask.Off),
            reports[0]);

        // 样式切换连带版式收口（总览横版 → 信息卡锁竖版）：回报的最终组合 = 收口后的当前请求
        vm.Layout = SharePosterLayout.Landscape;
        vm.Style = SharePosterStyle.Tray;

        var last = reports[^1];
        Assert.True(reports.Count > 2, "样式与版式各报一次（幂等冗余可接受），收口后至少再多两条");
        Assert.Equal(SharePosterStyle.Tray, last.Style);
        Assert.Equal(SharePosterLayout.Portrait, last.Layout);
        Assert.Equal(vm.CurrentRequest.Style, last.Style);
        Assert.Equal(vm.CurrentRequest.Layout, last.Layout);
        Assert.Equal(vm.CurrentRequest.Theme, last.Theme);
        Assert.Equal(vm.CurrentRequest.ProjectMask, last.Mask);
    }

    [Fact]
    public void Constructor_does_not_report_choices_even_when_the_start_needs_collapsing()
    {
        // 起值越界（里程碑 × 横版）在构造期收口：从未有"用户选择"发生，不得触发回写
        var reports = new List<SharePosterStyle>();
        _ = NewVm(
            new SharePosterRequest(
                SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
                new DateOnly(2026, 10, 17), new DateOnly(2026, 11, 2), SharePosterStyle.Milestone),
            onChoiceChanged: (style, _, _, _) => reports.Add(style));

        Assert.Empty(reports);
    }

    [Fact]
    public void Layout_notice_follows_the_style_capability()
    {
        var tray = NewVm(Request(singleDay: true, SharePosterStyle.Tray));
        var overview = NewVm(Request(singleDay: true, SharePosterStyle.Overview));

        // 信息卡锁竖版：说明行出现，文案来自信息卡专属 key（语言无关：两边同一出处）
        Assert.True(tray.ShowLayoutNotice);
        Assert.Equal(
            Tokenmetry.App.Localization.Translations.Instance.SharePosterTrayPortraitOnly,
            tray.LayoutNoticeText);
        Assert.False(overview.ShowLayoutNotice);
        Assert.Null(overview.LayoutNoticeText);
    }

    [AvaloniaFact]
    public void Dialog_layout_group_and_notice_follow_the_style_capability()
    {
        // 端到端绑定链：样式 → 能力表 → 版式组启用/禁用 + 说明行显隐
        var vm = NewVm(Request(singleDay: false, SharePosterStyle.Tray));
        var dialog = new SharePosterDialog { DataContext = vm };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var layoutHost = dialog.GetVisualDescendants().OfType<ItemsControl>()
            .First(c => c.Name == "LayoutOptionsHost");
        var notice = dialog.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "LayoutNotice");
        Assert.False(layoutHost.IsEnabled);
        Assert.True(notice.IsVisible);

        vm.Style = SharePosterStyle.Overview;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(layoutHost.IsEnabled);
        Assert.False(notice.IsVisible);

        dialog.Close();
    }

    [Fact]
    public void Options_are_localized_labels_not_enum_identifiers()
    {
        var vm = NewVm(Request(singleDay: false));

        // 分段控件的选项是带本地化 Label 的对象（不是枚举值）；对话框每次新建一批实例
        Assert.Equal(SharePosterOptions.KeyOf(vm.Theme), vm.SelectedTheme.Value);
        Assert.All(vm.LayoutOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
        Assert.Equal(
            vm.LayoutOptions.Count,
            vm.LayoutOptions.Select(o => o.Label).Distinct().Count());

        var landscape = vm.LayoutOptions.Single(o => o.Value == "landscape");
        vm.SelectedLayout = landscape;
        Assert.Equal(SharePosterLayout.Landscape, vm.Layout);
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
        Assert.Same(landscape, vm.SelectedLayout);
    }

    [Fact]
    public void Changing_an_option_rebuilds_the_preview()
    {
        var vm = NewVm(Request(singleDay: false));
        var before = vm.Preview;

        vm.Theme = SharePosterTheme.Light;

        Assert.NotSame(before, vm.Preview);
        Assert.Equal(SharePosterTheme.Light, vm.Preview.Data.Request.Theme);
        Assert.Equal(SharePosterTheme.Light, vm.CurrentRequest.Theme);
    }

    [AvaloniaFact]
    public void Switching_to_landscape_resizes_the_rendered_poster()
    {
        // 端到端绑定链：选项 → Preview 重建 → 海报视图 DataContext 变化 → 版面换宽度。
        // P5 阶段这条链是断的（选横版仍出竖版），本用例锁住它。
        var vm = NewVm(Request(singleDay: false));
        var dialog = new SharePosterDialog { DataContext = vm };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var poster = dialog.GetVisualDescendants().OfType<SharePosterView>().First();
        Assert.Equal(SharePosterView.PortraitWidth, poster.PosterRootControl.Bounds.Width, 1);

        vm.Layout = SharePosterLayout.Landscape;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(SharePosterView.LandscapeWidth, poster.PosterRootControl.Bounds.Width, 1);

        dialog.Close();
    }

    [AvaloniaFact]
    public void Switching_style_swaps_the_rendered_base()
    {
        // 样式与范围解耦的端到端链：同一份单日数据，切样式直接换基底（导出根随之切换）。
        var vm = NewVm(Request(singleDay: true, SharePosterStyle.Tray));
        var dialog = new SharePosterDialog { DataContext = vm };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var poster = dialog.GetVisualDescendants().OfType<SharePosterView>().First();
        Assert.True(poster.IsTrayStyle);
        Assert.Same(
            poster.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot"),
            poster.PosterRootControl);

        vm.Style = SharePosterStyle.Overview;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.False(poster.IsTrayStyle);
        Assert.Same(
            poster.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot"),
            poster.PosterRootControl);

        dialog.Close();
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task Copy_keeps_the_bitmap_alive_for_later_paste()
    {
        var dialog = new SharePosterDialog
        {
            DataContext = NewVm(Request(singleDay: false)),
        };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        await dialog.CopyAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 状态行如实报告成功；剪贴板里能读回同一张海报（不是已释放的空位图）
        var status = dialog.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "StatusText");
        Assert.Equal(Tokenmetry.App.Localization.Translations.Instance.SharePosterCopied, status.Text);

        var pasted = await dialog.Clipboard!.TryGetBitmapAsync();
        Assert.NotNull(pasted);
        Assert.True(pasted!.PixelSize.Width > 1000, "海报 2× 导出应远宽于 1000px");

        dialog.Close();
    }

    [AvaloniaFact]
    public void Dialog_hosts_and_renders_the_poster()
    {
        var dialog = new SharePosterDialog
        {
            DataContext = NewVm(Request(singleDay: false)),
        };

        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var poster = dialog.GetVisualDescendants().OfType<SharePosterView>().FirstOrDefault();
        Assert.NotNull(poster);
        Assert.True(poster!.PosterRootControl.Bounds.Width > 0, "海报版面应完成布局（导出才能光栅化）");

        dialog.Close();
    }

    [AvaloniaFact]
    public void Preview_fits_the_pane_by_default_without_distortion()
    {
        // 默认态 = 整体缩放到预览区全貌（旧实现 100% + 滚动条，长竖版必然只见局部）
        var dialog = new SharePosterDialog
        {
            DataContext = NewVm(Request(singleDay: false)),
        };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "PreviewScroll");
        var host = dialog.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        var poster = dialog.GetVisualDescendants().OfType<SharePosterView>().First();

        // 全貌：内容不超出视口（1px 容差吸收 fit 微内缩前的浮点毛刺）
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, "横向不应溢出预览区");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, "纵向不应溢出预览区");

        // 等比：渲染宽高比与海报自然比例一致——少了 Grid 垫层，ContentPresenter 会把
        // 比视口小的内容拉伸铺满，这里就是变形现场（2 位小数足以暴露铺满级变形）
        var naturalRatio = poster.Bounds.Width / poster.Bounds.Height;
        var renderedRatio = host.Bounds.Width / host.Bounds.Height;
        Assert.Equal(naturalRatio, renderedRatio, 2);

        // 居中：四周留白对称。LTC 内部按设备像素取整，fit 的亚像素余量（FitInset ~0.1%）会
        // 整块落到单边，表现为 ±1px 的留白不对称——肉眼不可见，容差放到 1.5px
        var position = host.TranslatePoint(default, scroll) ?? default;
        var horizontalBias = position.X - (scroll.Bounds.Width - host.Bounds.Width - position.X);
        var verticalBias = position.Y - (scroll.Bounds.Height - host.Bounds.Height - position.Y);
        Assert.True(Math.Abs(horizontalBias) < 1.5, $"水平应居中，偏差 {horizontalBias:0.##}px");
        Assert.True(Math.Abs(verticalBias) < 1.5, $"垂直应居中，偏差 {verticalBias:0.##}px");

        dialog.Close();
    }

    [AvaloniaFact]
    public void Preview_zooms_with_ctrl_wheel_and_resets_on_double_tap()
    {
        var dialog = new SharePosterDialog
        {
            DataContext = NewVm(Request(singleDay: false)),
        };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "PreviewScroll");
        var host = dialog.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        var fitWidth = host.Bounds.Width;
        var anchor = scroll.TranslatePoint(
            new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), dialog) ?? default;

        // Ctrl+滚轮一齿 = 1.1×：放大超过 fit，绑定轴开始溢出（滚动接管平移）
        dialog.MouseWheel(anchor, new Vector(0, 1), RawInputModifiers.Control);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(host.Bounds.Width > fitWidth * 1.05, "Ctrl+滚轮应放大预览");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1, "放大后纵向应可滚动");

        // 双击复位回 fit
        dialog.MouseDown(anchor, MouseButton.Left);
        dialog.MouseUp(anchor, MouseButton.Left);
        dialog.MouseDown(anchor, MouseButton.Left);
        dialog.MouseUp(anchor, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(Math.Abs(host.Bounds.Width - fitWidth) < fitWidth * 0.01, "双击应复位回 fit");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, "复位后不应再溢出");

        dialog.Close();
    }

    [AvaloniaFact]
    public void Preview_keeps_fit_when_wheeling_without_modifiers()
    {
        // 不带 Ctrl/⌘ 的滚轮保留原生滚动语义，不劫持成缩放
        var dialog = new SharePosterDialog
        {
            DataContext = NewVm(Request(singleDay: false)),
        };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "PreviewScroll");
        var host = dialog.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        var anchor = scroll.TranslatePoint(
            new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), dialog) ?? default;

        dialog.MouseWheel(anchor, new Vector(0, 3), RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(host.Bounds.Width, dialog.GetVisualDescendants().OfType<LayoutTransformControl>()
            .Single().Bounds.Width, 1);

        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Filter_change_schedules_a_reload_and_applies_the_new_bundle()
    {
        // 数据范围筛选 = 与主窗口共享同一 FilterStateService（同一实例即回写）；改筛选 → 调度重查，
        // 新数据轮整轮替换：范围随数据走，海报选项（样式等）不动
        var filters = new FilterStateService();
        var scheduled = new List<Func<Task>>();
        var multiDay = Bundle(Request(singleDay: false));
        var vm = NewVm(
            Request(singleDay: true, SharePosterStyle.Overview),
            filters: filters,
            reload: _ => Task.FromResult(multiDay),
            reloadScheduler: trigger => scheduled.Add(trigger));

        Assert.Same(filters, vm.Filters);
        Assert.Equal(Request(singleDay: true).RangeStart, vm.CurrentRequest.RangeStart);

        filters.Agent = new AgentOption("claude-code", "Claude Code");
        Assert.Equal("claude-code", filters.Agent.Id); // 回写：对话框里改的就是全局筛选本身

        var trigger = Assert.Single(scheduled);
        await trigger();

        Assert.Equal(multiDay.RangeStart, vm.CurrentRequest.RangeStart);
        Assert.Equal(multiDay.RangeEnd, vm.CurrentRequest.RangeEnd);
        Assert.Equal(multiDay.RangeStart, vm.Preview.Data.Request.RangeStart);
        Assert.Equal(SharePosterStyle.Overview, vm.CurrentRequest.Style);
        Assert.False(vm.IsUpdating);
        Assert.False(vm.HasReloadError);
    }

    [Fact]
    public void Poster_option_changes_do_not_requery()
    {
        // 四个海报选项只重排同一份快照，不触发重查（重查只属于数据范围筛选）
        var filters = new FilterStateService();
        var scheduled = new List<Func<Task>>();
        var vm = NewVm(
            Request(singleDay: false),
            filters: filters,
            reloadScheduler: trigger => scheduled.Add(trigger));

        vm.Theme = SharePosterTheme.Light;
        vm.Layout = SharePosterLayout.Landscape;
        vm.ProjectMask = SharePosterProjectMask.Full;

        Assert.Empty(scheduled);
    }

    [AvaloniaFact]
    public async Task Rapid_filter_changes_apply_only_the_latest_bundle()
    {
        // 防抖窗口期内的连续变更：新一轮开始即作废旧代——旧数据迟到不覆盖新筛选的结果
        var filters = new FilterStateService();
        var scheduled = new List<Func<Task>>();
        var seenTokens = new List<CancellationToken>();
        var stale = Bundle(Request(singleDay: true, SharePosterStyle.Overview));
        var latest = Bundle(Request(singleDay: false, SharePosterStyle.Overview));
        var firstQuery = new TaskCompletionSource<SharePosterDataBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = NewVm(
            Request(singleDay: true, SharePosterStyle.Overview),
            filters: filters,
            reload: ct =>
            {
                seenTokens.Add(ct);
                return seenTokens.Count == 1 ? firstQuery.Task : Task.FromResult(latest);
            },
            reloadScheduler: trigger => scheduled.Add(trigger));

        filters.Agent = new AgentOption("a1");
        var staleRun = scheduled[0](); // 在途（未完成）
        filters.Agent = new AgentOption("a2");
        await scheduled[1]();

        Assert.Equal(latest.RangeStart, vm.CurrentRequest.RangeStart);
        Assert.Equal(latest.RangeStart, vm.Preview.Data.Request.RangeStart);
        Assert.True(seenTokens[0].IsCancellationRequested, "旧代应被按代取消");

        firstQuery.SetResult(stale); // 旧数据迟到
        await staleRun;

        Assert.Equal(latest.RangeStart, vm.CurrentRequest.RangeStart);
        Assert.Equal(latest.RangeStart, vm.Preview.Data.Request.RangeStart);
        Assert.False(vm.IsUpdating);
    }

    [AvaloniaFact]
    public async Task Reload_failure_keeps_the_previous_preview_and_reports_inline()
    {
        // 重查失败：就地报错（连续失败不 toast 轰炸），旧预览保留可继续导出
        var filters = new FilterStateService();
        var scheduled = new List<Func<Task>>();
        var initial = Bundle(Request(singleDay: false, SharePosterStyle.Overview));
        var vm = NewVm(
            Request(singleDay: false, SharePosterStyle.Overview),
            filters: filters,
            reload: _ => Task.FromException<SharePosterDataBundle>(new InvalidOperationException("boom")),
            reloadScheduler: trigger => scheduled.Add(trigger));
        var previewBefore = vm.Preview;

        filters.Agent = new AgentOption("a1");
        await Assert.Single(scheduled)();

        Assert.Same(previewBefore, vm.Preview);
        Assert.Equal(initial.RangeStart, vm.CurrentRequest.RangeStart);
        Assert.True(vm.HasReloadError);
        Assert.Contains("boom", vm.ReloadError);
        Assert.False(vm.IsUpdating);

        // 再次动手 = 重试：旧错误先清，本轮仍失败则就地再报
        filters.Agent = new AgentOption("a2");
        Assert.False(vm.HasReloadError);
        await scheduled[1]();
        Assert.True(vm.HasReloadError);
    }

    [AvaloniaFact]
    public async Task Detach_stops_scheduled_reloads_and_drops_in_flight_data()
    {
        // 关窗解绑：在途数据整轮丢弃；退订后改筛选不再触发调度（VM 每开一次新建，不解绑会沿
        // FilterStateService 的订阅把整棵 VM 钉在进程里）
        var filters = new FilterStateService();
        var scheduled = new List<Func<Task>>();
        var lateBundle = Bundle(Request(singleDay: false, SharePosterStyle.Overview));
        var gate = new TaskCompletionSource<SharePosterDataBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = NewVm(
            Request(singleDay: true, SharePosterStyle.Overview),
            filters: filters,
            reload: _ => gate.Task,
            reloadScheduler: trigger => scheduled.Add(trigger));
        var previewBefore = vm.Preview;

        filters.Agent = new AgentOption("a1");
        var run = scheduled[0]();
        vm.Detach();
        gate.SetResult(lateBundle);
        await run;

        Assert.Equal(Request(singleDay: true).RangeStart, vm.CurrentRequest.RangeStart);
        Assert.Same(previewBefore, vm.Preview);

        filters.Agent = new AgentOption("a2");
        Assert.Single(scheduled);
    }

    [AvaloniaFact]
    public async Task Debounced_reload_coalesces_rapid_filter_changes()
    {
        // 默认调度器（真实 DispatcherTimer）的防抖冒烟——生产里最容易藏竞态的 Tick 闭包
        // 唯一用例：防抖窗口内连续变更折叠为一次重查。真实等待 + 轮询泵队，上限 2s 防挂死。
        var filters = new FilterStateService();
        var calls = 0;
        var applied = new TaskCompletionSource<SharePosterDataBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var multiDay = Bundle(Request(singleDay: false));
        var vm = NewVm(
            Request(singleDay: true, SharePosterStyle.Overview),
            filters: filters,
            reload: _ =>
            {
                calls++;
                applied.TrySetResult(multiDay);
                return Task.FromResult(multiDay);
            }); // 不注入 scheduler：走生产默认的防抖计时器

        filters.Agent = new AgentOption("a1");
        filters.Agent = new AgentOption("a2");
        filters.Agent = new AgentOption("a3");

        await Task.WhenAny(applied.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(applied.Task.IsCompleted, "防抖窗口过后应恰好执行一次重查");
        Assert.Equal(1, calls);
        Assert.Equal(multiDay.RangeStart, vm.CurrentRequest.RangeStart);
    }

    [AvaloniaFact]
    public void Filter_row_binds_the_shared_filter_state_and_writes_back()
    {
        // 端到端绑定链：对话框筛选行 = 全局筛选（初值如实显示；改视图控件即写回全局）
        var filters = new FilterStateService();
        var extraAgent = new AgentOption("claude-code", "Claude Code");
        filters.UpdateAgentOptions([AgentOption.All, extraAgent]);
        var vm = NewVm(Request(singleDay: false), filters: filters);
        var dialog = new SharePosterDialog { DataContext = vm };
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var combos = dialog.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count); // Agent / 模型 / 项目（时间范围是 DropDownButton，不在其列）
        var agentCombo = combos.Single(c => ReferenceEquals(c.ItemsSource, filters.AgentOptions));
        Assert.Same(filters.Agent, agentCombo.SelectedItem);
        Assert.Same(
            filters.Model,
            combos.Single(c => ReferenceEquals(c.ItemsSource, filters.ModelOptions)).SelectedItem);
        Assert.Same(
            filters.Project,
            combos.Single(c => ReferenceEquals(c.ItemsSource, filters.ProjectOptions)).SelectedItem);

        agentCombo.SelectedItem = extraAgent;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("claude-code", filters.Agent.Id);

        dialog.Close();
    }
}
