using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Tray;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 官网（tokenmetry.app）功能页截图留档：密集 stub 数据 × zh/en × 明暗双主题，裸窗整页渲染。
/// 与既有留档（<c>*-light/dark.png</c>，真实 DI 隔离空库的诚实空态）互补，本套面向对外展示：
/// <list type="bullet">
/// <item>数据是<b>受控演示数据</b>（非真实用户数据；模型名/量级/成本比例取真实区间，诚实可读），只服务截图；</item>
/// <item>裸窗（无侧栏外壳）——官网 Hero 的全窗视角由真机手截图承担，功能特写只需页面本体；</item>
/// <item>命名 <c>{view}-{locale}-{theme}.png</c>（如 time-zh-dark.png），与站点仓库 src/assets/shots 约定一致；</item>
/// <item>留档目录经 <c>TOKENMETRY_SITE_SHOTS_DIR</c> 传入；未设置时只断言富数据可渲染，不落盘；</item>
/// <item>fixture 锚定<b>真实今天</b>（相对时间列、默认「最近 7 天」档与日历热图都要活数据）。</item>
/// </list>
/// </summary>
public class SiteShotsTests
{
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_SITE_SHOTS_DIR");

    // ── stub：查询服务（七面数据可配置，模式同 DimensionPagesTests）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public HourlyDistributionResult Hourly { get; set; } = new() { Hours = Array.Empty<HourlyUsageBucket>() };

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

        public SessionListResult Sessions { get; set; } = new();

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Kpi);

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Daily);

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Agents);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Models);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Models.Select(m => m.Key).ToList());

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Projects);

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(Sessions);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Hourly);
    }

    private sealed class StubScanReader : IIngestionScanReader
    {
        public List<string> Registered { get; } = new();

        private readonly Dictionary<string, IngestionSourceScanSnapshot> _scans = new();

        public IReadOnlyList<string> RegisteredSources => Registered;

        public IngestionSourceScanSnapshot? GetLastScan(string agent) => _scans.GetValueOrDefault(agent);

        public void Register(string agent, IngestionSourceScanSnapshot snapshot)
        {
            if (!Registered.Contains(agent))
            {
                Registered.Add(agent);
            }

            _scans[agent] = snapshot;
        }
    }

    private sealed class StubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    // ── 受控演示数据：一周多 Agent / 多模型混用（量级与成本比例取真实区间）──

    /// <summary>演示数据基线：7 天总 token 36.44M / 已知成本 $81.22 + 缺价（kimi-k2.5）未知贡献 / 214 会话。</summary>
    private static class SiteData
    {
        public const long WeekTokens = 36_440_000;

        public const long WeekCacheRead = 28_430_000;

        public const decimal WeekKnownCost = 81.22m;

        public const long WeekSessions = 214;

        /// <summary>日序列（今天收尾，进行中偏轻；周二/周四重载），天粒度四分量之和 = 该日总量。</summary>
        public static IReadOnlyList<DailyUsagePoint> Week()
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            // (总token 万, 已知成本, 是否含缺价模型)：索引 0 = 今天-6 … 6 = 今天
            var shape = new (double TokensM, decimal Cost, bool Unknown)[]
            {
                (4.6, 8.9m, false),
                (7.2, 14.8m, true),
                (5.4, 11.2m, false),
                (8.1, 17.1m, false),
                (6.5, 14.0m, true),
                (2.4, 5.9m, false),
                (2.24, 9.3m, true),
            };
            return Enumerable.Range(0, 7).Select(i =>
            {
                var total = (long)(shape[i].TokensM * 1_000_000);
                return new DailyUsagePoint
                {
                    Day = UsageQueryFilter.FormatDay(today.AddDays(i - 6)),
                    InputTokens = total * 12 / 100,
                    OutputTokens = total * 5 / 100,
                    CacheCreationTokens = total * 5 / 100,
                    CacheReadTokens = total * 78 / 100,
                    TotalTokens = total,
                    Cost = new AggregatedCost(shape[i].Cost, shape[i].Unknown),
                    HasData = true,
                };
            }).ToList();
        }

        /// <summary>KPI：本周 vs 等长回看（上周略低 → 正向环比卡片有内容）。</summary>
        public static UsageKpiResult WeekKpi() => new()
        {
            TotalTokens = WeekTokens,
            CacheReadTokens = WeekCacheRead,
            Cost = new AggregatedCost(WeekKnownCost, hasUnknownCost: true),
            SessionCount = WeekSessions,
            CacheReadShare = 0.78m,
            PreviousPeriod = new KpiPeriodComparison
            {
                FromDay = UsageQueryFilter.FormatDay(DateOnly.FromDateTime(DateTime.Today.AddDays(-13))),
                ToDay = UsageQueryFilter.FormatDay(DateOnly.FromDateTime(DateTime.Today.AddDays(-7))),
                TotalTokens = 33_100_000,
                Cost = new AggregatedCost(74.90m, false),
                SessionCount = 198,
                CacheReadShare = 0.76m,
                TotalTokensChange = MetricChange.From(36_440_000m, 33_100_000m),
                CostChange = MetricChange.From(81.22m, 74.90m),
                SessionCountChange = MetricChange.From(214m, 198m),
                CacheReadShareChange = MetricChange.From(0.78m, 0.76m),
            },
        };

        /// <summary>小时分布：工作时段堆积 + 晚间高峰；粗粒度源 26 会话入 unknown（诚实可见，不伪造）。</summary>
        public static HourlyDistributionResult Hourly()
        {
            var sessionHours = new Dictionary<int, (int Sessions, double TokensM)>
            {
                [9] = (2, 0.9),
                [10] = (6, 2.1),
                [11] = (5, 1.8),
                [12] = (3, 0.8),
                [13] = (4, 1.2),
                [14] = (7, 2.4),
                [15] = (6, 2.0),
                [16] = (5, 1.7),
                [17] = (4, 1.1),
                [18] = (2, 0.5),
                [19] = (3, 0.9),
                [20] = (6, 1.9),
                [21] = (5, 1.6),
                [22] = (3, 0.7),
            };
            return new HourlyDistributionResult
            {
                Hours = Enumerable.Range(0, 24).Select(hour =>
                {
                    var (sessions, tokensM) = sessionHours.GetValueOrDefault(hour, (0, 0));
                    var total = (long)(tokensM * 1_000_000);
                    return new HourlyUsageBucket
                    {
                        Hour = hour,
                        SessionCount = sessions,
                        InputTokens = total * 12 / 100,
                        OutputTokens = total * 5 / 100,
                        CacheReadTokens = total * 78 / 100,
                        TotalTokens = total,
                        Cost = sessions > 0
                            ? new AggregatedCost((decimal)tokensM * 2.24m, false)
                            : AggregatedCost.Empty,
                    };
                }).ToList(),
                UnknownSessionCount = 26,
                UnknownTotalTokens = 1_860_000,
                TimeZoneId = TimeZoneInfo.Local.Id,
            };
        }

        public static IReadOnlyList<DimensionUsageRow> Agents() => new[]
        {
            Agent("Claude Code", 86, 15_940_000, 38.62m),
            Agent("Codex", 54, 9_830_000, 23.15m),
            Agent("ZCode", 33, 5_210_000, 12.08m),
            Agent("Gemini CLI", 24, 3_340_000, 7.42m),
            Agent("Kimi Code", 17, 2_120_000, null),
        };

        /// <summary>模型维度：真实模型名 + 唯一 provider + 关联 Agent；kimi-k2.5 缺价（成本未知不折 0）。</summary>
        public static IReadOnlyList<DimensionUsageRow> Models() => new[]
        {
            Model("claude-sonnet-4-5", "Anthropic", "Claude Code", 1, 74, 14_260_000, 34.85m),
            Model("gpt-5.1", "OpenAI", "Codex", 1, 48, 9_150_000, 21.53m),
            Model("glm-5.3", "Z.ai", "ZCode", 1, 29, 4_760_000, 11.02m),
            Model("gemini-2.5-pro", "Google", "Gemini CLI", 1, 22, 3_180_000, 7.06m),
            Model("qwen3-coder-plus", "Alibaba", "ZCode", 2, 18, 1_920_000, 4.81m),
            Model("kimi-k2.5", "Moonshot", "Kimi Code", 1, 15, 2_120_000, null),
            Model("claude-haiku-4.5", "Anthropic", "Claude Code", 2, 9, 1_050_000, 0.95m),
        };

        public static IReadOnlyList<DimensionUsageRow> Projects() => new[]
        {
            Project("/Users/dev/tokenmetry", "tokenmetry", 52, 9_640_000, 22.18m),
            Project("/Users/dev/checkout-api", "checkout-api", 38, 11_820_000, 27.41m),
            Project("/Users/work/billing-etl", "billing-etl", 41, 6_350_000, 14.52m),
            Project("/Users/dev/rag-playground", "rag-playground", 47, 5_230_000, 11.84m),
            Project("/Users/dev/cholf-site", "cholf-site", 36, 3_400_000, 5.27m),
        };

        /// <summary>会话明细：近两天的真实感混用（kimi 两行成本未知；TotalCount=214 供分页条）。</summary>
        public static SessionListResult Sessions()
        {
            var rows = new List<SessionListRow>();
            var today = DateTime.Today;
            void Add(string agent, string model, string project, int dayOffset, int h1, int m1, int h2, int m2, double tokensM, decimal? cost)
            {
                var total = (long)(tokensM * 1_000_000);
                rows.Add(new SessionListRow
                {
                    SessionId = Guid.NewGuid().ToString(),
                    Agent = agent,
                    Project = project,
                    ProjectDisplayName = project[(project.LastIndexOf('/') + 1)..],
                    DominantModel = model,
                    FirstSeenAt = new DateTimeOffset(today.AddDays(-dayOffset).AddHours(h1).AddMinutes(m1), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)),
                    LastSeenAt = new DateTimeOffset(today.AddDays(-dayOffset).AddHours(h2).AddMinutes(m2), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)),
                    InputTokens = total * 12 / 100,
                    OutputTokens = total * 5 / 100,
                    CacheCreationTokens = total * 5 / 100,
                    CacheReadTokens = total * 78 / 100,
                    TotalTokens = total,
                    CostUsd = cost,
                });
            }

            Add("Claude Code", "claude-sonnet-4-5", "/Users/dev/checkout-api", 0, 9, 41, 11, 58, 2.41, 5.87m);
            Add("Codex", "gpt-5.1", "/Users/dev/checkout-api", 0, 10, 12, 10, 47, 0.86, 2.03m);
            Add("ZCode", "glm-5.3", "/Users/dev/tokenmetry", 0, 11, 5, 13, 22, 1.73, 4.01m);
            Add("Claude Code", "claude-sonnet-4-5", "/Users/dev/tokenmetry", 0, 13, 44, 15, 2, 1.18, 2.88m);
            Add("Gemini CLI", "gemini-2.5-pro", "/Users/dev/rag-playground", 0, 14, 20, 14, 51, 0.64, 1.42m);
            Add("Kimi Code", "kimi-k2.5", "/Users/work/billing-etl", 0, 15, 10, 16, 3, 0.72, null);
            Add("Codex", "gpt-5.1", "/Users/work/billing-etl", 0, 16, 22, 17, 40, 1.05, 2.47m);
            Add("Claude Code", "claude-haiku-4.5", "/Users/dev/cholf-site", 0, 17, 55, 18, 12, 0.31, 0.28m);
            Add("ZCode", "glm-5.3", "/Users/dev/cholf-site", 0, 19, 4, 19, 38, 0.54, 1.25m);
            Add("Claude Code", "claude-sonnet-4-5", "/Users/dev/rag-playground", 0, 20, 15, 21, 26, 0.94, 2.29m);
            Add("Codex", "gpt-5.1", "/Users/dev/tokenmetry", 1, 16, 48, 18, 30, 1.32, 3.11m);
            Add("Claude Code", "claude-sonnet-4-5", "/Users/dev/checkout-api", 1, 10, 22, 12, 5, 2.07, 5.04m);
            Add("Gemini CLI", "gemini-2.5-pro", "/Users/dev/rag-playground", 1, 15, 33, 16, 19, 0.58, 1.29m);
            Add("Kimi Code", "kimi-k2.5", "/Users/work/billing-etl", 1, 11, 47, 12, 26, 0.61, null);

            return new SessionListResult { Rows = rows, TotalCount = WeekSessions, Page = 1, PageSize = 50 };
        }

        /// <summary>托盘弹层（今日口径）：今日 KPI + 今日构成三分量 + 今日 Top Agent。</summary>
        public static StubQueryService TodayForPopover() => new()
        {
            Kpi = new UsageKpiResult
            {
                TotalTokens = 2_240_000,
                CacheReadTokens = 1_742_000,
                Cost = new AggregatedCost(9.31m, hasUnknownCost: true),
                SessionCount = 23,
                CacheReadShare = 0.79m,
            },
            Daily = new[]
            {
                new DailyUsagePoint
                {
                    Day = UsageQueryFilter.FormatDay(DateOnly.FromDateTime(DateTime.Today)),
                    InputTokens = 268_800,
                    OutputTokens = 112_000,
                    CacheCreationTokens = 117_200,
                    CacheReadTokens = 1_742_000,
                    TotalTokens = 2_240_000,
                    Cost = new AggregatedCost(9.31m, true),
                    HasData = true,
                },
            },
            Agents = new[]
            {
                Agent("Claude Code", 9, 980_000, 4.12m),
                Agent("Codex", 6, 610_000, 1.47m),
                Agent("ZCode", 4, 430_000, 0.99m),
                Agent("Kimi Code", 4, 220_000, null),
            },
        };

        private static DimensionUsageRow Agent(string key, long sessions, long tokens, decimal? knownCost) => new()
        {
            Key = key,
            SessionCount = sessions,
            InputTokens = tokens * 12 / 100,
            OutputTokens = tokens * 5 / 100,
            CacheReadTokens = tokens * 78 / 100,
            TotalTokens = tokens,
            Cost = knownCost is { } cost ? new AggregatedCost(cost, false) : new AggregatedCost(null, true),
            TokenShare = tokens / (decimal)WeekTokens,
        };

        private static DimensionUsageRow Model(
            string key, string provider, string dominantAgent, int agentCount, long sessions, long tokens, decimal? knownCost) => new()
        {
            Key = key,
            Provider = provider,
            ProviderCount = 1,
            DominantAgent = dominantAgent,
            AgentCount = agentCount,
            SessionCount = sessions,
            InputTokens = tokens * 12 / 100,
            OutputTokens = tokens * 5 / 100,
            CacheReadTokens = tokens * 78 / 100,
            TotalTokens = tokens,
            Cost = knownCost is { } cost ? new AggregatedCost(cost, false) : new AggregatedCost(null, true),
            TokenShare = tokens / (decimal)WeekTokens,
            CostShare = knownCost is { } share ? share / WeekKnownCost : null,
        };

        private static DimensionUsageRow Project(string path, string display, long sessions, long tokens, decimal knownCost) => new()
        {
            Key = path,
            DisplayName = display,
            SessionCount = sessions,
            InputTokens = tokens * 12 / 100,
            OutputTokens = tokens * 5 / 100,
            CacheReadTokens = tokens * 78 / 100,
            TotalTokens = tokens,
            Cost = new AggregatedCost(knownCost, false),
            TokenShare = tokens / (decimal)WeekTokens,
        };
    }

    // ── 渲染留档骨架：zh/en × light/dark 四联，每语言重建 VM（语言订阅态不跨语言复用）──

    private static void ArchivePage(
        string view,
        double width,
        double height,
        Func<UserControl> createView,
        Func<bool> hasData)
    {
        var original = Translations.Instance.CurrentLanguage;
        try
        {
            foreach (var (language, locale) in new (AppLanguage, string)[]
                     {
                         (AppLanguage.SimplifiedChinese, "zh"),
                         (AppLanguage.English, "en"),
                     })
            {
                Translations.Instance.SetLanguage(language);
                foreach (var (variant, theme) in new (ThemeVariant, string)[]
                         {
                             (ThemeVariant.Light, "light"),
                             (ThemeVariant.Dark, "dark"),
                         })
                {
                    var window = new Window
                    {
                        Width = width,
                        Height = height,
                        Content = createView(),
                        RequestedThemeVariant = variant,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(hasData(), $"{view} 富数据前提不成立（留档失去价值）");

                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);

                    ChartTestHelpers.SaveArchive(frame!, ArchiveDir, $"{view}-{locale}-{theme}.png");

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

    // ── 页面留档 ──

    [AvaloniaFact]
    public void Time_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("time", 1280, 1080, () =>
        {
            var vm = new TimeViewModel(
                ShellPages.For(PageId.Time), new FilterStateService(),
                new StubQueryService { Hourly = SiteData.Hourly(), Daily = SiteData.Week() });
            Dispatcher.UIThread.RunJobs();
            return new TimeAnalysisView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Cost_page_renders_dense_data_for_site_shots()
    {
        CostViewModel? vm = null;
        ArchivePage("cost", 1280, 860, () =>
        {
            vm = new CostViewModel(
                ShellPages.For(PageId.Cost), new FilterStateService(),
                new StubQueryService
                {
                    Kpi = SiteData.WeekKpi(),
                    Daily = SiteData.Week(),
                    Models = SiteData.Models(),
                },
                new PricingEngine());
            Dispatcher.UIThread.RunJobs();
            return new CostAnalysisView { DataContext = vm };
        }, () => vm is not null && vm.UnpricedCountText != "—"); // 加载完成谓词（口径同主留档测试）
    }

    [AvaloniaFact]
    public void Models_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("models", 1280, 860, () =>
        {
            var vm = new ModelsViewModel(
                ShellPages.For(PageId.Models), new FilterStateService(),
                new StubQueryService { Models = SiteData.Models() });
            Dispatcher.UIThread.RunJobs();
            return new ModelsView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Agents_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("agents", 1280, 860, () =>
        {
            var vm = new AgentsViewModel(
                ShellPages.For(PageId.Agents), new FilterStateService(),
                new StubQueryService { Agents = SiteData.Agents() }, _ => { });
            Dispatcher.UIThread.RunJobs();
            return new AgentsView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Projects_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("projects", 1280, 860, () =>
        {
            var vm = new ProjectsViewModel(
                ShellPages.For(PageId.Projects), new FilterStateService(),
                new StubQueryService { Projects = SiteData.Projects() }, (_, _) => { });
            Dispatcher.UIThread.RunJobs();
            return new ProjectsView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Sessions_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("sessions", 1280, 860, () =>
        {
            var vm = new SessionsViewModel(
                ShellPages.For(PageId.Sessions), new FilterStateService(),
                new StubQueryService { Sessions = SiteData.Sessions() }, new StubToast());
            Dispatcher.UIThread.RunJobs();
            return new SessionsView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Data_pricing_page_renders_dense_data_for_site_shots()
    {
        ArchivePage("data-pricing", 1180, 900, () =>
        {
            var reader = new StubScanReader();
            var scanned = DateTimeOffset.Now.AddMinutes(-2);
            reader.Registered.AddRange(new[] { "Claude Code", "Codex", "ZCode", "DSH", "Gemini CLI", "Copilot CLI", "Kimi Code", "Hermes" });
            reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 2184, scanned));
            reader.Register("Codex", new IngestionSourceScanSnapshot("Codex", JsonlScanStatus.Ok, 1462, scanned));
            reader.Register("ZCode", new IngestionSourceScanSnapshot("ZCode", JsonlScanStatus.Ok, 938, scanned));
            reader.Register("DSH", new IngestionSourceScanSnapshot("DSH", JsonlScanStatus.Ok, 421, scanned));
            reader.Register("Gemini CLI", new IngestionSourceScanSnapshot("Gemini CLI", JsonlScanStatus.Ok, 356, scanned));
            reader.Register("Copilot CLI", new IngestionSourceScanSnapshot("Copilot CLI", JsonlScanStatus.Partial, 128, scanned));
            reader.Register("Kimi Code", new IngestionSourceScanSnapshot("Kimi Code", JsonlScanStatus.NoData, 0, scanned));
            // Hermes 已注册未扫描 → 诚实「尚未扫描」行（状态丰富度，对齐产品真实口径）

            var vm = new DataPricingViewModel(
                new AppSettings(),
                new JsonAppSettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "tokenmetry-site-shots-settings"))),
                reader,
                new BackgroundRefreshService(() => Task.FromResult(true)),
                new StubToast(),
                query: null,
                catalog: null);
            Dispatcher.UIThread.RunJobs();
            return new DataPricingView { DataContext = vm };
        }, () => true);
    }

    [AvaloniaFact]
    public void Tray_popover_renders_dense_data_for_site_shots()
    {
        var original = Translations.Instance.CurrentLanguage;
        try
        {
            foreach (var (language, locale) in new (AppLanguage, string)[]
                     {
                         (AppLanguage.SimplifiedChinese, "zh"),
                         (AppLanguage.English, "en"),
                     })
            {
                Translations.Instance.SetLanguage(language);
                foreach (var (variant, theme) in new (ThemeVariant, string)[]
                         {
                             (ThemeVariant.Light, "light"),
                             (ThemeVariant.Dark, "dark"),
                         })
                {
                    var owner = new Window { Width = 900, Height = 640, RequestedThemeVariant = variant };
                    owner.Show();
                    Dispatcher.UIThread.RunJobs();

                    // dateService 必须给（null 会静默不刷新）；Show 后显式 Reload 补一轮查询。
                    // 主题设在弹层窗体上（独立 TopLevel，不继承 owner 的 RequestedThemeVariant）。
                    var controller = new StatusPopoverController(owner, null, SiteData.TodayForPopover(), null, new DateRangeService());
                    controller.Show();
                    controller.WindowForTest!.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    _ = controller.ViewModel.ReloadAsync();
                    DispatcherPump.PumpUntil(() => controller.ViewModel.TodayTokensText is not null && controller.ViewModel.TodayTokensText != "—");
                    Dispatcher.UIThread.RunJobs();

                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    var frame = controller.WindowForTest!.CaptureRenderedFrame();
                    Assert.NotNull(frame);

                    // 弹层窗体四周是透明落白的阴影留白（headless 合成为白底），裁到卡片本体再留档
                    ChartTestHelpers.SaveArchive(CropTo(frame!, 39, 23, 420, 546), ArchiveDir, $"tray-{locale}-{theme}.png");

                    controller.Hide();
                    owner.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            Translations.Instance.SetLanguage(original); // 进程级单例，恢复基线
        }
    }

    /// <summary>
    /// 位图矩形裁剪（弹层留档去透明落白留白用）：CopyPixels 到缓冲再写回 WriteableBitmap，
    /// 保持源格式与 DPI。
    /// </summary>
    private static Avalonia.Media.Imaging.Bitmap CropTo(Avalonia.Media.Imaging.Bitmap source, int x, int y, int width, int height)
    {
        var rect = new PixelRect(x, y, width, height);
        var stride = width * (source.Format!.Value.BitsPerPixel / 8);
        var buffer = new byte[stride * height];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            source.CopyPixels(rect, pin.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            pin.Free();
        }

        var cropped = new Avalonia.Media.Imaging.WriteableBitmap(
            new PixelSize(width, height), new Avalonia.Vector(96, 96), source.Format!.Value);
        using (var l = cropped.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, l.Address, buffer.Length);
        }

        return cropped;
    }
}
