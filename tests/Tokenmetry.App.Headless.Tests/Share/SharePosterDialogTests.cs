using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tokenmetry.App.Headless.Tests.Controls;
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
    private static SharePosterData Snapshot(SharePosterRequest request)
    {
        var row = new DimensionUsageRow
        {
            Key = "ZCode",
            TotalTokens = 1_000_000,
            Cost = new AggregatedCost(1m, false),
        };

        return SharePosterSnapshot.Build(
            request, ChartTestHelpers.DailyPoints30(), [row], [row], [row], sessionCount: 3);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Heatmap), Snapshot);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Milestone), Snapshot);
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
        var landscapeStart = new SharePosterDialogViewModel(
            new SharePosterRequest(
                SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
                new DateOnly(2026, 10, 17), new DateOnly(2026, 11, 2), SharePosterStyle.Milestone),
            Snapshot);
        Assert.Equal(SharePosterLayout.Portrait, landscapeStart.CurrentRequest.Layout);
    }

    [Fact]
    public void Rhythm_style_locks_landscape_and_shows_its_own_notice()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Rhythm), Snapshot);
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
        var vm = new SharePosterDialogViewModel(request, Snapshot);

        Assert.Equal(SharePosterLayout.Landscape, vm.Layout);
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
        Assert.Equal("landscape", vm.SelectedLayout.Value);
    }

    [Fact]
    public void Style_chips_cover_the_registered_styles()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);

        Assert.Equal(5, vm.StyleOptions.Count);
        Assert.Equal(
            ["tray", "overview", "heatmap", "rhythm", "milestone"],
            vm.StyleOptions.Select(o => o.Value).ToArray());
        Assert.All(vm.StyleOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }

    [Fact]
    public void Tray_style_refuses_landscape_even_for_multi_day_ranges()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Tray), Snapshot);
        Assert.False(vm.CanChooseLayout);

        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Overview_style_allows_landscape_even_for_single_day_ranges()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: true, SharePosterStyle.Overview), Snapshot);
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
        var vm = new SharePosterDialogViewModel(request, Snapshot);

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
        Assert.Equal("portrait", vm.SelectedLayout.Value);
    }

    [Fact]
    public void Switching_style_back_to_tray_pulls_the_layout_back_to_portrait()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Overview), Snapshot);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Tray), Snapshot);
        var before = vm.Preview;

        vm.Style = SharePosterStyle.Overview;

        Assert.NotSame(before, vm.Preview);
        Assert.Equal(SharePosterStyle.Overview, vm.Preview.Data.Request.Style);
        Assert.Equal(SharePosterStyle.Overview, vm.CurrentRequest.Style);
    }

    [Fact]
    public void Segmented_options_start_on_the_current_choice_and_drive_the_enum()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);

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
    public void Layout_notice_follows_the_style_capability()
    {
        var tray = new SharePosterDialogViewModel(Request(singleDay: true, SharePosterStyle.Tray), Snapshot);
        var overview = new SharePosterDialogViewModel(Request(singleDay: true, SharePosterStyle.Overview), Snapshot);

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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false, SharePosterStyle.Tray), Snapshot);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);

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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);
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
        var vm = new SharePosterDialogViewModel(Request(singleDay: true, SharePosterStyle.Tray), Snapshot);
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
            DataContext = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot),
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
            DataContext = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot),
        };

        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var poster = dialog.GetVisualDescendants().OfType<SharePosterView>().FirstOrDefault();
        Assert.NotNull(poster);
        Assert.True(poster!.PosterRootControl.Bounds.Width > 0, "海报版面应完成布局（导出才能光栅化）");

        dialog.Close();
    }
}
