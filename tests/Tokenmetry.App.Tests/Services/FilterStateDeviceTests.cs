namespace Tokenmetry.App.Tests.Services;

using Tokenmetry.App.Services;

/// <summary>
/// 全局筛选设备维度（relay 计划 Task 11 / 设计 U5、A8、A12）：
/// 同步关 = 查询强制本机（GlobalFilterMapper 单点收口，不依赖 UI 状态）；
/// 同步开 = 默认全部设备（null），可切具体设备；Device 进 AffectsQueries。
/// 断言语言无关（不引用具体文案串）；经纯函数 ResolveDevice 驱动——FilterStateService 的
/// 属性 setter 依赖 UI 线程 marshal（无 UI 运行时的 xunit 进程里随进程状态不确定），
/// 状态对象路径由 headless 用例在真 UI 线程覆盖（Task 13）。
/// </summary>
public sealed class FilterStateDeviceTests
{
    [Fact]
    public void SyncOff_FilterForcesLocalDevice()
    {
        GlobalFilterMapper.ResolveDevice(isSyncEnabled: false, "devA", DeviceFilterOption.All)
            .Should().Be("devA", "同步关闭时查询钉在本机（A12）");
    }

    [Fact]
    public void SyncOn_AllDevice_FilterIsNull()
    {
        GlobalFilterMapper.ResolveDevice(isSyncEnabled: true, "devA", DeviceFilterOption.All)
            .Should().BeNull("同步开启默认合并视图 = 全部设备并集");
    }

    [Fact]
    public void SyncOn_SpecificDevice_PassesId()
    {
        GlobalFilterMapper.ResolveDevice(isSyncEnabled: true, "devA", new DeviceFilterOption("devB", "Dev B"))
            .Should().Be("devB");
    }

    [Fact]
    public void AffectsQueries_IncludesDeviceProperty()
    {
        FilterStateService.AffectsQueries(nameof(FilterStateService.Device)).Should().BeTrue();
    }
}
