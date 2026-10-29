using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 表格列几何回归锁（P0-1 叠字 / P0-2 表头硬裁，2026-10-29 前端审查）。
/// 两个独立事实，分别取证：
/// <list type="number">
/// <item><b>单元格不溢出</b>：行 Grid 的直接子级（单元格）内任何 TextBlock 的右缘都不得越出该单元格。
/// 水平 StackPanel 以无限宽测量子级是既有陷阱（<see cref="Tokenmetry.App.Controls.DonutChart"/> 图例、
/// 会话页 Agent 列都踩过）：单元格 Bounds 只有列宽，但子级按自然宽度排出去，视觉上压进右邻列。</item>
/// <item><b>表头/数值不被裁切</b>：文本自然宽度（FormattedText，实际 Typeface/FontSize）必须装进
/// 已排布宽度。TextBlock 无 TextWrapping 时越界部分是硬裁（不是省略号），故用自然宽度而非 Bounds
/// 作为判据——Bounds 恒等于列宽，永远"装得下"。</item>
/// </list>
/// </summary>
internal static class TableLayoutAssert
{
    /// <summary>容差：亚像素差不算裁切（11px 字号下 0.5px 肉眼不可见）。</summary>
    private const double Tolerance = 0.5;

    /// <summary>行级单元格溢出取证（P0-1）。返回人类可读的违例清单，空 = 通过。</summary>
    internal static IReadOnlyList<string> FindOverflowingCells(Visual root)
    {
        var offenders = new List<string>();

        foreach (var item in root.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.GetVisualAncestors().OfType<ListBox>().All(lb => !lb.Classes.Contains("data-table")))
            {
                continue;
            }

            foreach (var grid in item.GetVisualDescendants().OfType<Grid>())
            {
                // 行 Grid 的直接父级是 DataTemplate 根 Border（不是 Grid），所以只能靠"列数 + data-table
                // 祖先"识别；表头 Grid 无 ListBox 祖先，天然排除（它的单元格就是 TextBlock，无从溢出）。
                if (grid.ColumnDefinitions.Count < 4)
                {
                    continue;
                }

                foreach (var cell in grid.Children.OfType<Control>())
                {
                    var cellRight = cell.Bounds.Size.Width;
                    foreach (var text in cell.GetVisualDescendants().OfType<TextBlock>())
                    {
                        var point = text.TranslatePoint(new Point(text.Bounds.Width, 0), cell);
                        if (point is null)
                        {
                            continue;
                        }

                        if (point.Value.X > cellRight + Tolerance)
                        {
                            offenders.Add(
                                $"cell(col {Grid.GetColumn(cell)}) 内容越界 {point.Value.X - cellRight:F1}px："
                                + $"'{text.Text}' 右缘 {point.Value.X:F1} > 单元格 {cellRight:F1}");
                        }
                    }
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// 表头（TextBlock.table-th）自然宽度装进列宽（P0-2）。<paramref name="fixedColumnsOnly"/> 为 true 时
    /// 只核像素定宽列——星宽列在最小窗口下被压缩属既有挤压行为（可由 TextTrimming/裁切承担），
    /// 定宽列才是"按中文长度拍脑袋定死"的真缺陷。
    /// </summary>
    internal static IReadOnlyList<string> FindClippedHeaders(Visual root, bool fixedColumnsOnly)
    {
        var offenders = new List<string>();

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.Classes.Contains("table-th") || !text.IsVisible || string.IsNullOrEmpty(text.Text))
            {
                continue;
            }

            var available = text.Bounds.Width;
            if (fixedColumnsOnly && FixedColumnWidth(text) is null)
            {
                continue;
            }

            if (!HeaderRequirement(text))
            {
                continue; // 折行表头：见 HeaderRequirement 注释
            }

            var natural = NaturalTextWidth(text);
            if (natural > available + Tolerance)
            {
                offenders.Add($"表头 '{text.Text}' 需 {natural:F1}px，列宽 {available:F1}px");
            }
        }

        return offenders;
    }

    /// <summary>数值单元格（TextBlock.table-num）自然宽度装进列宽——千分位全量数字必须完整可读。</summary>
    internal static IReadOnlyList<string> FindClippedNumbers(Visual root)
    {
        var offenders = new List<string>();

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.Classes.Contains("table-num") || !text.IsVisible || string.IsNullOrEmpty(text.Text))
            {
                continue;
            }

            var natural = NaturalTextWidth(text);
            if (natural > text.Bounds.Width + Tolerance)
            {
                offenders.Add($"数值 '{text.Text}' 需 {natural:F1}px，列宽 {text.Bounds.Width:F1}px");
            }
        }

        return offenders;
    }

    /// <summary>
    /// Agent 单元格内的名称文本（TextBlock.table-td）不得被省略号截断（P0-1 的另一半：不叠字 ≠ 可读）。
    /// 通过 cell 内的 Border.agent-chip 定位单元格，只看该单元格——星宽文本列（项目名 / 模型名）
    /// 允许 TextTrimming，属既有挤压行为。
    /// </summary>
    internal static IReadOnlyList<string> FindTruncatedAgentNames(Visual root)
    {
        var offenders = new List<string>();

        foreach (var chip in root.GetVisualDescendants().OfType<Border>())
        {
            if (!chip.Classes.Contains("agent-chip") || chip.GetVisualParent() is not Control cell)
            {
                continue;
            }

            foreach (var text in cell.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!text.Classes.Contains("table-td") || !text.IsVisible || string.IsNullOrEmpty(text.Text))
                {
                    continue;
                }

                var natural = NaturalTextWidth(text);
                if (natural > text.Bounds.Width + Tolerance)
                {
                    offenders.Add($"Agent 名 '{text.Text}' 需 {natural:F1}px，列宽 {text.Bounds.Width:F1}px（被省略号截断）");
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// 表头裁切判据：只对**不换行**的表头比自然宽度。表头开了 <c>TextWrapping="Wrap"</c> 时折行
    /// 不算裁切（会话/模型两页的表头都允许折行，见 SessionsView 列宽注释），而"折行后是否还被裁"
    /// 取决于断行算法（CJK 逐字可断、连字符可断），用 FormattedText 反推不可靠——所以这里只锁
    /// 不换行表头这一种确定性场景。
    /// </summary>
    internal static bool HeaderRequirement(TextBlock text) => text.TextWrapping == TextWrapping.NoWrap;

    /// <summary>11px 表格文本的自然宽度（与渲染同 Typeface/FontSize，含 LetterSpacing 忽略不计）。</summary>
    internal static double NaturalTextWidth(TextBlock text) =>
        new FormattedText(
            text.Text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black).Width;

    private static double? FixedColumnWidth(TextBlock text)
    {
        for (Visual? node = text; node is not null; node = node.GetVisualParent())
        {
            if (node is not Grid grid)
            {
                continue;
            }

            var column = Grid.GetColumn(text);
            if (column < 0 || column >= grid.ColumnDefinitions.Count)
            {
                return null;
            }

            var definition = grid.ColumnDefinitions[column];
            return definition.Width.IsAbsolute ? definition.ActualWidth : null;
        }

        return null;
    }

    /// <summary>把违例清单折成一条断言（全量输出，便于一次测量拿到所有超标列）。</summary>
    internal static void AssertNone(IReadOnlyList<string> offenders, string what)
    {
        Assert.True(
            offenders.Count == 0,
            $"{what}：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
