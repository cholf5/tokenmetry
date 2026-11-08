using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.Headless.Tests.Shell;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Promo;

/// <summary>
/// 官网发布 PV（tokenmetry-site <c>promo/</c>）v0.5.0 功能素材：分享海报 5 风格 × zh/en、
/// 画板 4 色（Nord/Dracula/Solarized/Gruvbox）× zh/en 的总览真渲染、定价计算器。
/// 与 SiteShotsTests 同模式：数据是<b>受控演示数据</b>（命名沿 SiteData 家族，量级诚实可读），
/// 日期固定锚定 2026-10-09..11-07 保证可复现——峰值日 10-22 对齐站点复刻的「最活跃日」锚定。
/// 留档目录经 <c>TOKENMETRY_PV_SHOTS_DIR</c> 传入；未设置时只断言可渲染，不落盘。
/// </summary>
public class PromoShotsTests
{
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_PV_SHOTS_DIR");

    // ── 受控演示数据（固定 30 天窗口，可复现）──

    private static class PvData
    {
        public static readonly DateOnly Start = new(2026, 10, 9); // 周五
        public static readonly DateOnly End = new(2026, 11, 7);   // 周六，共 30 天

        /// <summary>(日偏移, 总 token 百万, 成本未知)。工作日饱满、周六低、周日如实留白；
        /// 10-13（周二）停用缺口；10-22（周四）为峰值——对齐站点复刻锚定的「最活跃日」。</summary>
        private static readonly (double M, bool UnknownCost)[] Shape =
        {
            (7.8, false), (2.1, false), (0, false),
            (8.4, false), (0, false), (6.9, false),
            (9.2, false), (7.5, false), (1.8, false), (0, false),
            (8.8, false), (10.4, false), (12.6, false), (21.4, false), (9.6, false),
            (2.4, false), (0, false),
            (8.2, false), (9.9, false), (7.7, false), (11.3, false), (8.9, false),
            (1.6, false), (0, false),
            (9.4, false), (10.8, false), (8.6, false), (12.1, false), (9.7, false),
            (2.8, true),
        };

        /// <summary>峰值日 10-22（偏移 13）：托盘信息卡用「最猛的一天」当样张。</summary>
        public static DateOnly PeakDay => Start.AddDays(13);

        /// <summary>单日切片：真实流程里查询服务按范围返回行，托盘信息卡（单日）的数据窗口
        /// 必须与请求范围一致——快照 Build 不裁剪，把 30 天行喂给单日卡会得到跨窗口径。
        /// 单日维度行按峰值日占 30 天总量的比例（≈10.2%）同缩，成本比例不变。</summary>
        public static (IReadOnlyList<DailyUsagePoint> Daily, IReadOnlyList<DimensionUsageRow> Agents,
            IReadOnlyList<DimensionUsageRow> Models) SingleDay()
        {
            const double scale = 21.4 / 209.9;
            var day = Daily().First(p => UsageQueryFilter.ParseDay(p.Day) == PeakDay);
            return (new[] { day }, Scale(Agents(), scale), Scale(Models(), scale));
        }

        private static IReadOnlyList<DimensionUsageRow> Scale(IReadOnlyList<DimensionUsageRow> rows, double k) =>
            rows.Select(r => new DimensionUsageRow
            {
                Key = r.Key,
                DisplayName = r.DisplayName,
                InputTokens = (long)(r.InputTokens * k),
                OutputTokens = (long)(r.OutputTokens * k),
                CacheCreationTokens = (long)(r.CacheCreationTokens * k),
                CacheReadTokens = (long)(r.CacheReadTokens * k),
                TotalTokens = (long)(r.TotalTokens * k),
                Cost = r.Cost.KnownCostUsd is null
                    ? r.Cost
                    : new AggregatedCost(r.Cost.KnownCostUsd * (decimal)k, r.Cost.HasUnknownCost),
                TokenShare = null,
            }).ToArray();

        public static IReadOnlyList<DailyUsagePoint> Daily() =>
            Shape.Select((s, i) =>
            {
                var total = (long)(s.M * 1_000_000);
                return new DailyUsagePoint
                {
                    Day = UsageQueryFilter.FormatDay(Start.AddDays(i)),
                    InputTokens = total * 12 / 100,
                    OutputTokens = total * 5 / 100,
                    CacheCreationTokens = total * 5 / 100,
                    CacheReadTokens = total * 78 / 100,
                    TotalTokens = total,
                    Cost = total > 0
                        ? new AggregatedCost((decimal)(s.M * 2.2), s.UnknownCost)
                        : AggregatedCost.Empty,
                    HasData = total > 0,
                };
            }).ToArray();

        // 三维度合计都与日序列一致（≈2.10 亿）；成本比例沿 SiteData 家族（≈$2.2/M）。
        public static IReadOnlyList<DimensionUsageRow> Agents() => new[]
        {
            Dim("Claude Code", 90.2, 196.7m),
            Dim("Codex", 56.3, 122.8m),
            Dim("ZCode", 30.6, 66.8m),
            Dim("Gemini CLI", 19.6, 42.8m),
            Dim("Kimi Code", 13.2, null),
        };

        public static IReadOnlyList<DimensionUsageRow> Models() => new[]
        {
            Dim("claude-sonnet-4-5", 89.4, 195.8m),
            Dim("gpt-5.1", 47.8, 104.3m),
            Dim("glm-5.3", 24.9, 54.3m),
            Dim("gemini-2.5-pro", 16.6, 36.2m),
            Dim("qwen3-coder-plus", 11.8, 25.7m),
            Dim("kimi-k2.5", 10.7, null),
            Dim("claude-haiku-4.5", 8.7, 19.0m),
        };

        public static IReadOnlyList<DimensionUsageRow> Projects() => new[]
        {
            Dim("/Users/dev/tokenmetry", 58.4, 127.4m),
            Dim("/Users/dev/checkout-api", 47.2, 102.9m),
            Dim("/Users/work/billing-etl", 38.6, 84.1m),
            Dim("/Users/dev/rag-playground", 36.9, 80.4m),
            Dim("/Users/dev/cholf-site", 28.8, 62.9m),
        };

        private static DimensionUsageRow Dim(string key, double m, decimal? cost) => new()
        {
            Key = key,
            InputTokens = (long)(m * 120_000),
            OutputTokens = (long)(m * 50_000),
            CacheCreationTokens = (long)(m * 50_000),
            CacheReadTokens = (long)(m * 780_000),
            TotalTokens = (long)(m * 1_000_000),
            Cost = new AggregatedCost(cost, cost is null),
            TokenShare = null,
        };

        /// <summary>星期 × 小时矩阵：小时曲线沿 SiteData.Hourly 的形状，工作日权重 1.0、
        /// 周六 0.3、周日 0.06；粗精度会话的 unknown 如实给一笔（周合计 ≈55M，与月窗口量级相称）。</summary>
        public static WeeklyRhythmResult Rhythm()
        {
            var hourCurve = new Dictionary<int, double>
            {
                [9] = 0.9, [10] = 2.1, [11] = 1.8, [12] = 0.8, [13] = 1.2, [14] = 2.4,
                [15] = 2.0, [16] = 1.7, [17] = 1.1, [18] = 0.5, [19] = 0.9, [20] = 1.9,
                [21] = 1.6, [22] = 0.7,
            };
            var dayWeight = new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 0.3, 0.06 }; // 周一..周日
            var cells = new List<WeeklyRhythmCell>();
            for (var w = 0; w < 7; w++)
            {
                for (var h = 0; h < 24; h++)
                {
                    var m = hourCurve.GetValueOrDefault(h) * dayWeight[w];
                    cells.Add(new WeeklyRhythmCell
                    {
                        Weekday = w,
                        Hour = h,
                        SessionCount = m > 0 ? Math.Max(1, (int)(m * 12)) : 0,
                        TotalTokens = m > 0 ? (long)(m * 520_000) : 0,
                    });
                }
            }
            return new WeeklyRhythmResult
            {
                Cells = cells,
                UnknownSessionCount = 31,
                UnknownTotalTokens = 2_240_000,
                TimeZoneId = TimeZoneInfo.Local.Id,
            };
        }
    }

    private static (AppLanguage Lang, string Locale)[] Languages =>
    [
        (AppLanguage.SimplifiedChinese, "zh"),
        (AppLanguage.English, "en"),
    ];

    // ── 海报：5 风格 × zh/en，dark 主题，按 PosterRoot 2× 光栅化 ──

    [AvaloniaFact]
    public void Posters_render_five_styles_for_pv()
    {
        var daily = PvData.Daily();
        var agents = PvData.Agents();
        var models = PvData.Models();
        var projects = PvData.Projects();
        var (singleDaily, singleAgents, singleModels) = PvData.SingleDay();

        var cases = new (SharePosterStyle Style, SharePosterLayout Layout, DateOnly Start, DateOnly End, string File)[]
        {
            (SharePosterStyle.Overview, SharePosterLayout.Portrait, PvData.Start, PvData.End, "overview"),
            (SharePosterStyle.Heatmap, SharePosterLayout.Landscape, PvData.Start, PvData.End, "heatmap"),
            (SharePosterStyle.Rhythm, SharePosterLayout.Landscape, PvData.Start, PvData.End, "rhythm"),
            (SharePosterStyle.Milestone, SharePosterLayout.Portrait, PvData.Start, PvData.End, "milestone"),
            (SharePosterStyle.Tray, SharePosterLayout.Portrait, PvData.PeakDay, PvData.PeakDay, "tray"),
        };

        var original = Translations.Instance.CurrentLanguage;
        try
        {
            foreach (var (language, locale) in Languages)
            {
                Translations.Instance.SetLanguage(language);
                foreach (var c in cases)
                {
                    var request = new SharePosterRequest(
                        c.Layout, SharePosterTheme.Dark, SharePosterProjectMask.Off, c.Start, c.End, c.Style);
                    // 单日卡用单日切片（口径对齐真实流程：查询按范围返回），其余样式用全窗口数据。
                    var dayDaily = c.Style == SharePosterStyle.Tray ? singleDaily : daily;
                    var dayAgents = c.Style == SharePosterStyle.Tray ? singleAgents : agents;
                    var dayModels = c.Style == SharePosterStyle.Tray ? singleModels : models;
                    var data = SharePosterSnapshot.Build(
                        request, dayDaily, dayAgents, dayModels, projects,
                        sessionCount: c.Style == SharePosterStyle.Tray ? 41 : 832,
                        weeklyRhythm: c.Style == SharePosterStyle.Rhythm ? PvData.Rhythm() : null);
                    var view = new SharePosterView { DataContext = new SharePosterViewModel(data) };

                    var wide = c.Layout == SharePosterLayout.Landscape;
                    var window = new Window
                    {
                        Width = wide ? 1900 : 1120,
                        Height = wide ? 1000 : 1700,
                        Content = view,
                        RequestedThemeVariant = ThemeVariant.Dark,
                        Background = Brushes.Black,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                    using var bitmap = SharePosterExporter.Render(view.PosterRootControl);
                    ChartTestHelpers.SaveArchive(bitmap, ArchiveDir, $"poster-{c.File}-{locale}.png");

                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            Translations.Instance.SetLanguage(original); // 进程级单例，恢复基线
        }
    }

    // ── 画板：4 色画板套总览页真渲染 × zh/en（dark 变体；Nord/Dracula 仅 dark，强制唯一变体）──

    [AvaloniaFact]
    public void Canvases_render_overview_for_pv()
    {
        var service = new ThemeService();
        var original = Translations.Instance.CurrentLanguage;
        try
        {
            foreach (var (language, locale) in Languages)
            {
                Translations.Instance.SetLanguage(language);
                foreach (var palette in new[] { "nord", "dracula", "solarized", "gruvbox" })
                {
                    service.Apply(palette, "dark");
                    var stub = new SiteShotsTests.StubQueryService
                    {
                        Kpi = SiteShotsTests.SiteData.WeekKpi(),
                        Daily = SiteShotsTests.SiteData.Week(),
                        Hourly = SiteShotsTests.SiteData.Hourly(),
                        Agents = SiteShotsTests.SiteData.Agents(),
                        Models = SiteShotsTests.SiteData.Models(),
                        Projects = SiteShotsTests.SiteData.Projects(),
                    };
                    var vm = new OverviewViewModel(
                        ShellPages.For(PageId.Overview), new FilterStateService(), stub);
                    Dispatcher.UIThread.RunJobs();

                    var window = new Window
                    {
                        Width = 1280,
                        Height = 1120,
                        Content = new OverviewView { DataContext = vm },
                        RequestedThemeVariant = ThemeVariant.Dark,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    ChartTestHelpers.SaveArchive(frame!, ArchiveDir, $"theme-{palette}-{locale}.png");

                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark"); // 画板还原默认
            Translations.Instance.SetLanguage(original);
        }
    }

    // ── 定价计算器：独立工具窗 × zh/en ──

    [AvaloniaFact]
    public void Price_calculator_renders_for_pv()
    {
        var original = Translations.Instance.CurrentLanguage;
        try
        {
            foreach (var (language, locale) in Languages)
            {
                Translations.Instance.SetLanguage(language);
                var window = new PriceCalculatorWindow { RequestedThemeVariant = ThemeVariant.Dark };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                ChartTestHelpers.SaveArchive(frame!, ArchiveDir, $"calc-{locale}.png");

                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            Translations.Instance.SetLanguage(original);
        }
    }
}
