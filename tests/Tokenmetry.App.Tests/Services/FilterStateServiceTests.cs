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
    public void InitialOptions_BuiltInsThenCustomSentinel()
    {
        // D-020：内置档按 日历档 → 滚动档 → 全部 排布，自定义哨兵殿后（用户预设装载后插在全部与自定义之间）
        TimeRangeOption.InitialOptions.Select(o => o.Id).Should().Equal(
            "Today",
            "Yesterday",
            "ThisMonth",
            "LastMonth",
            "Last7Days",
            "Last30Days",
            "Last90Days",
            "All",
            "Custom");
    }

    [Fact]
    public void BuiltIns_carry_the_unified_relative_spec()
    {
        // 内置档 = 预定义 Spec 实例（D-020 统一模型）：新日历档与既有滚动档同形状
        TimeRangeOption.Today.Spec.Should().Be(RelativeRangeSpec.SingleDay(0));
        TimeRangeOption.Yesterday.Spec.Should().Be(RelativeRangeSpec.SingleDay(-1));
        TimeRangeOption.ThisMonth.Spec.Should().Be(RelativeRangeSpec.Month(0));
        TimeRangeOption.LastMonth.Spec.Should().Be(RelativeRangeSpec.Month(-1));
        TimeRangeOption.Last7Days.Spec.Should().Be(RelativeRangeSpec.Rolling(7));
        TimeRangeOption.Last30Days.Spec.Should().Be(RelativeRangeSpec.Rolling(30));
        TimeRangeOption.Last90Days.Spec.Should().Be(RelativeRangeSpec.Rolling(90));
        TimeRangeOption.All.Spec.Should().BeNull();
        TimeRangeOption.Custom.Spec.Should().BeNull();
    }

    [Fact]
    public void Default_IsStillLast7Days()
    {
        // 顶部插入新档后默认档不得漂移（对齐原型 renderTrend(7) 初始窗口）
        TimeRangeOption.Default.Id.Should().Be("Last7Days");
    }

    [Fact]
    public void Labels_LocalizedForSentinelsAndPresets()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        TimeRangeOption.Today.Label.Should().Be("今日");
        TimeRangeOption.Custom.Label.Should().Be("自定义");
        TimeRangeOption.ThisMonth.Label.Should().Be("本月");

        Translations.Instance.SetLanguage(AppLanguage.English);
        TimeRangeOption.Today.Label.Should().Be("Today");
        TimeRangeOption.Custom.Label.Should().Be("Custom");
        TimeRangeOption.ThisMonth.Label.Should().Be("This month");
    }

    [Fact]
    public void Equality_is_by_stable_id_not_reference_or_label()
    {
        // 相等性契约：SelectedItem 绑定比较稳定，语言切换不改变相等性；不同实例同 Id 视为同一档
        var preset = new TimeRangeOption("preset:abc", RelativeRangeSpec.Week(0));
        var rebuilt = new TimeRangeOption("preset:abc", RelativeRangeSpec.Week(0));

        preset.Should().Be(rebuilt);
        preset.GetHashCode().Should().Be(rebuilt.GetHashCode());
        TimeRangeOption.Custom.Should().NotBe(TimeRangeOption.All);
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
        FilterStateService.AffectsQueries(nameof(FilterStateService.Model)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.Project)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.CustomFromDate)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.CustomToDate)).Should().BeTrue();
        FilterStateService.AffectsQueries(nameof(FilterStateService.Display)).Should().BeFalse();
        FilterStateService.AffectsQueries(nameof(FilterStateService.AgentOptions)).Should().BeFalse();
    }

    // Model/Project 默认哨兵断言不触 setter，留在本项目；依赖 setter / UpdateXxxOptions
    // （SetOnUi 经 Dispatcher）的用例在 Tokenmetry.App.Headless.Tests（[AvaloniaFact]）。

    [Fact]
    public void Model_project_defaults_are_all_sentinels()
    {
        var filters = new FilterStateService();

        filters.Model.Should().BeSameAs(ModelFilterOption.All);
        filters.Project.Should().BeSameAs(ProjectFilterOption.All);
        filters.ModelOptions.Should().ContainSingle().Which.Should().BeSameAs(ModelFilterOption.All);
        filters.ProjectOptions.Should().ContainSingle().Which.Should().BeSameAs(ProjectFilterOption.All);
    }
}

