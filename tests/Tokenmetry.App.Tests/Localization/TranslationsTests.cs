using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using FluentAssertions;

namespace Tokenmetry.App.Tests.Localization;

public class TranslationsTests : IDisposable
{
    // 进程级单例：每个用例保存并恢复语言基线，避免相互污染（同 SettingsViewModelTests 的语言用例约定）。
    private readonly AppLanguage _originalLanguage = Translations.Instance.CurrentLanguage;

    public void Dispose() => Translations.Instance.SetLanguage(_originalLanguage);

    [Theory]
    [InlineData("zh", AppLanguage.SimplifiedChinese)]
    [InlineData("zh-hant", AppLanguage.TraditionalChinese)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("ja", AppLanguage.Japanese)]
    [InlineData("ko", AppLanguage.Korean)]
    [InlineData("pt-br", AppLanguage.BrazilianPortuguese)]
    [InlineData("ru", AppLanguage.Russian)]
    [InlineData("es", AppLanguage.Spanish)]
    [InlineData("de", AppLanguage.German)]
    [InlineData("fr", AppLanguage.French)]
    [InlineData("system", AppLanguage.System)]
    [InlineData("  EN  ", AppLanguage.English)]
    [InlineData("  PT-BR  ", AppLanguage.BrazilianPortuguese)]
    [InlineData("klingon", AppLanguage.System)]
    [InlineData("", AppLanguage.System)]
    public void ApplyPreference_MapsKnownValuesAndFallsBackToSystem(string preference, AppLanguage expected)
    {
        Translations.ApplyPreference(preference);

        // 显式语言 → 原样生效；system/未知 → 按 UI 文化解析（测试进程已固定 zh-Hans → 简体中文）。
        var expectedResolved = expected == AppLanguage.System
            ? AppLanguage.SimplifiedChinese
            : expected;
        Translations.Instance.CurrentLanguage.Should().Be(expectedResolved);
    }

    [Fact]
    public void Resolve_SystemFollowsUiCulture()
    {
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-CN")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-Hans")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-SG")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-TW")).Should().Be(AppLanguage.TraditionalChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-HK")).Should().Be(AppLanguage.TraditionalChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-Hant")).Should().Be(AppLanguage.TraditionalChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("zh-Hant-HK")).Should().Be(AppLanguage.TraditionalChinese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("en-US")).Should().Be(AppLanguage.English);
        Translations.Resolve(AppLanguage.System, new CultureInfo("ja-JP")).Should().Be(AppLanguage.Japanese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("ko-KR")).Should().Be(AppLanguage.Korean);
        Translations.Resolve(AppLanguage.System, new CultureInfo("pt-BR")).Should().Be(AppLanguage.BrazilianPortuguese);
        // pt 系只出 pt-BR 一个变体：pt-PT 等其余葡语地区也落 pt-BR（单 locale 纪律，不做 pt-PT）
        Translations.Resolve(AppLanguage.System, new CultureInfo("pt-PT")).Should().Be(AppLanguage.BrazilianPortuguese);
        Translations.Resolve(AppLanguage.System, new CultureInfo("ru-RU")).Should().Be(AppLanguage.Russian);
        Translations.Resolve(AppLanguage.System, new CultureInfo("es-ES")).Should().Be(AppLanguage.Spanish);
        Translations.Resolve(AppLanguage.System, new CultureInfo("es-MX")).Should().Be(AppLanguage.Spanish);
        Translations.Resolve(AppLanguage.System, new CultureInfo("de-DE")).Should().Be(AppLanguage.German);
        Translations.Resolve(AppLanguage.System, new CultureInfo("fr-FR")).Should().Be(AppLanguage.French);
        // 未支持的语种如实落 English（不落简体——英语是事实上的开发者通用回退）
        Translations.Resolve(AppLanguage.System, new CultureInfo("it-IT")).Should().Be(AppLanguage.English);
    }

    [Fact]
    public void Resolve_ExplicitPreferenceOverridesSystemCulture()
    {
        Translations.Resolve(AppLanguage.English, new CultureInfo("zh-CN")).Should().Be(AppLanguage.English);
        Translations.Resolve(AppLanguage.SimplifiedChinese, new CultureInfo("en-US")).Should().Be(AppLanguage.SimplifiedChinese);
        Translations.Resolve(AppLanguage.TraditionalChinese, new CultureInfo("en-US")).Should().Be(AppLanguage.TraditionalChinese);
        Translations.Resolve(AppLanguage.Japanese, new CultureInfo("pt-BR")).Should().Be(AppLanguage.Japanese);
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
    public void English_CoversEveryChineseSourceKey()
    {
        // 回归守护（Q-010）：En 缺项按合并语义回落中文，但缺项即英语界面漏出中文——
        // I-035 模型目录 29 键曾整体漏覆盖，英文下整段显示中文。英文字典必须覆盖
        // 中文字典的全部 key：新增 zh key 时同步补 en 覆盖（回落机制本身仍由
        // English_MissingKey_FallsBackToChineseSource 守护，本测试让它不再被触发）。
        var zh = SourceDictionary("Zh");
        var en = SourceDictionary("En");

        en.Keys.Should().BeEquivalentTo(zh.Keys,
            because: "英文缺覆盖时回落中文，英语界面会漏出中文文案");
    }

    [Theory]
    [InlineData("ZhHant")]
    [InlineData("Ja")]
    [InlineData("Ko")]
    [InlineData("PtBr")]
    [InlineData("Ru")]
    [InlineData("Es")]
    [InlineData("De")]
    [InlineData("Fr")]
    public void NewLanguageDictionaries_CoverEveryChineseSourceKey(string fieldName)
    {
        // 与 English_CoversEveryChineseSourceKey 同纪律（D-024）：回落链是新语种的安全网而非
        // 欠账借口——首发语种必须全量覆盖，缺项即外语界面漏出英文/中文文案。
        var zh = SourceDictionary("Zh");
        var lang = SourceDictionary(fieldName);

        lang.Keys.Should().BeEquivalentTo(zh.Keys,
            because: "新语种缺覆盖时经回落链会露出英文/中文文案");
    }

    [Theory]
    [InlineData("En")]
    [InlineData("ZhHant")]
    [InlineData("Ja")]
    [InlineData("Ko")]
    [InlineData("PtBr")]
    [InlineData("Ru")]
    [InlineData("Es")]
    [InlineData("De")]
    [InlineData("Fr")]
    public void LanguageDictionaries_KeepInvariantFormatPlaceholders(string fieldName)
    {
        // {0} 风格占位符由 string.Format 消费：任何语言的译文槽位集必须与中文源一致——
        // 多语言最大的机械性风险就是译文丢占位符 / 挪槽位导致 FormatException 或错位。
        var zh = SourceDictionary("Zh");
        var lang = SourceDictionary(fieldName);

        foreach (var (key, zhValue) in zh)
        {
            lang.TryGetValue(key, out var translated).Should().BeTrue($"key {key} 应被 {fieldName} 全量覆盖");
            PlaceholderTokens(zhValue).Should().BeEquivalentTo(PlaceholderTokens(translated!),
                because: $"key {key} 的 {fieldName} 译文必须保留与中文源一致的格式占位符");
        }
    }

    [Fact]
    public void FallbackChain_SecondaryLanguages_FallBackToEnglishThenChineseSimplified()
    {
        // D-024 回退语义：新语种缺项 → 英文 → 简体（链序即覆盖优先序，末级最兜底）。
        // 链本身按设计不被触发（全量覆盖由 *_CoversEveryChineseSourceKey 固化），
        // 本测试固化的是「兜底时先露英文、不露中文」的顺序决策。
        var ja = Translations.FallbackChain(AppLanguage.Japanese);
        ja.Should().HaveCount(3);
        ja[0].Should().BeSameAs(SourceDictionary("Ja"));
        ja[1].Should().BeSameAs(SourceDictionary("En"));
        ja[2].Should().BeSameAs(SourceDictionary("Zh"));

        Translations.FallbackChain(AppLanguage.Korean)[1].Should().BeSameAs(SourceDictionary("En"));
        Translations.FallbackChain(AppLanguage.BrazilianPortuguese)[1].Should().BeSameAs(SourceDictionary("En"));
        Translations.FallbackChain(AppLanguage.Russian)[1].Should().BeSameAs(SourceDictionary("En"));
        Translations.FallbackChain(AppLanguage.Spanish).Should().HaveCount(3);
        Translations.FallbackChain(AppLanguage.Spanish)[0].Should().BeSameAs(SourceDictionary("Es"));
        Translations.FallbackChain(AppLanguage.Spanish)[1].Should().BeSameAs(SourceDictionary("En"));
        Translations.FallbackChain(AppLanguage.German).Should().HaveCount(3);
        Translations.FallbackChain(AppLanguage.German)[0].Should().BeSameAs(SourceDictionary("De"));
        Translations.FallbackChain(AppLanguage.German)[1].Should().BeSameAs(SourceDictionary("En"));
        Translations.FallbackChain(AppLanguage.French).Should().HaveCount(3);
        Translations.FallbackChain(AppLanguage.French)[0].Should().BeSameAs(SourceDictionary("Fr"));
        Translations.FallbackChain(AppLanguage.French)[1].Should().BeSameAs(SourceDictionary("En"));
    }

    [Fact]
    public void FallbackChain_TraditionalChinese_FallsBackToSimplifiedChineseNotEnglish()
    {
        // 同语言变体缺项回落简体（而非英文）：繁体界面漏出简体比漏出英文更可读。
        var chain = Translations.FallbackChain(AppLanguage.TraditionalChinese);
        chain.Should().HaveCount(2);
        chain[0].Should().BeSameAs(SourceDictionary("ZhHant"));
        chain[1].Should().BeSameAs(SourceDictionary("Zh"));
    }

    [Fact]
    public void FallbackChain_EnglishAndSimplifiedChinese_Unchanged()
    {
        // 原有两级的既有语义不随多语言扩展变化（en 缺项回落中文；zh 即源）。
        Translations.FallbackChain(AppLanguage.SimplifiedChinese).Single().Should().BeSameAs(SourceDictionary("Zh"));

        var en = Translations.FallbackChain(AppLanguage.English);
        en.Should().HaveCount(2);
        en[0].Should().BeSameAs(SourceDictionary("En"));
        en[1].Should().BeSameAs(SourceDictionary("Zh"));
    }

    private static Dictionary<string, string> SourceDictionary(string fieldName) =>
        (Dictionary<string, string>)typeof(Translations)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IReadOnlyList<string> PlaceholderTokens(string value) =>
        Regex.Matches(value, @"\{[^{}]+\}").Select(m => m.Value).ToList();

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

    [Fact]
    public void FormatCompactTokens_UsesWanUnitsForSimplifiedChinese()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        TrendChart.FormatCompactTokens(50_000).Should().Contain("万").And.NotContain("萬");
    }

    [Fact]
    public void FormatCompactTokens_UsesTraditionalUnitsForTraditionalChinese()
    {
        // zh-Hant 走万进制但单位用繁体字形（萬/億）——图表数值混入简体字形即漏译。
        Translations.Instance.SetLanguage(AppLanguage.TraditionalChinese);
        TrendChart.FormatCompactTokens(50_000).Should().Contain("萬").And.NotContain("万");
    }

    [Fact]
    public void FormatCompactTokens_UsesKiloUnitsForNonChineseLanguages()
    {
        foreach (var language in new[]
                 {
                     AppLanguage.English, AppLanguage.Japanese, AppLanguage.Korean,
                     AppLanguage.BrazilianPortuguese, AppLanguage.Russian,
                     AppLanguage.Spanish, AppLanguage.German, AppLanguage.French,
                 })
        {
            Translations.Instance.SetLanguage(language);
            TrendChart.FormatCompactTokens(50_000).Should().NotContainAny("万", "萬");
        }
    }
}
