namespace Tokenmetry.Infrastructure.Tests.Cindy;

using Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy 目录发现测试：区域 userData 目录可同机并存（cn / global / 历史 xdt-maker；dev 的 CindyDev
/// 有意不统计），独立 Host profile 与桌面同构；Pi 会话目录与 Codex 私有 home 都从同一份根清单派生。
/// 环境变量全部注入，不读真实用户目录。
/// </summary>
public class CindyLogLocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Discovers_existing_region_directories_and_host_profiles_but_not_dev()
    {
        var (appData, environment) = CreatePlatformAppData();
        foreach (var folder in new[] { "Cindy", "CindyGlobal", "xdt-maker" })
        {
            AddDirectory(appData, folder);
        }

        AddDirectory(appData, "CindyDev"); // 开发构建身份：普通用户不会接触，且常含测试数据
        AddDirectory(appData, "CindyHost", "cn");
        AddDirectory(appData, "CindyHost", "global");
        AddDirectory(appData, "UnrelatedProduct");

        var roots = new CindyLogLocator(environment).ResolveUserDataRoots(null);

        roots.Should().Equal(
            Path.Combine(appData, "Cindy"),
            Path.Combine(appData, "CindyGlobal"),
            Path.Combine(appData, "xdt-maker"),
            Path.Combine(appData, "CindyHost", "cn"),
            Path.Combine(appData, "CindyHost", "global"));
    }

    [Fact]
    public void Missing_region_directories_yield_no_roots()
    {
        var (appData, environment) = CreatePlatformAppData();
        AddDirectory(appData, "UnrelatedProduct");

        new CindyLogLocator(environment).ResolveUserDataRoots(null).Should().BeEmpty();
    }

    [Fact]
    public void Explicit_root_is_exclusive()
    {
        var (appData, environment) = CreatePlatformAppData();
        AddDirectory(appData, "Cindy");
        AddDirectory(appData, "CindyGlobal");

        var roots = new CindyLogLocator(environment).ResolveUserDataRoots(@"E:\pinned-cindy");

        roots.Should().Equal(
            [@"E:\pinned-cindy"],
            "显式配置是调用方钉定，不扩展区域目录");
    }

    [Fact]
    public void Pi_session_directories_are_collected_per_existing_root()
    {
        var (appData, environment) = CreatePlatformAppData();
        AddDirectory(appData, "Cindy", "pi-agent-home", "sessions");
        AddDirectory(appData, "CindyGlobal", "pi-agent-home", "sessions");
        AddDirectory(appData, "xdt-maker", "pi-agent-home"); // 有 home 没有 sessions：不算扫描根
        AddDirectory(appData, "CindyDev", "pi-agent-home", "sessions"); // dev 身份：有意不统计
        AddDirectory(appData, "CindyHost", "global", "pi-agent-home", "sessions");

        var directories = new CindyLogLocator(environment).ResolveSessionDirectories(null);

        directories.Should().Equal(
            Path.Combine(appData, "Cindy", "pi-agent-home", "sessions"),
            Path.Combine(appData, "CindyGlobal", "pi-agent-home", "sessions"),
            Path.Combine(appData, "CindyHost", "global", "pi-agent-home", "sessions"));
    }

    [Fact]
    public void Codex_homes_include_default_and_every_subscription_account()
    {
        var userData = CreateTempDir();
        AddDirectory(userData, "codex-home", "sessions");
        AddDirectory(userData, "codex-accounts", "owner-a", "openai");
        AddDirectory(userData, "codex-accounts", "owner-a", "openai-work");
        AddDirectory(userData, "codex-accounts", "owner-b", "openai");
        AddDirectory(userData, "codex-accounts", "owner-b"); // 没有 provider 子目录：不产生 home
        AddDirectory(userData, "pi-agent-home", "sessions"); // 非 Codex 布局：不计入

        var homes = new CindyLogLocator(_ => null).ResolveCodexHomes(userData);

        homes.Should().Equal(
            Path.Combine(userData, "codex-home"),
            Path.Combine(userData, "codex-accounts", "owner-a", "openai"),
            Path.Combine(userData, "codex-accounts", "owner-a", "openai-work"),
            Path.Combine(userData, "codex-accounts", "owner-b", "openai"));
    }

    [Fact]
    public void Missing_codex_homes_yield_empty()
    {
        var userData = CreateTempDir();

        new CindyLogLocator(_ => null).ResolveCodexHomes(userData).Should().BeEmpty();
    }

    [Fact]
    public void Primary_user_data_root_keeps_legacy_semantics()
    {
        var locator = new CindyLogLocator(name => null);

        locator.ResolveUserDataRoot(@"E:\cindy-userdata").Should().Be(@"E:\cindy-userdata");
        locator.ResolveUserDataRoot("   ").Should().BeNull("桩环境解析不出平台 home");
    }

    // ---- helpers ----

    /// <summary>
    /// 按当前平台生成 appData 根并返回对应的环境访问器（Windows=APPDATA、macOS=HOME、
    /// Linux=XDG_CONFIG_HOME）。三平台 CI 上都能跑，不依赖真实用户目录。
    /// </summary>
    private (string AppData, Func<string, string?> Environment) CreatePlatformAppData()
    {
        var temp = CreateTempDir();
        if (OperatingSystem.IsWindows())
        {
            var appData = Path.Combine(temp, "Roaming");
            Directory.CreateDirectory(appData);
            return (appData, key => key == "APPDATA" ? appData : null);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Path.Combine(temp, "home");
            var appData = Path.Combine(home, "Library", "Application Support");
            Directory.CreateDirectory(appData);
            return (appData, key => key == "HOME" ? home : null);
        }

        var xdgConfigHome = Path.Combine(temp, "xdg");
        Directory.CreateDirectory(xdgConfigHome);
        return (xdgConfigHome, key => key == "XDG_CONFIG_HOME" ? xdgConfigHome : null);
    }

    private static string AddDirectory(string root, params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = root;
        segments.CopyTo(parts, 1);
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenmetry-cindy-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言
            }
        }
    }
}
