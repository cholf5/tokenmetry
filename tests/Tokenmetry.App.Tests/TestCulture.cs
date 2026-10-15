using System.Globalization;
using System.Runtime.CompilerServices;

namespace Tokenmetry.App.Tests;

/// <summary>
/// 测试进程 UI 文化基线：固定简体中文。Translations 单例的 system 偏好按宿主 UI 文化解析
/// （开发机/CI 可能为英文），文案对账断言以 zh 源串为基线——显式固定，不随宿主机器漂移；
/// 语言切换类用例自行 SetLanguage 并在 finally 恢复（同 stupid-dict 测试约定）。
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void PinChineseUiCulture()
    {
        // CurrentUICulture 是线程级属性——xUnit 线程池线程不继承主线程的设置，
        // 必须用 DefaultThreadCurrentUICulture 覆盖全部未显式设文化的线程。
        var zh = new CultureInfo("zh-Hans");
        CultureInfo.DefaultThreadCurrentUICulture = zh;
        CultureInfo.CurrentUICulture = zh;
    }
}
