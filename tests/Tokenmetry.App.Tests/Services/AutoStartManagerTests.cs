using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// AutoStartManager 纯构造函数的内容形状测试（S-006）：SetEnabled 操作真实 OS 状态
/// （注册表 / 全局 LaunchAgents 目录），测试宿主绝不调用——注册内容用纯构造函数断言
/// （InternalsVisibleTo，与 App 其他 internal 供测试对账的入口同纪律）。
/// </summary>
public class AutoStartManagerTests
{
    [Fact]
    public void BuildWindowsRunValue_QuotesPathAndAppendsLaunchArg()
    {
        var value = AutoStartManager.BuildWindowsRunValue(@"C:\Program Files\Tokenmetry\Tokenmetry.App.exe");

        value.Should().Be("\"C:\\Program Files\\Tokenmetry\\Tokenmetry.App.exe\" --autostart");
    }

    [Fact]
    public void BuildLaunchAgentPlist_ContainsLabelArgumentsAndRunAtLoad()
    {
        var exePath = "/Applications/Tokenmetry.app/Contents/MacOS/Tokenmetry.App";

        var plist = AutoStartManager.BuildLaunchAgentPlist(exePath);

        plist.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"); // launchd 期望的 plist 头
        plist.Should().Contain("http://www.apple.com/DTDs/PropertyList-1.0.dtd"); // launchd plist 声明
        plist.Should().Contain("<key>Label</key>");
        plist.Should().Contain("<string>com.tokenmetry.app</string>");
        plist.Should().Contain(exePath);
        plist.Should().Contain("<string>--autostart</string>"); // 与 App 启动侧识别的参数同源
        plist.Should().Contain("<key>RunAtLoad</key>");
        plist.Should().Contain("<true />");
    }

    [Fact]
    public void BuildLaunchAgentPlist_XmlEscapesExecutablePath()
    {
        var plist = AutoStartManager.BuildLaunchAgentPlist("/Users/tom & jerry/Tokenmetry.App");

        plist.Should().Contain("tom &amp; jerry");
        plist.Should().NotContain("tom & jerry");
    }

    [Fact]
    public void BuildDesktopEntry_QuotesExecPathAndAppendsLaunchArg()
    {
        var entry = AutoStartManager.BuildDesktopEntry("/opt/tokenmetry/Tokenmetry.App");

        entry.Should().StartWith("[Desktop Entry]");
        entry.Should().Contain("Exec=\"/opt/tokenmetry/Tokenmetry.App\" --autostart");
        entry.Should().Contain("Type=Application");
    }

    [Fact]
    public void LaunchArg_IsStableContractBetweenRegistrationAndStartup()
    {
        // 注册侧写进 Run 键 / plist / desktop 的参数与 App 启动侧识别的参数必须是同一常量
        // （两侧分处 AutoStartManager 与 App 启动序列，靠本常量耦合，改签名即测试失败）。
        AutoStartManager.LaunchArg.Should().Be("--autostart");
    }
}
