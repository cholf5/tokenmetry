using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Views;

/// <summary>
/// 分享海报视图（P3）：纯展示，数据来自 <see cref="SharePosterViewModel"/> 的不可变快照。
/// 主题由请求决定而不是跟随应用——海报可能在应用处于夜间时按日间导出，所以整棵子树挂在一个
/// <see cref="ThemeVariantScope"/> 下，否则取到的会是应用当前主题的令牌（品牌标识尤其明显：
/// Light 档是黑标，Dark 档是白标，反色就会看不见）。
/// </summary>
public partial class SharePosterView : UserControl
{
    public SharePosterView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyRequestedTheme();
    }

    /// <summary>海报版面自身宽度（导出按 2× 光栅化）。</summary>
    public const double PosterWidth = 1080;

    /// <summary>导出时要光栅化的节点（版面根，不含外面用来适配预览的缩放容器）。</summary>
    public Control PosterRootControl => PosterRoot;

    private void ApplyRequestedTheme()
    {
        var theme = (DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp;
        var dark = theme switch
        {
            SharePosterTheme.Dark => true,
            SharePosterTheme.Light => false,
            _ => Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
        };

        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
