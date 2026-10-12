using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using ScottPlot;
using CCUsage.App.DependencyInjection;
using CCUsage.App.ViewModels;
using CCUsage.Core.Services;

namespace CCUsage.App.Views;

/// <summary>
/// [SPIKE D-002] ScottPlot.Avalonia 集成 spike 临时页：
/// 30 天成本趋势 + 缩放/平移（AvaPlot 默认交互）+ Crosshair/Tooltip 悬停原型。
/// 正式集成（v0.4 Dashboard 趋势卡）时会被替换或删除，不复用为产品代码。
/// </summary>
public partial class TrendSpikeView : UserControl
{
    /// <summary>悬停拾取半径（屏幕像素），与官方 Avalonia demo ShowValueUnderMouseWindow 一致。</summary>
    private const double MaxHoverDistance = 15;

    private ScottPlot.Plottables.Scatter? _scatter;
    private ScottPlot.Plottables.Crosshair? _crosshair;
    private ScottPlot.Plottables.Tooltip? _tooltip;

    public TrendSpikeView()
    {
        InitializeComponent();

        var vm = new TrendSpikeViewModel(AppServices.Get<CacheService>());
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrendSpikeViewModel.Points))
            {
                RebuildPlot();
            }
        };
        Loaded += (_, _) => RebuildPlot();
        Loaded += OnLoadedCaptureForVerification; // [SPIKE D-002] 自动化验证钩子，见方法注释
    }

    private void RebuildPlot()
    {
        var vm = (TrendSpikeViewModel)DataContext!;
        var points = vm.Points;

        TrendPlot.Plot.Clear();
        _scatter = null;
        _crosshair = null;
        _tooltip = null;

        if (points.Count == 0)
        {
            TrendPlot.Refresh();
            return;
        }

        // x 轴用 OADate 数值（ScottPlot 5 的日期数值约定），配合 DateTimeTicksBottom 出日期刻度。
        double[] xs = points.Select(p => p.Date.ToOADate()).ToArray();
        double[] ys = points.Select(p => p.CostUsd).ToArray();
        _scatter = TrendPlot.Plot.Add.Scatter(xs, ys);
        _scatter.LegendText = "cost USD/day";
        _scatter.LineWidth = 2;
        _scatter.MarkerSize = 5;

        _crosshair = TrendPlot.Plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _crosshair.MarkerShape = MarkerShape.OpenCircle;
        _crosshair.MarkerSize = 12;

        _tooltip = TrendPlot.Plot.Add.Tooltip(0, 0, string.Empty, 0, 0);
        _tooltip.IsVisible = false;
        _tooltip.LabelPadding = 6;

        TrendPlot.Plot.Title("[SPIKE] 30-day cost trend (real facts)");
        TrendPlot.Plot.YLabel("USD / day");
        TrendPlot.Plot.XLabel("Date");
        TrendPlot.Plot.Axes.DateTimeTicksBottom();

        TrendPlot.Refresh();
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scatter is null)
        {
            return;
        }

        // 官方 Avalonia demo（ShowValueUnderMouseWindow）的换算方式：
        // e.GetPosition(control) → Pixel → Plot.GetCoordinates → plottable.GetNearestX。
        var pos = e.GetPosition(TrendPlot);
        Pixel mousePixel = new(pos.X, pos.Y);
        UpdateHoverAt(TrendPlot.Plot.GetCoordinates(mousePixel));
        TrendPlot.Refresh();
    }

    private void OnPlotPointerExited(object? sender, PointerEventArgs e)
    {
        HideHover();
        TrendPlot.Refresh();
    }

    /// <summary>按数据坐标更新悬停视觉；鼠标路径先经 Plot.GetCoordinates 换算后调用同一方法。</summary>
    private void UpdateHoverAt(Coordinates targetLocation)
    {
        if (_scatter is null || _crosshair is null || _tooltip is null)
        {
            return;
        }

        DataPoint nearest = _scatter.GetNearestX(targetLocation, TrendPlot.Plot.LastRender, (float)MaxHoverDistance);

        var vm = (TrendSpikeViewModel)DataContext!;
        if (nearest.IsReal && nearest.Index >= 0 && nearest.Index < vm.Points.Count)
        {
            var point = vm.Points[nearest.Index];
            _crosshair.IsVisible = true;
            _crosshair.Position = nearest.Coordinates;

            var limits = TrendPlot.Plot.Axes.GetLimits();
            double yRange = limits.YRange.Max - limits.YRange.Min;
            // ScottPlot 的 Label 对齐语义是"锚点在气泡的哪个角"：UpperLeft=气泡自锚点向下延伸，
            // LowerLeft=向上延伸。故点在上半区用 UpperLeft 让气泡向下挂，避免顶出数据区被裁剪。
            _tooltip.IsVisible = true;
            _tooltip.TipLocation = nearest.Coordinates;
            _tooltip.LabelAlignment = nearest.Coordinates.Y > limits.YRange.Min + yRange * 0.5
                ? Alignment.UpperLeft
                : Alignment.LowerLeft;
            _tooltip.LabelLocation = nearest.Coordinates;
            _tooltip.LabelText = $"{point.Date:yyyy-MM-dd}\n${point.CostUsd:F2}\n{point.Tokens:N0} tok";

            HoverText.Text = $"{point.Date:yyyy-MM-dd}: ${point.CostUsd:F2}";
        }
        else
        {
            HideHover();
        }
    }

    private void HideHover()
    {
        if (_crosshair is not null)
        {
            _crosshair.IsVisible = false;
        }

        if (_tooltip is not null)
        {
            _tooltip.IsVisible = false;
        }

        HoverText.Text = "悬停查看数值";
    }

    /// <summary>
    /// [SPIKE D-002] 自动化验证钩子：macOS 截屏（screencapture）依赖屏幕录制 TCC 权限，CI / 终端里常被拒。
    /// 设置环境变量后由应用进程自己离屏渲染证据图（均为仓库外路径），不依赖任何系统权限：
    /// - CCUSAGE_SPIKE_CAPTURE=/tmp/xx.png → ScottPlot 对同一 Plot 对象离屏渲染（Skia），
    ///   并在峰值数据点合成一次悬停（走 UpdateHover 同一条代码路径）再渲染一张 *.hover.png，
    ///   用于自动化验证 Crosshair / Tooltip 的渲染效果。
    /// - CCUSAGE_SPIKE_CAPTURE_WINDOW=/tmp/x.png → Avalonia RenderTargetBitmap 渲染本控件视觉树。
    /// 未设置环境变量时行为不变；正式集成时随本页一起删除。
    /// </summary>
    private async void OnLoadedCaptureForVerification(object? sender, EventArgs e)
    {
        try
        {
            var plotPng = Environment.GetEnvironmentVariable("CCUSAGE_SPIKE_CAPTURE");
            if (string.IsNullOrWhiteSpace(plotPng))
            {
                return;
            }

            await Task.Delay(500); // 等 RebuildPlot 完成后再渲染
            TrendPlot.Plot.SavePng(plotPng, 768, 420);

            var vm = (TrendSpikeViewModel)DataContext!;
            if (_scatter is not null && vm.Points.Count > 0)
            {
                // 合成悬停：在成本峰值点走与鼠标移动完全相同的拾取与渲染路径
                //（鼠标路径仅多一步 Pixel→Coordinates 换算）。
                var peak = vm.Points.OrderByDescending(p => p.CostUsd).First();
                UpdateHoverAt(new Coordinates(peak.Date.ToOADate(), peak.CostUsd));
                TrendPlot.Refresh();
                TrendPlot.Plot.SavePng(plotPng + ".hover.png", 768, 420);
                HideHover();
                TrendPlot.Refresh();
            }

            // Avalonia 视觉树离屏渲染：证明活动窗口内的真实 AvaPlot 控件实例绘出了图表。
            //（注意：Avalonia 11+ 由组合器逐 Visual 调 Render，对父级调 Render 不会递归子树，
            // 因此这里直接渲染 AvaPlot 自身；页面级证据以人工目检为准。）
            var windowPng = Environment.GetEnvironmentVariable("CCUSAGE_SPIKE_CAPTURE_WINDOW");
            if (!string.IsNullOrWhiteSpace(windowPng))
            {
                await Task.Delay(1500); // 等首帧布局与渲染完成
                var size = TrendPlot.Bounds;
                if (size.Width > 1 && size.Height > 1)
                {
                    using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new Avalonia.PixelSize((int)size.Width, (int)size.Height));
                    using (var ctx = rtb.CreateDrawingContext())
                    {
                        TrendPlot.Render(ctx);
                    }

                    rtb.Save(windowPng, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }

            // 状态栏文本（数据行数 / 加载耗时）写进 stdout 日志，作为性能观察证据。
            var vmStatus = ((TrendSpikeViewModel)DataContext!);
            Console.WriteLine($"[SPIKE] status={vmStatus.Status}; range={vmStatus.DataRangeText}; " +
                              $"points={vmStatus.Points.Count}");
        }
        catch
        {
            // 验证钩子失败不影响应用运行（spike 证据不足时以人工验证兜底）
        }
    }
}
