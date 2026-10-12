using Avalonia;
using Avalonia.Styling;

namespace CCUsage.App.Services;

public interface IThemeService
{
    /// <summary>把主题偏好（light / dark / system）应用到全局 ThemeVariant，system 跟随操作系统并随系统切换自动更新。</summary>
    void Apply(string? themePreference);
}

public sealed class ThemeService : IThemeService
{
    public void Apply(string? themePreference)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = themePreference?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
