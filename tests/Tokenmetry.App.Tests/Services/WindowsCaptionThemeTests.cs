using Avalonia.Styling;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// D-026：Windows 原生标题栏深色声明。只锁决策、回落与跳过条件——真实 DWM 效果
/// 无法在 headless/CI 断言，真机目检是唯一有效验收（同 D-018 标题栏图标纪律）。
/// </summary>
public class WindowsCaptionThemeTests
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeLegacy = 19;

    private static WindowsCaptionTheme Create(
        List<(IntPtr Hwnd, int Attribute, int Value)> calls,
        Func<int, bool>? accepted = null,
        bool isWindows = true,
        string? headless = null)
    {
        accepted ??= _ => true;
        return new WindowsCaptionTheme(
            (hwnd, attribute, value) =>
            {
                calls.Add((hwnd, attribute, value));
                return accepted(attribute);
            },
            () => isWindows,
            _ => headless);
    }

    [Fact]
    public void Dark_variant_declares_immersive_dark_mode()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls);

        theme.Apply(new IntPtr(0x1234), ThemeVariant.Dark);

        calls.Should().ContainSingle()
            .Which.Should().Be((new IntPtr(0x1234), ImmersiveDarkMode, 1));
    }

    [Fact]
    public void Light_variant_clears_immersive_dark_mode()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls);

        theme.Apply(new IntPtr(0x1234), ThemeVariant.Light);

        calls.Should().ContainSingle()
            .Which.Should().Be((new IntPtr(0x1234), ImmersiveDarkMode, 0));
    }

    [Fact]
    public void Zero_handle_is_skipped()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls);

        theme.Apply(IntPtr.Zero, ThemeVariant.Dark);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void Non_windows_host_is_skipped()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls, isWindows: false);

        theme.Apply(new IntPtr(0x1234), ThemeVariant.Dark);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void Headless_host_is_skipped()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls, headless: "1");

        theme.Apply(new IntPtr(0x1234), ThemeVariant.Dark);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void Rejected_current_attribute_falls_back_to_legacy()
    {
        var calls = new List<(IntPtr Hwnd, int Attribute, int Value)>();
        var theme = Create(calls, accepted: attribute => attribute == ImmersiveDarkModeLegacy);

        theme.Apply(new IntPtr(0x1234), ThemeVariant.Dark);

        calls.Select(c => c.Attribute).Should().Equal(ImmersiveDarkMode, ImmersiveDarkModeLegacy);
        calls.Should().OnlyContain(c => c.Value == 1);
    }
}
