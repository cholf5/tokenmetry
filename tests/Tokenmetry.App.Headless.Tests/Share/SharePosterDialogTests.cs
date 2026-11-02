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
/// 生成对话框（P5）：选项驱动预览重建、单日禁用横版、窗口能真的把海报渲染出来。
/// 断言不涉及任何界面文案（语言无关纪律）。
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

    private static SharePosterRequest Request(bool singleDay) => new(
        SharePosterLayout.Portrait,
        SharePosterTheme.Dark,
        SharePosterProjectMask.Off,
        new DateOnly(2026, 10, 17),
        singleDay ? new DateOnly(2026, 10, 17) : new DateOnly(2026, 11, 2));

    [Fact]
    public void Single_day_window_refuses_landscape()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: true), Snapshot);
        Assert.False(vm.CanChooseLandscape);

        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Portrait, vm.Layout);
        Assert.Equal(SharePosterLayout.Portrait, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Multi_day_window_allows_landscape()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);
        Assert.True(vm.CanChooseLandscape);

        vm.Layout = SharePosterLayout.Landscape;

        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
    }

    [Fact]
    public void Segmented_options_start_on_the_current_choice_and_drive_the_enum()
    {
        var vm = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);

        // 起值：每个分组恰好一项选中，且选中项与枚举一致
        Assert.Single(vm.LayoutOptions, o => o.IsSelected);
        Assert.Single(vm.ThemeOptions, o => o.IsSelected);
        Assert.Single(vm.MaskOptions, o => o.IsSelected);
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
    public void Single_day_window_shows_the_portrait_only_notice()
    {
        var single = new SharePosterDialogViewModel(Request(singleDay: true), Snapshot);
        var multi = new SharePosterDialogViewModel(Request(singleDay: false), Snapshot);

        Assert.True(single.ShowSingleDayNotice);
        Assert.False(multi.ShowSingleDayNotice);
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
