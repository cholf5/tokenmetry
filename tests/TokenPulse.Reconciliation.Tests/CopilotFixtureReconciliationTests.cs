using TokenPulse.Core.Models;
using TokenPulse.Reconciliation.Tests.CCUsageCli;
using TokenPulse.Reconciliation.Tests.Comparison;
using TokenPulse.Reconciliation.Tests.Support;

namespace TokenPulse.Reconciliation.Tests;

/// <summary>
/// TC-002（Copilot 侧，I-008 主线 + I-023 otel 合并与积分对账）：仓库内 Copilot CLI fixture 上，
/// 自研链路（CopilotSessionAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 copilot daily --json --offline，COPILOT_HOME 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。copilot daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：shutdown 累计快照差分（resume 跨日不重复出账、总量守恒）、同 id 重复
/// shutdown 去重、uncached input 拆分（inputTokens 含缓存读写）、全零模型条目跳过、-1m 内部
/// 后缀剥离归并（gpt-5.1-1m → gpt-5.1）、嵌套会话目录不发现、坏 JSON 行（I-008 主线）；
/// otel 四形态互斥去重（chat span 双导出计一次、低优先级记录抑制、同 turn 共享 response id
/// 保留）、otel × shutdown 取优合并（进程崩溃接管、最新 shutdown 后保留/前抑制）、credit gap
/// （shutdown 后 checkpoint 持续未解释 → cost-only unknown、checkpoint-only 会话全额、无
/// checkpoint / 缺总额 / 有 otel 行豁免）（I-023）。
/// </summary>
public class CopilotFixtureReconciliationTests
{
    private static readonly string[] AllDays = ["2026-09-14", "2026-09-15", "2026-09-16", "2026-09-17", "2026-09-18"];

    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadCopilot(FixturePaths.CopilotGoldenPath);
        var ours = await UsageChainAggregator.AggregateCopilotAsync(FixturePaths.CopilotRoot);

        // 链路健康检查：坏 JSON 行（含 shutdown 标记，过预过滤后解析失败）计 malformed 不中断；
        // session.started 行按 skipped 口径计数（带总额的 checkpoint 产出积分快照、不产出记录也不计跳过）。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "fixture 含 1 行坏 JSON（仓库口径计 malformed）");
        ours.Diagnostics.MalformedLines.Should().Be(1);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        // 记录数：session-state 区间 10（alpha 3 + beta 2 + retained/credit/exempt/incomplete/resumegap 各 1）
        // + otel 行 13（merge 6 + retained 1 + dedupe 5 + otelgap 1）+ credit gap 4（credit/cponly/resumegap×2）。
        ours.RecordCount.Should().Be(27);
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型（含 -1m 剥离后的归并名）都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_resume_interval_differential_and_totals_conservation()
    {
        // session-alpha：resume-1（09-14 累计快照）与 resume-2（09-15 累计快照，含全部历史）。
        // 差分后 09-14 出账首条、09-15 只出增量；同 id 重复 shutdown 去重，不出双份。
        var ours = await UsageChainAggregator.AggregateCopilotAsync(FixturePaths.CopilotRoot);

        ours.ModelEntries.Select(entry => entry.Model).Distinct().Should().BeEquivalentTo(
            new[] { "claude-sonnet-4", "gpt-5.1", "gpt-5-mini", "unknown" },
            "credit gap 以 cost-only unknown 条目落统一记录（I-023）");
        // -1m 后缀剥离：CLI 与自研都把 gpt-5.1-1m 归并进 gpt-5.1，模型清单里没有 gpt-5.1-1m。

        var day14 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day14.InputTokens.Should().Be(10000, "16000 input − 4000 cacheRead − 2000 cacheWrite（uncached 拆分）");
        day14.OutputTokens.Should().Be(1000);
        day14.CacheCreationTokens.Should().Be(2000);
        day14.CacheReadTokens.Should().Be(4000);

        var day15 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-15");
        // 差分：claude-sonnet-4 uncached 20500−10000=10500、out 1200、cw 500、cr 3000；
        // 加 gpt-5.1（5000/800）、gpt-5-mini（1200/300）、gpt-5.1-1m 归并条目（1500/100/cr500）。
        day15.InputTokens.Should().Be(18200);
        day15.OutputTokens.Should().Be(2400);
        day15.CacheCreationTokens.Should().Be(500);
        day15.CacheReadTokens.Should().Be(3500);

        // 总量守恒：两次快照的 uncached 累计之和 == 各区间之和（resume 不重复出账）。
        var grand = ours.TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate);
        grand.InputTokens.Should().Be(31614);
        grand.OutputTokens.Should().Be(3750);
        grand.CacheCreationTokens.Should().Be(2500);
        grand.CacheReadTokens.Should().Be(7500);
        grand.TotalTokens.Should().Be(45364);
    }

    [Fact]
    public async Task Fixture_exercises_otel_merge_and_credit_gap_scenarios()
    {
        // I-023 场景逐项断言（语义见类头；golden 对账在上一测试按日粒度完成，此处钉住场景级数字）。
        var ours = await UsageChainAggregator.AggregateCopilotAsync(FixturePaths.CopilotRoot);
        var records = ours.Records.ToList();

        // 场景 A（session-merge，进程崩溃接管）：otel 调用 6 > shutdown 请求数差分 3 →
        // otel 从第一个有行的区间接管——shutdown 区间全抑制、otel 行全保留（含 09-17T11:00 无 shutdown 后调用）。
        var merge = records.Where(record => record.SessionId == "session-merge").ToList();
        merge.Should().HaveCount(6, "otel 接管：6 行逐调用、区间 0 条");
        merge.Should().OnlyContain(record => record.InputTokens == 1 && record.OutputTokens == 1);
        merge.Select(record => record.Timestamp).Should().BeEquivalentTo(new[]
        {
            new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 16, 9, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 11, 0, 0, TimeSpan.Zero),
        });

        // 场景 B（session-retained）：最新 shutdown（10:00）之后的 otel 行保留、之前的被抑制；
        // otel 调用 2 == 请求数差分 1+0 → 不接管，shutdown 区间照常出账。
        var retained = records.Where(record => record.SessionId == "session-retained").ToList();
        retained.Should().HaveCount(2, "shutdown 区间（100/10 @10:00）+ 保留的 otel 行（7/3 @11:00）");
        retained.Should().Contain(record => record.InputTokens == 100 && record.OutputTokens == 10
            && record.Timestamp == new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero));
        retained.Should().Contain(record => record.InputTokens == 7 && record.OutputTokens == 3
            && record.Timestamp == new DateTimeOffset(2026, 9, 16, 11, 0, 0, TimeSpan.Zero));

        // 场景 C（conv-dedupe）：双导出 chat span 计一次（B1 折叠）、inference log 与 agent summary
        // 同 response id 被抑制、同 turn 共享 response id 的多次调用按用量区分全保留。
        var dedupe = records.Where(record => record.SessionId == "conv-dedupe").ToList();
        dedupe.Should().HaveCount(5, "A1 / A2 / agent turn / D1 / D2");
        dedupe.Should().NotContain(record => record.InputTokens == 80, "inference log（resp-1）被 chat span 抑制");
        dedupe.Should().NotContain(record => record.InputTokens == 300, "agent summary span（resp-1）被 chat span 抑制");
        dedupe.Should().Contain(record => record.InputTokens == 1000 && record.OutputTokens == 100, "A1（重导出的 B1 计一次）");
        dedupe.Should().Contain(record => record.InputTokens == 100 && record.OutputTokens == 10, "D1：同 turn 共享 response id");
        dedupe.Should().Contain(record => record.InputTokens == 200 && record.OutputTokens == 20
            && record.Timestamp == new DateTimeOffset(2026, 9, 16, 12, 3, 10, TimeSpan.Zero), "D2：同 turn 第二次调用");

        // 场景 D（session-credit）：会话总额 4 credits、模型归因 3 credits → cost-only unknown $0.01；
        // checkpoint（1 credit）早于 shutdown，已含在 shutdown 总额中，不重复出缺口。
        var creditGap = records.Where(record => record.SessionId == "session-credit" && record.Model == "unknown").ToList();
        creditGap.Should().ContainSingle();
        creditGap[0].CostUsd.Should().Be(0.01m, "1e9 nano-AIU / 1e11 = $0.01（decimal 精确换算）");
        creditGap[0].InputTokens.Should().Be(0, "credit gap 是 cost-only 0-token 条目");
        creditGap[0].Timestamp.Should().Be(new DateTimeOffset(2026, 9, 16, 11, 0, 0, TimeSpan.Zero));

        // 场景 E（豁免）：session-exempt 无 checkpoint、session-incomplete shutdown 缺模型侧总额——
        // 区间照常出账但不产出 unknown 条目。
        records.Where(record => record.SessionId == "session-exempt").Should().ContainSingle()
            .Which.Model.Should().Be("claude-sonnet-4");
        records.Where(record => record.SessionId == "session-incomplete").Should().ContainSingle()
            .Which.Model.Should().Be("claude-sonnet-4");
        records.Should().NotContain(record =>
            (record.SessionId == "session-exempt" || record.SessionId == "session-incomplete")
            && record.Model == "unknown");

        // 场景 F（session-otelgap）：checkpoint-only 会话但有 otel 行 → 缺口让位 otel（无 unknown 条目），
        // otel 行照常产出。
        records.Where(record => record.SessionId == "session-otelgap").Should().ContainSingle()
            .Which.InputTokens.Should().Be(1);

        // 场景 G（session-cponly）：checkpoint-only、无 shutdown、无 otel → 全额缺口 $0.003。
        var cpOnly = records.Where(record => record.SessionId == "session-cponly").ToList();
        cpOnly.Should().ContainSingle();
        cpOnly[0].Model.Should().Be("unknown");
        cpOnly[0].CostUsd.Should().Be(0.003m);

        // 场景 H（session-resumegap）：shutdown 后各 checkpoint 的持续未解释部分逐快照成缺口
        // （1.5 credits @09-17 + 0.5 credits @09-18；shutdown 时点已归因、无缺口）。
        var resumeGaps = records
            .Where(record => record.SessionId == "session-resumegap" && record.Model == "unknown")
            .OrderBy(record => record.Timestamp)
            .ToList();
        resumeGaps.Should().HaveCount(2);
        resumeGaps[0].CostUsd.Should().Be(0.015m);
        resumeGaps[0].Timestamp.Should().Be(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
        resumeGaps[1].CostUsd.Should().Be(0.005m);
        resumeGaps[1].Timestamp.Should().Be(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero));

        // 分日总计（与 golden 一致的场景级快照）。
        var day16 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-16");
        day16.InputTokens.Should().Be(3411);
        day16.OutputTokens.Should().Be(347);
        var day17 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-17");
        day17.InputTokens.Should().Be(3);
        day17.OutputTokens.Should().Be(3);
        var day18 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-18");
        day18.InputTokens.Should().Be(0, "credit gap 是 cost-only 0-token 条目，仅计成本");
        day18.OutputTokens.Should().Be(0);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.CopilotGoldenPath)})";

    private static string OursLabel() => "自研链路(CopilotSessionAdapter+PricingEngine)";
}
