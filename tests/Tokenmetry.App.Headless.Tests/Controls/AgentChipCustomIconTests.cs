using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Tokenmetry.App.Controls;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// Task 7：插件自定义图标的 chip 契约。附加属性 <see cref="AgentChip.CustomIconProperty"/> 非 null
/// 时给 Border 挂 agent-custom 伪类（SharedStyles 据此把内层 Image.agent-logo 的 Source 绑到该属性、
/// 隐藏字形），null 时清除——内置 Agent 的 agent-* 品牌样式因此完全不受影响。
/// 断言语言/主题无关（伪类名 + 图片可见性与源同一性）。
/// </summary>
public class AgentChipCustomIconTests
{
    [AvaloniaFact]
    public void Custom_icon_sets_pseudo_class_and_clears_when_null()
    {
        var chip = new Border { Classes = { "agent-chip" } };
        AgentChip.SetCustomIcon(chip, new DrawingImage());
        Assert.Contains("agent-custom", chip.Classes);

        AgentChip.SetCustomIcon(chip, null);
        Assert.DoesNotContain("agent-custom", chip.Classes);
    }

    [AvaloniaFact]
    public void Builtin_chip_stays_without_custom_pseudo_class()
    {
        var chip = new Border { Classes = { "agent-chip" } };
        AgentChip.SetLogo(chip, "claudecode");
        Assert.Contains("agent-claudecode", chip.Classes);
        Assert.DoesNotContain("agent-custom", chip.Classes);
    }

    [AvaloniaFact]
    public void Custom_icon_style_shows_the_bound_image()
    {
        var icon = new DrawingImage();
        var image = new Image { Classes = { "agent-logo" } };
        var glyph = new TextBlock { Text = "✳" };
        var chip = new Border
        {
            Classes = { "agent-chip", "agent-generic" },
            Child = new Panel { Children = { glyph, image } },
        };

        var window = new Window { Width = 200, Height = 200, Content = chip };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AgentChip.SetCustomIcon(chip, icon);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(image.IsVisible);
        Assert.Same(icon, image.Source);
        Assert.False(glyph.IsVisible);
    }
}
