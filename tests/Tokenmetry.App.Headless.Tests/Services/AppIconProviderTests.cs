using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Services;

/// <summary>
/// 真实图标提取端到端（仅 macOS；其它平台直接返回）：系统 Terminal.app 必定存在且带 .icns，
/// 用它验证 plutil + sips + Avalonia Bitmap 这条链在 headless 环境里可用。
/// </summary>
public sealed class AppIconProviderTests
{
    [AvaloniaFact]
    public async Task Real_provider_extracts_system_terminal_icon_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        const string terminal = "/System/Applications/Utilities/Terminal.app";
        if (!Directory.Exists(terminal))
        {
            return;
        }

        var icon = await AppIconProvider.Shared.GetIconAsync("test:system-terminal", terminal);

        Assert.NotNull(icon);
        Assert.True(icon!.Size.Width > 0);
        Assert.True(icon.Size.Height > 0);
    }
}
