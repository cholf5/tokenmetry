using System.IO;
using Avalonia.Headless.XUnit;
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;
using Xunit;

namespace CCUsage.App.Headless.Tests.Shell;

/// <summary>
/// A-2 隔离守卫：headless 宿主启动真实 App（AppServices.Initialize）时，AppData 根必须被
/// SemiTestAppBuilder 重定向到测试专属临时目录——测试进程绝不读写真实用户数据
/// （修复前测试无条件走 CreateDefault()，写本机 LocalApplicationData 下的 ccusage.db）。
/// </summary>
public class TestIsolationTests
{
    [AvaloniaFact]
    public void Headless_host_appdata_is_redirected_to_test_temp_root()
    {
        var paths = AppServices.Get<AppPaths>();

        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "ccusage-headless-tests"),
            paths.AppDataDirectory);
    }
}
