using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Views;

/// <summary>
/// 分享海报视图：纯展示，数据来自 <see cref="SharePosterViewModel"/> 的不可变快照。
/// 主题由请求决定而不是跟随应用——海报可能在应用处于夜间时按日间导出，所以整棵子树挂在一个
/// <see cref="ThemeVariantScope"/> 下，否则取到的会是应用当前主题的令牌（品牌标识尤其明显：
/// Light 档是黑标，Dark 档是白标，反色就会看不见）。
/// <para>
/// 版式（竖版 / 横版）决定画布宽度与六个面板的栅格落位：面板本体在 XAML 里只声明一次
/// （声明的是竖版落位），横版由这里改栅格定义 + 重排 Grid.Row/Column，不复制第二套标记。
/// </para>
/// </summary>
public partial class SharePosterView : UserControl
{
    /// <summary>竖版画布宽度（导出按 2× 光栅化）。</summary>
    public const double PortraitWidth = 1080;

    /// <summary>
    /// 信息卡画布宽（900）：有意偏离竖版 1080。信息卡内容只是一摞单列信息流，1080 下
    /// 内容高度 ~880，排出来是横扁一条，KPI 列间与列表条中段全是气口。下限由 KPI 脚注
    /// 决定：en/de 的缓存占比脚注 14px 实测 ~227/~240px，默认页边距 58 时 900 给每列
    /// ~247px 恰好不折行，再窄 en/de 就折——中文/日文等全都没有贴边问题。
    /// </summary>
    public const double TrayWidth = 900;

    /// <summary>横版画布宽度（设计 §3.3：1800 宽，导出 3600）。</summary>
    public const double LandscapeWidth = 1800;

    /// <summary>热力格子基准边长 / 间隙（格距 4 = Margin 2×2；满 52 周的尺度，列宽随周数收放，见 SharePosterHeatmap.ColumnWidthFor）。</summary>
    public const int HeatCellSize = 27;
    public const int HeatCellGap = 4;

    /// <summary>节律格子边长 / 间隙 / 列槽宽（24 列在 1800 横版下的尺度和月份标签行对列规则同热力基底）。</summary>
    public const int RhythmCellSize = 60;
    public const int RhythmCellGap = 6;
    public const int RhythmColumnWidth = 66;

    /// <summary>
    /// 里程碑英雄展示字号（修订九沿定稿尺寸）：HTML 稿 clamp 上限 116@760 映射到 1080 版心
    /// ≈ 174，取 176（= 44 叙事值的整 4 倍，字号体系内的展示档）——设计定的尺寸，不是
    /// 「能放多大放多大」。溢出守卫只会往下收（见 <see cref="HeroFontSizeFor"/>）。
    /// 与 XAML <c>milestone-hero</c> 样式的 Value 必须一致，headless 测试锁同值。
    /// </summary>
    public const double MilestoneHeroFontSize = 176;

    /// <summary>
    /// 里程碑英雄锁定值的「尾数 → 单位」间距：尾数与绿色单位同排（HTML 稿 number/unit 锁定）。
    /// 英雄样式的右内边距（负字距补偿，14）也落在视觉间隙里，故此处取 8——补偿 + 间距 ≈ 稿子的
    /// 单位左距（10 × 1.5 = 15），不会因补偿而变宽。XAML 里英雄锁定值 <c>StackPanel</c> 的
    /// Spacing 字面量与本值必须一致（守卫预算扣减项），headless 测试以「锁定值不越版心」锁住。
    /// </summary>
    public const double MilestoneHeroUnitSpacing = 8;

    public SharePosterView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            ApplyRequestedTheme();
            ApplyLayout();
        };
        // 里程碑英雄溢出守卫在布局回调里跑：样式值（字族/字重/字距）生效、绑定文本就位之后
        // 才能量测，这是最可靠的时机（见 ApplyMilestoneHeroGuard）
        MilestoneRoot.LayoutUpdated += (_, _) => ApplyMilestoneHeroGuard();
        // 跟随应用：对话框开着时切应用主题，海报与投影跟着变（静态海报本身不需要重绘）
        ActualThemeVariantChanged += (_, _) =>
        {
            if (IsFollowingAppTheme)
            {
                ApplyPanelShadows(ActualThemeVariant);
            }
        };
    }

    /// <summary>挂树订阅语言切换（重译代码生成的格子文案）；脱树对称退订，见 OnTranslationsPropertyChanged。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
    }

    /// <summary>
    /// 语言切换重译：热力/节律网格是代码生成的（tooltip 与月份/星期标签在 build 时固化字符串），
    /// 不重跑就停在旧语言；XAML 直绑 Translations 的文案由绑定自动刷新。
    /// 与 Controls.Heatmap 的重译订阅同一模式（B-007）。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            ApplyHeatGrid();
            ApplyRhythmGrid();
        }
    }

    /// <summary>导出时要光栅化的节点（当前基底的根，不含外面用来适配预览的缩放容器）。</summary>
    public Control PosterRootControl =>
        (DataContext as SharePosterViewModel)?.Data.Request.Style switch
        {
            SharePosterStyle.Tray => TrayRoot,
            SharePosterStyle.Heatmap => HeatRoot,
            SharePosterStyle.Rhythm => RhythmRoot,
            SharePosterStyle.Milestone => MilestoneRoot,
            _ => PosterRoot,
        };

    /// <summary>信息卡样式走托盘基底（固定 900 窄版），总览样式走总览基底（1080 / 1800）——只看样式，与范围无关。</summary>
    public bool IsTrayStyle => (DataContext as SharePosterViewModel)?.IsTrayStyle ?? false;

    /// <summary>打卡热力图样式走热力基底（固定 1800 横版，能力表锁定）。</summary>
    public bool IsHeatmapStyle => (DataContext as SharePosterViewModel)?.IsHeatmapStyle ?? false;

    /// <summary>时段节律样式走节律基底（固定 1800 横版，能力表锁定）。</summary>
    public bool IsRhythmStyle => (DataContext as SharePosterViewModel)?.IsRhythmStyle ?? false;

    /// <summary>里程碑样式走叙事基底（固定 1080 竖版，能力表锁定）。</summary>
    public bool IsMilestoneStyle => (DataContext as SharePosterViewModel)?.IsMilestoneStyle ?? false;

    /// <summary>版式 → 画布宽度（纯函数，测试与导出共用）。</summary>
    public static double WidthFor(SharePosterLayout layout) =>
        layout == SharePosterLayout.Landscape ? LandscapeWidth : PortraitWidth;

    private SharePosterLayout CurrentLayout =>
        (DataContext as SharePosterViewModel)?.Data.Request.Layout ?? SharePosterLayout.Portrait;

    /// <summary>请求页边距（收口后）：五种基底上下左右统一取这一值，XAML 里的 Padding 只是设计期缺省。</summary>
    private double RequestMargin =>
        (DataContext as SharePosterViewModel)?.Data.Request.SafeMargin ?? SharePosterRequest.DefaultMargin;

    /// <summary>托盘构成条的三段宽度：按输入/输出/缓存 token 比例给星号（全 0 时等分）。</summary>
    private void ApplyTrayTypeBar()
    {
        var types = (DataContext as SharePosterViewModel)?.TrayTypes;
        var values = types is { Count: >= 3 }
            ? types.Take(3).Select(t => t.Value).ToArray()
            : [0d, 0d, 0d];
        var total = values.Sum();
        var widths = total > 0
            ? values.Select(v => (v / total).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "*")
            : ["*", "*", "*"];
        TrayTypeBar.ColumnDefinitions = new ColumnDefinitions(string.Join(",", widths));
    }

    private bool IsFollowingAppTheme =>
        ((DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp)
        == SharePosterTheme.FollowApp;

    private void ApplyRequestedTheme()
    {
        var theme = (DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp;
        if (theme == SharePosterTheme.FollowApp)
        {
            // 跟随应用 = 不钉主题，交给继承：应用切明暗时海报子树自己跟着换令牌
            ThemeScope.RequestedThemeVariant = null;
            ApplyPanelShadows(ActualThemeVariant);
            return;
        }

        var dark = theme == SharePosterTheme.Dark;
        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ApplyPanelShadows(dark ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    /// <summary>
    /// 日间海报的卡片投影（设计 §3.5 第 6 条）：浅底上卡片与页面底几乎同色，不分层会糊成一片。
    /// 夜间不给投影（深底上根本看不见，给了只是白白多一层像素）。投影颜色与浓度全在
    /// <c>AppPosterShadowColor</c> 令牌里，此处负责拼出 <see cref="DropShadowEffect"/>——XAML 无法
    /// 只对效果的颜色部分接 DynamicResource。
    /// <para>
    /// 用 Effect 而不是 <c>Border.BoxShadow</c> 是硬约束（上游 issue AvaloniaUI/Avalonia#20693，
    /// 截至 12.1.5 未修、无待合并 PR）：BoxShadow 的绘制在 Skia 立即路径走 DrawRectangle →
    /// RestoreCanvas()，DPI≠96 的 RenderTargetBitmap 上会把 DPI 缩放二次乘进其后第一次
    /// PushTransform——首个带影卡片之后整张海报按 DPI 平方放大错位，真机（Android/Skia）同样
    /// 复现，v0.4.0 的日间导出即中招（回归锁见导出测试）；Effect 不走该路径，预览与导出都正确。
    /// 升级 Avalonia 后若要换回 BoxShadow，先确认 #20693 已关闭。
    /// </para>
    /// </summary>
    private void ApplyPanelShadows(ThemeVariant variant)
    {
        // 资源查找覆盖两种时机：DataContextChanged 发生在挂树前（控件自身的资源链还没接到 App），
        // 这时回落到 Application 的资源宿主显式按变体取色。
        var found = TryGetResource("AppPosterShadowColor", variant, out var resource)
                    || (Application.Current?.TryGetResource("AppPosterShadowColor", variant, out resource) ?? false);
        DropShadowEffect? effect = null;
        if (found && resource is Color color && color.A > 0)
        {
            effect = new DropShadowEffect
            {
                Color = Color.FromRgb(color.R, color.G, color.B),
                Opacity = color.A / 255d,
                BlurRadius = 20,
            };
        }

        foreach (var panel in CardSurfaces())
        {
            panel.Effect = effect;
        }
    }

    private IEnumerable<Border> CardSurfaces() =>
        KpiGrid.Children.OfType<Border>().Concat(
            new Border[] { TrendPanel, AgentPanel, ModelPanel, TypePanel, ProjectPanel, CostPanel, HeatCard, RhythmCard })
        .Concat(MilestoneStats.Children.OfType<Border>())
        .Concat(new Border[] { MilestoneModelCard });

    /// <summary>
    /// 按版式重排栅格。两套版面的差别不只是换列宽：面板归属也换行（类型环形、成本趋势从第二排列
    /// 提到第一列/第二列），所以每行是一个独立 Grid，横版时把面板移到目标行里。
    /// <list type="bullet">
    /// <item>竖版（1080）：三行——趋势 1.45 / Agent 0.95；模型 / 类型 1:1；项目 / 成本 1:1。</item>
    /// <item>横版（1800）：两行——趋势 2.1 / Agent 1 / 类型 1；模型 1 / 项目 1 / 成本 1.2（设计 §3.3）。</item>
    /// </list>
    /// 竖版第一行偏离设计稿的 1.55/0.85 是有意的：190px 环形后面只剩 ~65px，图例被裁成「Z…」；
    /// 1.45/0.95 下趋势区 581px 与基准稿的 577 基本一致。
    /// 尺度差异（字号 / 图表高度 / 环形直径）由 <c>poster-root.land</c> 的样式组承担，这里只管布局。
    /// </summary>
    private void ApplyLayout()
    {
        // 页边距是请求参数（对话框滑杆可调）：五种基底上下左右统一，XAML 样式值只作设计期缺省
        var margin = new Thickness(RequestMargin);
        PosterRoot.Padding = margin;
        TrayRoot.Padding = margin;
        HeatRoot.Padding = margin;
        RhythmRoot.Padding = margin;
        MilestoneRoot.Padding = margin;

        if (IsTrayStyle)
        {
            // 托盘基底是固定 900 窄版：不受版式选项影响（对话框在信息卡样式下也禁了横版）。
            // 隐形的总览基底仍按竖版宽摆，它自己的栅格口径不变。
            TrayRoot.Width = TrayWidth;
            PosterRoot.Width = PortraitWidth;
            PosterRoot.Classes.Remove("land");
            ApplyTrayTypeBar();
            return;
        }

        if (IsHeatmapStyle)
        {
            // 热力基底横版（能力表只登记横版，版式选项对它无效），画布宽随窗口周数与页边距收放：
            // 窄/中范围脚注三格搬进卡片与日历并排（海报不再窄而长），长范围回到底部横排
            var weeks = (DataContext as SharePosterViewModel)?.Heat.Weeks ?? SharePosterHeatmap.MaxWeeks;
            HeatRoot.Width = SharePosterHeatmap.CanvasWidthFor(weeks, RequestMargin);
            var beside = SharePosterHeatmap.UseBesideStats(weeks);
            HeatStatsCol.IsVisible = beside;
            HeatStatsRow.IsVisible = !beside;
            ApplyHeatGrid();
            return;
        }

        if (IsRhythmStyle)
        {
            // 节律基底横版：画布随 7×24 网格与页边距收（与热力基底同一 chrome 口径），
            // 不钉满宽——钉满会让卡片右侧多出一段空带
            RhythmRoot.Width = SharePosterHeatmap.WeekdayGutter
                               + 24 * RhythmColumnWidth
                               + SharePosterHeatmap.CanvasChromeFor(RequestMargin);
            ApplyRhythmGrid();
            return;
        }

        if (IsMilestoneStyle)
        {
            // 叙事基底是固定 1080 竖版：纯排版，无栅格要重排（卡片投影走 ApplyPanelShadows 统一收）
            MilestoneRoot.Width = PortraitWidth;
            return;
        }

        var landscape = CurrentLayout == SharePosterLayout.Landscape;
        PosterRoot.Width = WidthFor(CurrentLayout);

        // 先换容器可见性、再把面板搬进目标容器：两种版式各带一套固定列定义的容器，
        // 运行期不动任何 ColumnDefinitions（Avalonia Grid 在定义数量变化时会踩测量缓存索引越界）。
        PortraitRow1.IsVisible = !landscape;
        PortraitRow2.IsVisible = !landscape;
        PortraitRow3.IsVisible = !landscape;
        LandscapeRow1.IsVisible = landscape;
        LandscapeRow2.IsVisible = landscape;

        if (landscape)
        {
            if (!PosterRoot.Classes.Contains("land"))
            {
                PosterRoot.Classes.Add("land");
            }

            Place(TrendPanel, LandscapeRow1, 0);
            Place(AgentPanel, LandscapeRow1, 1);
            Place(TypePanel, LandscapeRow1, 2);
            Place(ModelPanel, LandscapeRow2, 0);
            Place(ProjectPanel, LandscapeRow2, 1);
            Place(CostPanel, LandscapeRow2, 2);
        }
        else
        {
            PosterRoot.Classes.Remove("land");
            Place(TrendPanel, PortraitRow1, 0);
            Place(AgentPanel, PortraitRow1, 1);
            Place(ModelPanel, PortraitRow2, 0);
            Place(TypePanel, PortraitRow2, 1);
            Place(ProjectPanel, PortraitRow3, 0);
            Place(CostPanel, PortraitRow3, 1);
        }

        static void Place(Control panel, Grid row, int column)
        {
            // 面板在两次版式间换行：先从旧行摘下再挂到目标行（图表随之重建，见控件 OnAttachedToVisualTree）
            if (panel.Parent is Panel previous && !ReferenceEquals(previous, row))
            {
                previous.Children.Remove(panel);
            }

            if (!row.Children.Contains(panel))
            {
                row.Children.Add(panel);
            }

            Grid.SetRow(panel, 0);
            Grid.SetColumn(panel, column);
        }
    }

    /// <summary>守卫去重：量测入参没变就不重测（布局回调会反复进来，同一份数据只算一次）。</summary>
    private (string? Text, double BudgetWidth, FontFamily Family, FontWeight Weight, double Spacing)? _heroGuardKey;

    /// <summary>
    /// 里程碑英雄溢出收缩守卫：176 定字号遇上超长紧凑值（万亿级）时，实测超宽才等比缩小、
    /// 永不放大。接在 <see cref="MilestoneRoot"/> 的 LayoutUpdated 上——守卫要等样式值
    /// （字族/字重/字距）生效、绑定文本就位后才能量测，布局回调是最可靠的时机；换 VM（页边距滑杆、
    /// 筛选、样式）时字号串会变，回调必然再进来。
    /// <para>
    /// 预算按锁定值口径扣减（修订九：单位从英雄串里拆出来单独 56px 绿字，宽度与英雄字号无关）：
    /// 预算 = 版心 − 单位自然宽 − 锁定间距 − 负字距补偿（英雄样式右内边距补回尾字符负字距削掉的
    /// 量测宽，HeroFontSizeFor 的 TextLayout 宽不含这份补偿，预算要同步扣，否则锁定值可越界
    /// |字距|）。单位块隐藏（未达首档的全量数字）按 0 计。
    /// </para>
    /// </summary>
    private void ApplyMilestoneHeroGuard()
    {
        if (!IsMilestoneStyle || DataContext is not SharePosterViewModel)
        {
            return;
        }

        var unitWidth = MilestoneHeroUnit.IsVisible ? MilestoneHeroUnit.DesiredSize.Width : 0;
        var budget = PortraitWidth - 2 * RequestMargin - unitWidth - MilestoneHeroUnitSpacing
                     - Math.Abs(MilestoneHeroText.LetterSpacing);
        var key = (MilestoneHeroText.Text, budget, MilestoneHeroText.FontFamily,
            MilestoneHeroText.FontWeight, MilestoneHeroText.LetterSpacing);
        if (_heroGuardKey == key)
        {
            return;
        }

        _heroGuardKey = key;
        var size = HeroFontSizeFor(key.Text, budget,
            new Typeface(key.FontFamily, FontStyle.Normal, key.FontWeight), key.LetterSpacing);
        if (size >= MilestoneHeroFontSize)
        {
            // 未溢出：回到样式值（同为 176），不留本地值——样式的字号是唯一出处
            MilestoneHeroText.ClearValue(TextBlock.FontSizeProperty);
        }
        else
        {
            MilestoneHeroText.FontSize = size;
        }
    }

    /// <summary>
    /// 英雄字号（纯函数，测试共用）：以 <see cref="MilestoneHeroFontSize"/> 量测文本宽，超出版心才
    /// 等比收缩（半磅取整），否则原字号返回——永不放大，定尺寸是设计决定不是测量结果。
    /// </summary>
    public static double HeroFontSizeFor(string? text, double contentWidth, Typeface typeface, double letterSpacing)
    {
        if (string.IsNullOrEmpty(text) || contentWidth <= 0)
        {
            return MilestoneHeroFontSize;
        }

        using var layout = new TextLayout(text, typeface, MilestoneHeroFontSize, Brushes.Black,
            letterSpacing: letterSpacing);
        var scale = Math.Min(1, contentWidth / layout.Width);
        return Math.Floor(MilestoneHeroFontSize * scale * 2) / 2;
    }

    /// <summary>
    /// 热力图的月份标签行、星期标签列与按天格子：格子是数据形状（最多 52×7 = 364 个），
    /// 在 XAML 里逐个声明没有意义，按 VM 算好的 <see cref="SharePosterViewModel.Heat"/> 生成——
    /// 颜色一律走 Classes（heat-0..4）接 ThemeDictionaries 令牌，代码里不出现色值。
    /// 列槽宽随窗口周数收放（<see cref="SharePosterHeatmap.ColumnWidthFor"/>），月份标签行与格子
    /// 共用同一列划分，标签才能对上列。格子悬停挂 tooltip（预览活视图；导出位图不含浮层）。
    /// </summary>
    private void ApplyHeatGrid()
    {
        if (DataContext is not SharePosterViewModel vm)
        {
            return;
        }

        var heat = vm.Heat;
        var columnWidth = SharePosterHeatmap.ColumnWidthFor(heat.Weeks);
        var cellSize = columnWidth - HeatCellGap;

        // 月份标签：每列一个定宽槽，标签跨到下一个标签列（左对齐，海报没有右起排元素）
        var slots = string.Join(",", Enumerable.Repeat(columnWidth, heat.Weeks));
        HeatMonthRow.ColumnDefinitions = new ColumnDefinitions(slots);
        HeatMonthRow.Children.Clear();
        foreach (var label in heat.MonthLabels)
        {
            var text = new TextBlock
            {
                Classes = { "heat-month-label" },
                Text = label.Text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            };
            Grid.SetColumn(text, label.StartCol);
            if (label.Span > 1)
            {
                Grid.SetColumnSpan(text, label.Span);
            }

            HeatMonthRow.Children.Add(text);
        }

        // 星期标签列：周一起 7 行，只标一/三/五（同 GitHub 的密度），空行也占位对齐
        HeatWeekdayCol.Children.Clear();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            HeatWeekdayCol.Children.Add(new Border
            {
                Height = cellSize + HeatCellGap,
                Child = new TextBlock
                {
                    Classes = { "heat-weekday-label" },
                    Text = heat.WeekdayLabels[weekday] ?? string.Empty,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            });
        }

        // 按天格子：行主序（先星期后周列）与 BuildCells 的顺序一致
        HeatGrid.Columns = heat.Weeks;
        HeatGrid.Children.Clear();
        foreach (var cell in heat.Cells)
        {
            var border = new Border
            {
                Width = cellSize,
                Height = cellSize,
                Margin = new Thickness(HeatCellGap / 2d),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            border.Classes.Add(cell.IsBlank ? "heat-blank" : $"heat-{cell.Level}");
            if (cell.IsPeak)
            {
                border.Classes.Add("heat-peak");
            }

            // 范围外留白格不给 tooltip（CellTooltip 返回 null）；统一 chart-tip 装饰与时间页同款
            var tip = SharePosterHeatmap.CellTooltip(cell);
            if (tip is not null)
            {
                ChartTooltips.Set(border, tip);
            }

            HeatGrid.Children.Add(border);
        }
    }

    /// <summary>
    /// 节律矩阵的星期列、小时标签行与 7 × 24 格子：数据形状由 VM 算好的
    /// <see cref="SharePosterViewModel.Rhythm"/> 给出（纯计算在 <see cref="SharePosterRhythm"/>），
    /// 颜色同样走 heat-0..4 类接 ThemeDictionaries 令牌，峰值格复用 heat-peak 描边。
    /// 列槽宽固定为 <see cref="RhythmColumnWidth"/>，小时标签行与格子共用同一列划分；
    /// 格子悬停挂 tooltip（预览活视图；导出位图不含浮层）。
    /// </summary>
    private void ApplyRhythmGrid()
    {
        if (DataContext is not SharePosterViewModel vm)
        {
            return;
        }

        var rhythm = vm.Rhythm;

        // 星期列：周一起 7 行，只标一/三/五（与打卡热力图同密度），空行占位对齐
        RhythmWeekdayCol.Children.Clear();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            RhythmWeekdayCol.Children.Add(new Border
            {
                Height = RhythmCellSize + RhythmCellGap,
                Child = new TextBlock
                {
                    Classes = { "heat-weekday-label" },
                    Text = SharePosterHeatmap.WeekdayLabels()[weekday] ?? string.Empty,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            });
        }

        // 小时标签行：每两小时一个刻度（0..22），列槽与格子一一对应
        var slots = string.Join(",", Enumerable.Repeat(RhythmColumnWidth, 24));
        RhythmHourRow.ColumnDefinitions = new ColumnDefinitions(slots);
        RhythmHourRow.Children.Clear();
        foreach (var hour in Enumerable.Range(0, 24).Where(h => h % 2 == 0))
        {
            RhythmHourRow.Children.Add(new TextBlock
            {
                Classes = { "heat-weekday-label" },
                Text = hour.ToString("00", CultureInfo.InvariantCulture),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            });
            Grid.SetColumn(RhythmHourRow.Children[^1], hour);
        }

        // 格子：行主序（先星期后小时）与 Build 的顺序一致
        RhythmGrid.Columns = 24;
        RhythmGrid.Children.Clear();
        foreach (var cell in rhythm.Cells)
        {
            var border = new Border
            {
                Width = RhythmCellSize,
                Height = RhythmCellSize,
                Margin = new Thickness(RhythmCellGap / 2d),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            border.Classes.Add($"heat-{cell.Level}");
            if (cell.IsPeak)
            {
                border.Classes.Add("heat-peak");
            }

            ChartTooltips.Set(border, SharePosterRhythm.CellTooltip(cell));
            RhythmGrid.Children.Add(border);
        }
    }
}
