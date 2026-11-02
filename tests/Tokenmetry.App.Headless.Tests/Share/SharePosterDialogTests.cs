using System;
using System.Linq;
using Avalonia.Controls;
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

        // ComboBox 的选中项是选项对象（不是枚举值）：标签来自 Translations，且每个值唯一
        Assert.Equal(vm.SelectedTheme, SharePosterOptions.ForTheme(vm.Theme));
        Assert.All(vm.LayoutOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
        Assert.Equal(
            vm.LayoutOptions.Count,
            vm.LayoutOptions.Select(o => o.Label).Distinct().Count());

        vm.SelectedLayout = SharePosterOptions.ForLayout(SharePosterLayout.Landscape);
        Assert.Equal(SharePosterLayout.Landscape, vm.Layout);
        Assert.Equal(SharePosterLayout.Landscape, vm.CurrentRequest.Layout);
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
