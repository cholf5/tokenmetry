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

    /// <summary>构建单个淡入动画（Opacity 0→1）。</summary>
    public static Animation FadeIn(double durationMs, double delayMs = 0) => new()
    {
        Duration = TimeSpan.FromMilliseconds(durationMs),
        Delay = TimeSpan.FromMilliseconds(delayMs),
        Easing = EaseOut,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
        },
    };

    /// <summary>给一批可视元素跑 stagger 淡入（各元素 Delay = i×intervalMs）。</summary>
    public static void StaggerFadeIn(IReadOnlyList<Visual> items, double durationMs, double intervalMs)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            item.Opacity = 0;
            _ = FadeIn(durationMs, i * intervalMs).RunAsync(item);
        }
    }
}
