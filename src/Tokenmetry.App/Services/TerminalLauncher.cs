using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Services;

/// <summary>
/// 终端出口（三平台）：在指定目录打开一个新的终端窗口。
///
/// 与 <see cref="ShellLauncher"/> 同语义——best-effort，失败只打诊断并返回 false，由调用方决定
/// 是否 toast。命令构建（<c>Build*Plans</c>）与启动分离为纯函数，测试不真起终端：
/// <list type="bullet">
/// <item>Windows：先试 Windows Terminal（<c>wt.exe -d &lt;path&gt;</c>），失败退回 <c>cmd.exe</c>
/// （工作目录直接设在项目路径）；</item>
/// <item>macOS：按已安装应用优先 Ghostty → iTerm2 → Terminal.app；Ghostty 新窗口经
/// <c>open -na Ghostty.app --args --working-directory=&lt;path&gt;</c>，iTerm2 走 AppleScript
/// （<c>quoted form of</c> 由 shell 语义转义，路径不拼进脚本字符串）；</item>
/// <item>Linux：按 <c>$TERMINAL</c> → <c>xdg-terminal-exec</c> → 常见终端逐条尝试，参数化终端
/// 用各自的 working-directory 开关，其余靠进程工作目录。</item>
/// </list>
/// 路径一律经 <see cref="ProcessStartInfo.ArgumentList"/> 传递，不拼 shell 字符串。
/// </summary>
public static class TerminalLauncher
{
    /// <summary>macOS 终端选择（探测结果与测试注入共用；默认 Terminal.app）。</summary>
    internal enum MacTerminalKind
    {
        Terminal,
        ITerm,
        Ghostty,
    }

    /// <summary>本机首选终端的展示名（右键菜单「在 Ghostty 中打开终端」用）；探测失败回落 "Terminal"。</summary>
    public static string PreferredDisplayName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CommandExists("wt.exe") ? "Windows Terminal" : "Command Prompt";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return DetectMacTerminal() switch
            {
                MacTerminalKind.Ghostty => "Ghostty",
                MacTerminalKind.ITerm => "iTerm2",
                _ => "Terminal",
            };
        }

        foreach (var plan in BuildLinuxPlans(string.Empty, Environment.GetEnvironmentVariable("TERMINAL")))
        {
            if (CommandExists(plan.FileName))
            {
                return LinuxDisplayName(plan.FileName);
            }
        }

        return "Terminal";
    }

    /// <summary>本机首选终端的图标源（macOS = .app 路径；Windows/Linux 暂未实现图标提取）。</summary>
    public static string? PreferredIconSource()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        return DetectMacTerminal() switch
        {
            MacTerminalKind.Ghostty => FindMacBundle("Ghostty.app"),
            MacTerminalKind.ITerm => FindMacBundle("iTerm.app"),
            _ => FindTerminalBundle(),
        };
    }

    /// <summary>在 <paramref name="directoryPath"/> 打开新终端窗口；成功启动任一候选返回 true。</summary>
    public static bool TryOpen(string directoryPath)
    {
        foreach (var plan in BuildPlans(directoryPath))
        {
            if (AppLaunchProcess.TryStart(plan))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按当前平台给出候选计划（顺序即优先级）。</summary>
    internal static IReadOnlyList<AppLaunchPlan> BuildPlans(string directoryPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return BuildWindowsPlans(directoryPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return BuildMacPlans(directoryPath, DetectMacTerminal());
        }

        return BuildLinuxPlans(directoryPath, Environment.GetEnvironmentVariable("TERMINAL"));
    }

    internal static IReadOnlyList<AppLaunchPlan> BuildWindowsPlans(string directoryPath) =>
    [
        new("wt.exe", ["-d", directoryPath], directoryPath),
        new("cmd.exe", ["/k"], directoryPath),
    ];

    internal static IReadOnlyList<AppLaunchPlan> BuildMacPlans(string directoryPath, MacTerminalKind terminal) =>
        terminal switch
        {
            MacTerminalKind.Ghostty =>
            [
                // -n = 新实例；window-inherit-working-directory 默认 true 会覆盖 working-directory，
                // 必须显式关掉，否则已有窗口时新窗口会继承旧目录而不是项目目录。
                new(
                    "open",
                    [
                        "-na", "Ghostty.app", "--args",
                        $"--working-directory={directoryPath}",
                        "--window-inherit-working-directory=false",
                    ],
                    directoryPath),
            ],
            MacTerminalKind.ITerm =>
            [
                new("osascript", BuildITermAppleScriptArguments(directoryPath), directoryPath),
            ],
            _ =>
            [
                new("open", ["-a", "Terminal", directoryPath], directoryPath),
            ],
        };

    internal static IReadOnlyList<AppLaunchPlan> BuildLinuxPlans(string directoryPath, string? terminalEnvironmentVariable)
    {
        var plans = new List<AppLaunchPlan>();
        var preferred = terminalEnvironmentVariable?.Trim();
        if (!string.IsNullOrEmpty(preferred))
        {
            // $TERMINAL 约定是命令而非裸路径；按空白拆成程序 + 额外参数（如 "alacritty --class foo"）。
            var parts = preferred.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            plans.Add(new AppLaunchPlan(parts[0], parts[1..], directoryPath));
        }

        plans.Add(new AppLaunchPlan("xdg-terminal-exec", [], directoryPath));
        plans.Add(new AppLaunchPlan("ghostty", [$"--working-directory={directoryPath}"], directoryPath));
        plans.Add(new AppLaunchPlan("gnome-terminal", ["--working-directory", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("konsole", ["--workdir", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("xfce4-terminal", ["--working-directory", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("mate-terminal", ["--working-directory", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("alacritty", ["--working-directory", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("kitty", ["--directory", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("wezterm", ["start", "--cwd", directoryPath], directoryPath));
        plans.Add(new AppLaunchPlan("xterm", [], directoryPath));
        return plans;
    }

    /// <summary>macOS 已安装终端探测：Ghostty → iTerm2 → Terminal.app（Terminal.app 系统自带）。</summary>
    internal static MacTerminalKind DetectMacTerminal()
    {
        if (IsMacApplicationInstalled("Ghostty"))
        {
            return MacTerminalKind.Ghostty;
        }

        if (IsMacApplicationInstalled("iTerm"))
        {
            return MacTerminalKind.ITerm;
        }

        return MacTerminalKind.Terminal;
    }

    private static bool IsMacApplicationInstalled(string applicationName) =>
        FindMacBundle($"{applicationName}.app") is not null;

    private static string? FindMacBundle(string bundleName)
    {
        var system = Path.Combine("/Applications", bundleName);
        if (Directory.Exists(system))
        {
            return system;
        }

        var userApplications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
        var user = Path.Combine(userApplications, bundleName);
        return Directory.Exists(user) ? user : null;
    }

    private static string? FindTerminalBundle()
    {
        // 新版 macOS 的 Terminal.app 在 /System/Applications/Utilities 下。
        const string systemTerminal = "/System/Applications/Utilities/Terminal.app";
        if (Directory.Exists(systemTerminal))
        {
            return systemTerminal;
        }

        return FindMacBundle("Terminal.app");
    }

    /// <summary>
    /// iTerm2 的 AppleScript 启动参数：路径经 <c>on run argv</c> 传入、在脚本内用
    /// <c>quoted form of</c> 转义后拼接 <c>cd</c>，避免把路径写进脚本源码。
    /// </summary>
    private static string[] BuildITermAppleScriptArguments(string directoryPath) =>
    [
        "-e", "on run argv",
        "-e", "tell application \"iTerm\"",
        "-e", "create window with default profile",
        "-e", "tell current session of current window",
        "-e", "write text (\"cd \" & quoted form of item 1 of argv)",
        "-e", "end tell",
        "-e", "end tell",
        "-e", "end run",
        "--",
        directoryPath,
    ];

    private static bool CommandExists(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                if (File.Exists(Path.Combine(directory, command + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string LinuxDisplayName(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command);
        return name switch
        {
            "xdg-terminal-exec" => "Terminal",
            "ghostty" => "Ghostty",
            "gnome-terminal" => "GNOME Terminal",
            "xfce4-terminal" => "Xfce Terminal",
            "mate-terminal" => "MATE Terminal",
            "konsole" => "Konsole",
            "alacritty" => "Alacritty",
            "kitty" => "kitty",
            "wezterm" => "WezTerm",
            "xterm" => "xterm",
            _ => name,
        };
    }
}
