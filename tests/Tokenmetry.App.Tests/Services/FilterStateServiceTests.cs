using System;
using System.Collections.Generic;
using System.Linq;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using FluentAssertions;
using Xunit;

namespace Tokenmetry.App.Tests.Services;

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

    [Fact]
    public void Options_OrderTodayFirstAndCustomLast()
    {
        TimeRangeOption.Options.Select(o => o.Value).Should().Equal(
            TimeRange.Today,
            TimeRange.Last7Days,
            TimeRange.Last30Days,
            TimeRange.Last90Days,
            TimeRange.All,
            TimeRange.Custom);
    }

    [Fact]
    public void Default_IsStillLast7Days()
    {
        // 顶部插入「今日」档后默认档不得漂移（对齐原型 renderTrend(7) 初始窗口）
        TimeRangeOption.Default.Value.Should().Be(TimeRange.Last7Days);
    }

    [Fact]
    public void Labels_LocalizedForTodayAndCustom()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        TimeRangeOption.Options.Single(o => o.Value == TimeRange.Today).Label.Should().Be("今日");
        TimeRangeOption.Options.Single(o => o.Value == TimeRange.Custom).Label.Should().Be("自定义");

        Translations.Instance.SetLanguage(AppLanguage.English);
        TimeRangeOption.Options.Single(o => o.Value == TimeRange.Today).Label.Should().Be("Today");
        TimeRangeOption.Options.Single(o => o.Value == TimeRange.Custom).Label.Should().Be("Custom");
    }

    // 依赖 FilterStateService 属性 setter（SetOnUi 经 Dispatcher）的用例在 Tokenmetry.App.Headless.Tests：
    // 纯 xunit 进程里 UI 线程被并行测试类抢先钉住时写入会被 Post 悬空，须在 [AvaloniaFact] 下执行。

    [Fact]
    public void AffectsQueries_CoversAllQueryDimensionsButNotDerivedStrings()
    {
        FilterStateService.AffectsQueries(null).Should().BeTrue();
        FilterStateService.AffectsQueries(string.Empty).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.TimeRange)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.Agent)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.CustomFromDate)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.CustomToDate)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.Display)).Should().BeFalse();
        FilterStateService.AffectsQueries(nameof(FilterStateService.AgentOptions)).Should().BeFalse();
    }
}

