using System.Globalization;
using System.Runtime.CompilerServices;

namespace TokenPulse.App.Headless.Tests;

/// <summary>
/// 测试进程 UI 文化基线：固定简体中文。AppSettings.Language 缺省 "system"，App 启动序列
/// （headless 每个测试用例重建 App）经 Translations.ApplyPreference 按宿主 UI 文化解析——
/// 开发机/CI 文化为英文时会把 system 解析成 English，导致全部文案断言（zh 源串）失败。
/// 与 stupid-dict 的测试约定一致：语言基线显式固定，不随宿主机器漂移；
/// 语言切换类用例自行 SetLanguage 并在 finally 恢复。
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
