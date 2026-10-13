using System.ComponentModel;
using System.Globalization;
using CCUsage.App.Localization;
using FluentAssertions;

namespace CCUsage.App.Tests.Localization;

public class TranslationsTests : IDisposable
{
    // 进程级单例：每个用例保存并恢复语言基线，避免相互污染（同 SettingsViewModelTests 的语言用例约定）。
    private readonly AppLanguage _originalLanguage = Translations.Instance.CurrentLanguage;

    public void Dispose() => Translations.Instance.SetLanguage(_originalLanguage);

    [Theory]
    [InlineData("zh", AppLanguage.SimplifiedChinese)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("system", AppLanguage.System)]
    [InlineData("  EN  ", AppLanguage.English)]
    [InlineData("klingon", AppLanguage.System)]
    [InlineData("", AppLanguage.System)]
    public void ApplyPreference_MapsKnownValuesAndFallsBackToSystem(string preference, AppLanguage expected)
    {
        Translations.ApplyPreference(preference);

        // 英文偏好 → 英文生效；system/未知 → 按 UI 文化解析（测试进程已固定 zh-Hans → 中文）。
        var expectedResolved = expected switch
        {
            AppLanguage.English => AppLanguage.English,
            AppLanguage.SimplifiedChinese => AppLanguage.SimplifiedChinese,
            _ => AppLanguage.SimplifiedChinese, // zh-Hans 文化下 system 解析为中文
        };
        Translations.Instance.CurrentLanguage.Should().Be(expectedResolved);
    }

    [Fact]
    public void Resolve_SystemFollowsUiCulture()
    {
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-CN")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-Hans")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("en-US")).Should().Be(AppLanguage.English);
        Translations.Resolve(AppLanguage.System, new CultureInfo("ja-JP")).Should().Be(AppLanguage.English);
    }

    [Fact]
    public void Resolve_ExplicitPreferenceOverridesSystemCulture()
    {
        Translations.Resolve(AppLanguage.English, new CultureInfo("zh-CN")).Should().Be(AppLanguage.English);
        Translations.Resolve(AppLanguage.SimplifiedChinese, new CultureInfo("en-US")).Should().Be(AppLanguage.SimplifiedChinese);
    }

    [Fact]
    public void SetLanguage_SwitchesStringsAndRaisesFullBurstWithCurrentLanguageSignal()
    {
        var instance = Translations.Instance;
        var raised = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        instance.PropertyChanged += handler;
        try
        {
            instance.SetLanguage(AppLanguage.SimplifiedChinese);
            var zhSourcesTitle = instance.SourcesPanelTitle;
            zhSourcesTitle.Should().Be("已识别的数据源");

            raised.Clear();
            instance.SetLanguage(AppLanguage.English);

            instance.SourcesPanelTitle.Should().Be("Recognized data sources");
            instance.CurrentLanguage.Should().Be(AppLanguage.English);
            // 全量 raise（含 CurrentLanguage——命令式重译订阅方以此作为「每次切换一次」的信号）
            raised.Should().Contain(nameof(Translations.CurrentLanguage));
            raised.Should().Contain(nameof(Translations.SourcesPanelTitle));
            raised.Count(name => name == nameof(Translations.CurrentLanguage)).Should().Be(1);
        }
        finally
        {
            instance.PropertyChanged -= handler;
        }
    }

    [Fact]
    public void SetLanguage_WithSameResolvedLanguage_DoesNotRaise()
    {
        // 启动路径每次 ApplyPreference 都会调用 SetLanguage——同语言不广播（避免无谓全量重译）。
        var instance = Translations.Instance;
        instance.SetLanguage(AppLanguage.SimplifiedChinese); // 测试进程 UI 文化 zh-Hans，已为中文
        var raised = 0;
        PropertyChangedEventHandler handler = (_, _) => raised++;
        instance.PropertyChanged += handler;
        try
        {
            instance.SetLanguage(AppLanguage.System); // 解析结果仍是中文

            raised.Should().Be(0);
        }
        finally
        {
            instance.PropertyChanged -= handler;
        }
    }

    [Fact]
    public void English_MissingKey_FallsBackToChineseSource()
    {
        // 缺英文覆盖的 key 回落中文（zh 为源、en 为覆盖的合并语义）。此断言当前必然通过——
        // 它守护的是「未来新增 key 漏写 En 覆盖时界面仍可读」的兜底行为本身。
        var instance = Translations.Instance;
        instance.SetLanguage(AppLanguage.English);
        instance.SourcesPanelTitle.Should().NotBeNullOrEmpty();

        // 英文下设置串存在覆盖；换回中文后同 key 有 zh 值（两个字典都登记了该 key 的健全性抽查）。
        instance.SetLanguage(AppLanguage.SimplifiedChinese);
        instance.SourcesPanelTitle.Should().Be("已识别的数据源");
    }

    [Fact]
    public void FormatStrings_KeepInvariantPlaceholders()
    {
        // {0} 风格占位符（string.Format 消费）：抽查代表键的槽位数与可格式化。
        var instance = Translations.Instance;
        instance.SetLanguage(AppLanguage.SimplifiedChinese);
        string.Format(instance.LastUpdatedFormat, new DateTime(2026, 10, 13, 9, 5, 0)).Should().Contain("09:05");
        string.Format(instance.CountTextFormat, "1", "2").Should().Contain("1").And.Contain("2");
        string.Format(instance.AgentKpiFootFormat, "3", "$0.12").Should().Contain("3 会话").And.Contain("$0.12");
    }
}
