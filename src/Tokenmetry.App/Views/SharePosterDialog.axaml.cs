using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// 海报生成对话框（P5）：独立窗口。窗口本身就是渲染宿主——海报子树挂在里面才拿得到样式、
/// 主题和图表初始化，导出时直接对子树根光栅化。
/// </summary>
public partial class SharePosterDialog : Window
{
    /// <summary>
    /// 剪贴板位图必须活到粘贴那一刻：<c>Clipboard.SetBitmapAsync</c> 在 Windows/X11 上是延迟提供
    /// （真正的 HGLOBAL/字节在别的应用粘贴时才取），方法返回就 Dispose 会得到「提示已复制、粘贴为空」。
    /// 因此<b>不在关窗时释放</b>——用户可能复制后关掉窗口再粘贴。只在下次复制替换时释放上一张，
    /// 同时最多多占一张 2× 海报的内存（约 16MB，设计风险表里可接受）。
    /// </summary>
    private Bitmap? _clipboardBitmap;

    // —— 预览缩放（默认适配全貌 + Ctrl/⌘+滚轮放大 + 双击复位）——
    // 预览的职责是判断样式/主题/版式的整体构图，100% + 滚动条等于隔着钥匙孔看构图，fit 是默认态；
    // 放大只服务于「看细节」，因此不做滑杆/百分比/工具条，手势足够（设计 §8 预览缩放比留给实现者）。

    /// <summary>海报当前「适配预览区」的缩放 = min(预览区/海报自然尺寸)，预览区或海报换尺寸即重算。</summary>
    private double _fitScale = 1.0;

    /// <summary>用户缩放倍率（1 = fit；滚轮累乘、双击复位），有效缩放 = fit × 倍率。</summary>
    private double _userZoom = 1.0;

    /// <summary>上次应用到 <see cref="PreviewHost"/> 的缩放，防止布局回调里同值反复应用成反馈环。</summary>
    private double _appliedScale;

    /// <summary>滚轮每一齿的缩放步长（指数底数，触控板的细步长也平滑）。</summary>
    private const double WheelZoomStep = 1.1;

    /// <summary>用户缩放上限：fit ≈ 0.3 的典型预览区下有效缩放到 ~1.5×，读清小字绰绰有余。</summary>
    private const double MaxUserZoom = 5.0;

    private const double ScaleEpsilon = 0.0005;

    /// <summary>
    /// fit 的微内缩：fit 绑定轴的渲染宽度 = 自然宽 ×(可用/自然宽)，除乘往返可能溢出 1 ulp，
    /// Auto 滚动条会为这 1e-13 闪现——0.1%（亚像素）买断这个毛刺，肉眼不可见。
    /// </summary>
    private const double FitInset = 0.999;

    public SharePosterDialog()
    {
        InitializeComponent();
        // Esc 关闭：独立窗口的关闭语义之一（设计 §9.1 把 Esc/关闭列为选独立窗口的代价）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        // 关窗解绑：VM 订阅了与应用同生命周期的 FilterStateService（数据范围筛选回写），
        // 不退订会把整棵 VM 连同海报预览钉在进程里
        Closed += (_, _) => ViewModel?.Detach();

        // 缩放手势挂在 ScrollViewer 上（海报内容的滚轮/双击都冒泡到这里）。TrendChart 已禁
        // AvaPlot 输入并放行滚轮，但这里用 handledEventsToo 兜底：预览里没有任何子元素该吃掉
        // 这两个手势，哪怕未来某个子控件开始 handled，也不该破坏「看细节」的入口。
        PreviewScroll.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        PreviewScroll.AddHandler(DoubleTappedEvent, OnPreviewDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        // fit 的两个自变量各自变化时重算：预览区尺寸（窗口缩放）与海报自然尺寸（换样式/版式/数据）
        PreviewScroll.SizeChanged += (_, _) => UpdatePreviewScale();
        PreviewView.SizeChanged += (_, _) => UpdatePreviewScale();
    }



    private SharePosterDialogViewModel? ViewModel => DataContext as SharePosterDialogViewModel;

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>复制到剪贴板；平台不支持时降级为提示，不静默失败。</summary>
    public async Task CopyAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var t = Translations.Instance;
        try
        {
            var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
            var ok = await SharePosterExporter.CopyToClipboardAsync(Clipboard, bitmap);
            if (ok)
            {
                // 延迟提供语义：位图必须留着（见字段注释），替换上一张并等窗口关闭时释放
                _clipboardBitmap?.Dispose();
                _clipboardBitmap = bitmap;
            }
            else
            {
                bitmap.Dispose();
            }

            StatusText.Text = ok ? t.SharePosterCopied : t.SharePosterCopyFailed;
            if (ok)
            {
                FlashCopied();
            }
        }
        catch (Exception ex)
        {
            // 渲染/剪贴板都可能抛（显卡或平台差异）：如实报错，不静默
            StatusText.Text = string.Format(t.SharePosterCopyFailedFormat, ex.Message);
        }
    }

    /// <summary>主按钮按下后 2 秒内显示「已复制」，随后复原（设计 §3.11 的即时反馈）。</summary>
    private void FlashCopied()
    {
        var t = Translations.Instance;
        CopyButton.Content = t.SharePosterCopied;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (CopyButton is not null)
                {
                    CopyButton.Content = t.SharePosterCopy;
                }
            },
            TimeSpan.FromSeconds(2));
    }

    /// <summary>保存为 PNG；用户取消对话框时不提示。</summary>
    public async Task SaveAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var t = Translations.Instance;
        try
        {
            using var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
            var path = await SharePosterExporter.SaveAsync(this, bitmap);
            StatusText.Text = path is null ? string.Empty : string.Format(t.SharePosterSavedFormat, path);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(t.SharePosterSaveFailedFormat, ex.Message);
        }
    }

    /// <summary>
    /// 重算 fit 缩放（预览区 / 海报自然尺寸取小者）并应用。挂两个 <c>SizeChanged</c>（预览区、
    /// 海报视图）进来；布局早期一边还没尺寸时直接跳过，等另一边触发时自然会算对。
    /// fit 读的是 <see cref="PreviewScroll"/> 自身尺寸而非 Viewport——Viewport 会随滚动条出现缩小，
    /// 那是缩放的结果不是原因，用它算会闭环抖动；自身尺寸在滚动条出现前后不变。
    /// </summary>
    private void UpdatePreviewScale()
    {
        var natural = PreviewView.Bounds;
        var available = PreviewScroll.Bounds;
        if (natural.Width < 1 || natural.Height < 1 || available.Width < 1 || available.Height < 1)
        {
            return;
        }

        var fit = Math.Min(available.Width / natural.Width, available.Height / natural.Height) * FitInset;
        if (fit <= 0 || double.IsNaN(fit) || double.IsInfinity(fit))
        {
            return;
        }

        _fitScale = fit;
        ApplyScale();
    }

    private void ApplyScale()
    {
        var scale = _fitScale * _userZoom;
        if (Math.Abs(scale - _appliedScale) < ScaleEpsilon)
        {
            return;
        }

        _appliedScale = scale;
        PreviewHost.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl（macOS 触控板捏合在系统层就是 Ctrl+滚轮）或 ⌘ 才缩放；不带修饰键保留原生滚动
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0)
        {
            return;
        }

        ZoomAt(e.GetCurrentPoint(PreviewScroll).Position, Math.Pow(WheelZoomStep, e.Delta.Y));
        e.Handled = true;
    }

    /// <summary>
    /// 以光标为锚点缩放：缩放前后光标下的内容点保持原地，否则每滚一齿视图就朝固定角落漂，
    /// 「放大看某处」根本看不住那个地方。逐轴处理——fit 时超宽轴由居中兜着（无偏移可加），
    /// 放大后才进入滚动状态；复位回 fit 的轴偏移归零交给居中。
    /// </summary>
    private void ZoomAt(Point anchorInViewport, double factor)
    {
        var natural = PreviewView.Bounds;
        if (natural.Width < 1 || natural.Height < 1)
        {
            return;
        }

        var oldScale = _fitScale * _userZoom;
        var newScale = Math.Clamp(oldScale * factor, _fitScale, _fitScale * MaxUserZoom);
        var ratio = newScale / oldScale;
        if (Math.Abs(ratio - 1) < ScaleEpsilon)
        {
            return;
        }

        // 锚点换算到内容坐标：滚动轴加回偏移；居中轴扣掉居中留白（此态偏移恒 0）
        var viewport = PreviewScroll.Viewport;
        var offset = PreviewScroll.Offset;
        var renderedX = natural.Width * oldScale;
        var renderedY = natural.Height * oldScale;
        var contentX = anchorInViewport.X + (renderedX > viewport.Width ? offset.X : -(viewport.Width - renderedX) / 2);
        var contentY = anchorInViewport.Y + (renderedY > viewport.Height ? offset.Y : -(viewport.Height - renderedY) / 2);

        _userZoom = newScale / _fitScale;
        ApplyScale();
        PreviewScroll.UpdateLayout(); // 新的滚动范围要等布局出来，Offset 才能按新范围正确 clamp

        // 新状态下仍小于视口的轴继续居中（偏移 0）；超出视口的轴把锚点内容点按比例拉回光标下
        var renderedNewX = natural.Width * newScale;
        var renderedNewY = natural.Height * newScale;
        PreviewScroll.Offset = new Vector(
            renderedNewX > viewport.Width ? Math.Max(0, contentX * ratio - anchorInViewport.X) : 0,
            renderedNewY > viewport.Height ? Math.Max(0, contentY * ratio - anchorInViewport.Y) : 0);
    }

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e)
    {
        ResetToFit();
        e.Handled = true;
    }

    /// <summary>复位回 fit：倍率归一，偏移交给居中（fit 态内容必不超出视口，clamp 即归零）。</summary>
    internal void ResetToFit()
    {
        if (Math.Abs(_userZoom - 1.0) < ScaleEpsilon)
        {
            return;
        }

        _userZoom = 1.0;
        ApplyScale();
        PreviewScroll.UpdateLayout();
        PreviewScroll.Offset = new Vector(0, 0);
    }
}
