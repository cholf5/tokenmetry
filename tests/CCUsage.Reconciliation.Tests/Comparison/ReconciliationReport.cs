using System.Text;

namespace CCUsage.Reconciliation.Tests.Comparison;

/// <summary>
/// 一次对账比较的结果：差异明细（按 日期 × 模型 × 字段）+ 文本报告。
/// 测试失败时把 <see cref="Render"/> 全文打进断言消息，差异可定位到解析或定价原因。
/// </summary>
public sealed class ReconciliationReport
{
    public ReconciliationReport(
        ReconciliationTolerance tolerance,
        IReadOnlyList<ReconciliationDifference> differences,
        int comparedEntries,
        string goldenLabel,
        string oursLabel)
    {
        Tolerance = tolerance;
        Differences = differences;
        ComparedEntries = comparedEntries;
        GoldenLabel = goldenLabel;
        OursLabel = oursLabel;
    }

    public ReconciliationTolerance Tolerance { get; }

    public IReadOnlyList<ReconciliationDifference> Differences { get; }

    /// <summary>双方都存在并逐项比较过的 日期 × 模型 条目数（缺失条目不计入）。</summary>
    public int ComparedEntries { get; }

    public string GoldenLabel { get; }

    public string OursLabel { get; }

    public bool HasDifferences => Differences.Count > 0;

    public string Render()
    {
        var builder = new StringBuilder();
        builder.Append("对账比较：")
            .Append(GoldenLabel)
            .Append(" vs ")
            .Append(OursLabel)
            .Append("；比较 ")
            .Append(ComparedEntries)
            .Append(" 组 日期×模型，tokens 精确一致，cost 相对误差 ≤ ")
            .Append(Tolerance.CostRelativeTolerance.ToString("0.###"))
            .Append(" 或绝对误差 < $")
            .Append(Tolerance.CostAbsoluteToleranceUsd.ToString("0.###"));

        if (!HasDifferences)
        {
            builder.AppendLine().Append("无差异：两条链路在该容差内一致。");
            return builder.ToString();
        }

        builder.AppendLine().Append("差异 ").Append(Differences.Count).AppendLine(" 项：");
        foreach (var difference in Differences)
        {
            builder.Append("  [").Append(difference.KindLabel).Append("] ")
                .Append(difference.DisplayName)
                .Append(" golden=").Append(difference.GoldenValue)
                .Append(" ours=").Append(difference.OursValue);
            if (difference.Delta.Length > 0)
            {
                builder.Append(" 差=").Append(difference.Delta);
            }

            if (difference.RelativeDeviation is { } deviation)
            {
                builder.Append(" 相对偏差=").Append(deviation.ToString("+0.00%;-0.00%"));
            }

            builder.AppendLine();
            builder.Append("      原因：").AppendLine(difference.Hint);
        }

        return builder.ToString().TrimEnd();
    }

    public override string ToString() => Render();
}
