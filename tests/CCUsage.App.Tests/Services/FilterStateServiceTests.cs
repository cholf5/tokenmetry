using System.Collections.Generic;
using CCUsage.App.Localization;
using CCUsage.App.Services;
using FluentAssertions;
using Xunit;

namespace CCUsage.App.Tests.Services;

public class FilterStateServiceTests : IDisposable
{
    // Translations 进程级单例：用例保存并恢复语言基线，避免相互污染（同 TranslationsTests 约定）。
    private readonly AppLanguage _originalLanguage = Translations.Instance.CurrentLanguage;

    public void Dispose() => Translations.Instance.SetLanguage(_originalLanguage);

    [Fact]
    public void LanguageSwitch_BroadcastsFullBurstForPageRerender()
    {
        // 语言切换广播 INPC 空串（全属性变更）：各页 VM 的 IsNullOrEmpty 分支按「整页重查重渲染」
        // 消费——Token 紧凑数值等单位随语言即时切换，不必等下一次筛选变更。
        var filters = new FilterStateService();
        var raised = new List<string?>();
        filters.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Translations.Instance.SetLanguage(
            _originalLanguage == AppLanguage.English
                ? AppLanguage.SimplifiedChinese
                : AppLanguage.English);

        raised.Should().Contain(string.Empty);
    }
}
