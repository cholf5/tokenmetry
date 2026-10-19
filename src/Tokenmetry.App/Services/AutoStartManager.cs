using System;
using System.IO;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Tokenmetry.App.Services;

/// <summary>
/// 开机自启动注册（S-006）：把「当前可执行文件 + <see cref="AutoStartManager.LaunchArg"/> 参数」
/// 登记为用户登录启动项，三平台零额外依赖的原生机制——Windows = HKCU Run 键、
/// macOS = ~/Library/LaunchAgents plist（RunAtLoad）、Linux = XDG autostart .desktop。
/// <para><b>headless 测试红线</b>：SetEnabled 操作真实 OS 状态（注册表 / 全局 LaunchAgents 目录），
/// 测试只允许走 fakes 或内部纯构造函数（BuildWindowsRunValue / BuildLaunchAgentPlist /
/// BuildDesktopEntry，经 InternalsVisibleTo 断言内容），禁止对真实管理器调用 SetEnabled。</para>
/// </summary>
public interface IAutoStartManager
{
    /// <summary>注册（true）或注销（false）开机自启动；幂等，失败抛异常由调用方决定回滚/记日志。</summary>
    void SetEnabled(bool enabled);
}

public sealed class AutoStartManager : IAutoStartManager
{
    /// <summary>自启动注册附加的启动参数：App 据此静默启动（不弹主窗、常驻托盘，A-0 语义）。</summary>
    public const string LaunchArg = "--autostart";

    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValueName = "Tokenmetry";
    private const string MacLaunchAgentLabel = "com.tokenmetry.app";
    private const string LinuxDesktopFileName = "tokenmetry.desktop";

    public void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsSetEnabled(enabled);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacSetEnabled(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxSetEnabled(enabled);
        }
        else
        {
            throw new PlatformNotSupportedException($"当前平台不支持开机自启动：{Environment.OSVersion}");
        }
    }

    // ---- Windows：HKCU Run 键（用户级，登录启动；SetValue 覆盖写天然幂等）----

    [SupportedOSPlatform("windows")]
    private static void WindowsSetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath);
        if (enabled)
        {
            key.SetValue(WindowsValueName, BuildWindowsRunValue(ExecutablePath));
        }
        else
        {
            key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Run 键值内容：带引号的可执行文件路径 + 自启动参数（Program Files 空格路径安全）。</summary>
    internal static string BuildWindowsRunValue(string exePath) => $"\"{exePath}\" {LaunchArg}";

    // ---- macOS：用户级 LaunchAgent（随该用户登录启动）----

    [SupportedOSPlatform("macos")]
    private static void MacSetEnabled(bool enabled)
    {
        var plistPath = MacLaunchAgentPlistPath;
        if (!enabled)
        {
            if (File.Exists(plistPath))
            {
                File.Delete(plistPath);
            }

            return;
        }

        // 只写 plist 不 launchctl load：load 会立即再拉起一个实例（本进程已在跑）；生效点就是下次登录。
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
        File.WriteAllText(plistPath, BuildLaunchAgentPlist(ExecutablePath));
    }

    /// <summary>LaunchAgent 落点：~/Library/LaunchAgents/&lt;label&gt;.plist（用户级）。</summary>
    private static string MacLaunchAgentPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", MacLaunchAgentLabel + ".plist");

    internal static string BuildLaunchAgentPlist(string exePath)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", MacLaunchAgentLabel),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array",
                        new XElement("string", exePath),
                        new XElement("string", LaunchArg)),
                    new XElement("key", "RunAtLoad"), new XElement("true"))));
        // XDocument.ToString() 省略 XML 声明（launchd 期望的 plist 头），显式拼回。
        return document.Declaration is null
            ? document.ToString()
            : $"{document.Declaration}{Environment.NewLine}{document}";
    }

    // ---- Linux：XDG autostart（GNOME/KDE 等主流 DE 通吃）----

    [SupportedOSPlatform("linux")]
    private static void LinuxSetEnabled(bool enabled)
    {
        var desktopFilePath = LinuxDesktopFilePath;
        if (!enabled)
        {
            if (File.Exists(desktopFilePath))
            {
                File.Delete(desktopFilePath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(desktopFilePath)!);
        File.WriteAllText(desktopFilePath, BuildDesktopEntry(ExecutablePath));
    }

    private static string LinuxDesktopFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", LinuxDesktopFileName);

    /// <summary>XDG autostart 桌面入口；Exec 的可执行路径带引号（空格路径安全）。</summary>
    internal static string BuildDesktopEntry(string exePath) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=Tokenmetry",
        $"Exec=\"{exePath}\" {LaunchArg}",
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        string.Empty);

    private static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("无法定位当前可执行文件，无法注册开机自启动");
}
