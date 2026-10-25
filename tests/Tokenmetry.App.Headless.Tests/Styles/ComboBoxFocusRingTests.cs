using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// ComboBox 焦点环网页化（Q-014）：Semi 的 :focus 态无条件画主色描边 + 提亮底色 + 箭头变色，
/// 而 Avalonia 里选完下拉项后焦点留在 ComboBox 上、点击不可聚焦区域不转移焦点——顶栏筛选框
/// 选完一项蓝环会一直挂着，直到点了左侧页签这类可聚焦元素才消失（web 的 :focus-visible 语义
/// 下不会这样）。SharedStyles 以 <c>:focus:not(:focus-visible)</c> 覆写把非键盘来源的焦点压回
/// 默认观感；:focus-visible 由 InputElement 按 NavigationMethod 判定（仅 Tab/方向键点亮）。
/// 断言采用同窗双 ComboBox 基线对照（A 获焦 vs B 保持未聚焦），而非按资源键取期望值：
/// Semi 的部分画刷（如 ComboBoxIconFocusForeground）只活在 ControlTheme 作用域里，控件树
/// 的 TryFindResource/FindResource 不可见，逐键取值不可靠；同主题同窗口下两条解析路径一致，
/// 「与基线同 / 异」才是跨明暗变体稳定的契约面。
/// 1. 指针焦点（含选完下拉后的焦点回归路径）——背景/描边/箭头与未聚焦基线完全一致（环退场）；
/// 2. 键盘焦点（Tab / 方向键）——三者均偏离基线（Semi 焦点环原样保留，键盘可达性不受影响）。
/// </summary>
public class ComboBoxFocusRingTests
{
    private static (ComboBox A, ComboBox Baseline, Window Window) CreatePair()
    {
        var a = new ComboBox { MinWidth = 124 };
        var baseline = new ComboBox { MinWidth = 124 };
        var window = new Window
        {
            Width = 400,
            Height = 100,
            Content = new StackPanel { Children = { a, baseline } },
        };
        window.Show(); // 挂入视觉树，模板与 SharedStyles 覆写生效
        Dispatcher.UIThread.RunJobs();
        return (a, baseline, window);
    }

    private static PathIcon GlyphOf(ComboBox combo) =>
        combo.GetVisualDescendants().OfType<PathIcon>().First(p => p.Name == "DropDownGlyph");

    [AvaloniaFact]
    public void PointerFocus_LooksIdenticalToUnfocusedBaseline()
    {
        var (combo, baseline, window) = CreatePair();
        try
        {
            combo.Focus(NavigationMethod.Pointer);
            Dispatcher.UIThread.RunJobs();

            // 模态锁定：指针来源只点亮 :focus，不点亮 :focus-visible（覆写选择器的前提）
            Assert.True(combo.IsFocused);
            Assert.Contains(":focus", combo.Classes);
            Assert.DoesNotContain(":focus-visible", combo.Classes);

            // 环退场：背景/描边/箭头三属性与未聚焦基线完全一致
            Assert.Equal(baseline.BorderBrush, combo.BorderBrush);
            Assert.Equal(baseline.Background, combo.Background);
            Assert.Equal(GlyphOf(baseline).Foreground, GlyphOf(combo).Foreground);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void KeyboardFocus_KeepsSemiFocusRing()
    {
        var (combo, baseline, window) = CreatePair();
        try
        {
            combo.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            // 模态锁定：键盘来源同时点亮 :focus 与 :focus-visible
            Assert.True(combo.IsFocused);
            Assert.Contains(":focus", combo.Classes);
            Assert.Contains(":focus-visible", combo.Classes);

            // 环保留：Semi :focus 三属性全部偏离未聚焦基线
            Assert.NotEqual(baseline.BorderBrush, combo.BorderBrush);
            Assert.NotEqual(baseline.Background, combo.Background);
            Assert.NotEqual(GlyphOf(baseline).Foreground, GlyphOf(combo).Foreground);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
