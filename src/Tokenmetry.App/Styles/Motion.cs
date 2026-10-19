using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;

namespace Tokenmetry.App.Styles;

/// <summary>
/// 动效常量与代码侧动画基建（motion design）。时长与 Tokens.axaml 的 AppMotion*Ms
/// 资源值镜像（DesignTokenTests 固化同步）；缓动统一 CubicEaseOut（克制取向）。
/// </summary>
public static class Motion
{
    public const double FastMs = 120;
    public const double NormalMs = 180;
    public const double SlowMs = 240;

    /// <summary>统一进场缓动（ease-out）。</summary>
    public static Easing EaseOut { get; } = new CubicEaseOut();

    /// <summary>构建单个淡入动画（Opacity 0→1）。FillMode Both：stagger 延迟窗内保持起帧 0、
    /// 结束后保持终帧——配合 <see cref="RunFadeIn"/> 的本地终值 1，两个方向都不闪。</summary>
    public static Animation FadeIn(double durationMs, double delayMs = 0) => new()
    {
        Duration = TimeSpan.FromMilliseconds(durationMs),
        Delay = TimeSpan.FromMilliseconds(delayMs),
        Easing = EaseOut,
        FillMode = FillMode.Both,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
        },
    };

    /// <summary>
    /// 淡入并落定（契约）：动画完成后 Avalonia 把属性回落到本地值——若本地值停在起帧 0，
    /// 淡入结束的瞬间元素会消失（用户实测的「DonutChart 中心数字扫入后消失」）。本方法先压
    /// 本地 0 防首帧闪现，启动动画后同帧把本地值置终值 1（同帧两次赋值间无渲染，不闪）；
    /// 播放期间动画值驱动，结束后回落到 1。
    /// </summary>
    public static void RunFadeIn(Visual item, double durationMs, double delayMs = 0)
    {
        item.Opacity = 0;
        _ = FadeIn(durationMs, delayMs).RunAsync(item);
        item.Opacity = 1;
    }

    /// <summary>给一批可视元素跑 stagger 淡入（各元素 Delay = i×intervalMs）。</summary>
    public static void StaggerFadeIn(IReadOnlyList<Visual> items, double durationMs, double intervalMs)
    {
        for (var i = 0; i < items.Count; i++)
        {
            RunFadeIn(items[i], durationMs, i * intervalMs);
        }
    }
}
