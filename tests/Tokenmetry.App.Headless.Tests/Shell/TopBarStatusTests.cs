using Avalonia.Headless.XUnit;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// A-12 顶栏状态灯回归锁：数据源状态由采集线每轮回写（BackgroundRefreshWiring →
/// NoteDataSourceStatus），不再硬编码"本地数据源就绪"绿灯——error 红 / no data 橙 / 其余绿。
/// </summary>
public class TopBarStatusTests
{
    [AvaloniaFact]
    public void Initial_state_is_honest_startup_not_faux_ready()
    {
        var vm = AppServices.Get<MainWindowViewModel>();

        Assert.Equal("启动中…", vm.DataStatusText);
        Assert.False(vm.IsDataSourceError);
        Assert.False(vm.IsDataSourceWarned);
    }

    [AvaloniaFact]
    public void Error_status_turns_dot_red_with_readble_text()
    {
        var vm = AppServices.Get<MainWindowViewModel>();

        vm.NoteDataSourceStatus("error");

        Assert.Equal("error", vm.DataStatusLevel);
        Assert.True(vm.IsDataSourceError);
        Assert.False(vm.IsDataSourceWarned);
        Assert.Equal("数据源错误（本轮刷新失败）", vm.DataStatusText);
    }

    [AvaloniaFact]
    public void NoData_status_turns_dot_warn()
    {
        var vm = AppServices.Get<MainWindowViewModel>();

        vm.NoteDataSourceStatus("no data");

        Assert.Equal("warn", vm.DataStatusLevel);
        Assert.True(vm.IsDataSourceWarned);
        Assert.False(vm.IsDataSourceError);
        Assert.Equal("未检出本地数据", vm.DataStatusText);
    }

    [AvaloniaFact]
    public void Ok_status_restores_ready_text()
    {
        var vm = AppServices.Get<MainWindowViewModel>();

        vm.NoteDataSourceStatus("ok");

        Assert.Equal("ok", vm.DataStatusLevel);
        Assert.False(vm.IsDataSourceError);
        Assert.False(vm.IsDataSourceWarned);
        Assert.Equal("本地数据源就绪", vm.DataStatusText);
    }
}
