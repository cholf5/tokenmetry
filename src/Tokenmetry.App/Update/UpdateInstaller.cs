using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Tokenmetry.App.Update;

/// <summary>
/// Windows 静默升级的执行端：把 Inno Setup 安装器以 <c>/SILENT</c> 拉起，由它替换正在运行的文件、
/// 装完自动重启应用；应用这边一经拉起就退出，文件锁随即释放。
///
/// 为什么这不算「自更新替换」：替文件的是安装器——它懂文件锁重试、半拷回滚、旧版本清理，而应用自己
/// 跨平台替换运行中的单文件 exe / .app 是雷区（进程锁、封签、Gatekeeper）。应用只做三件事：
/// 下完、拉起、退出。便携版与 macOS/Linux 仍走「下载 + 文件管理器显示」。
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>静默升级参数：不弹向导、不等确认；<c>/CLOSEAPPLICATIONS</c> 让 Restart Manager 收掉
    /// 仍占着文件的实例（应用自己也会退出，这是双保险）；<c>/NORESTART</c> 不重启系统——安装器脚本
    /// 末尾会自己把应用拉起来（<c>[Run]</c> 条目）。</summary>
    public const string SilentArguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES";

    private readonly Func<string, string, bool>? _startOverride;

    /// <summary>startOverride 供测试接管「拉起了什么、带了什么参数」与结果（true = 已启动），不真起进程。</summary>
    public UpdateInstaller(Func<string, string, bool>? startOverride = null) => _startOverride = startOverride;

    /// <summary>拉起静默安装。true = 安装器进程已启动（调用方随后应退出）；
    /// false = 起不来（文件被拦、权限、安装器路径不存在），调用方退回「在文件管理器里显示文件」。</summary>
    public bool TryStartSilent(string installerPath)
    {
        if (_startOverride is { } start)
        {
            return start(installerPath, SilentArguments);
        }

        if (!File.Exists(installerPath))
        {
            Console.Error.WriteLine($"[Tokenmetry] 安装器不存在：{installerPath}");
            return false;
        }

        try
        {
            // UseShellExecute=false + CreateNoWindow：不弹命令行黑框，也不经由 shell 关联（避免被
            // 「用什么程序打开 .exe」之类关联劫持）。安装器自己会显示进度窗口。
            using var process = Process.Start(new ProcessStartInfo(installerPath, SilentArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
            });
            if (process is not null)
            {
                return true;
            }

            Console.Error.WriteLine($"[Tokenmetry] 安装器未能启动：{installerPath}");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 启动安装器失败（{installerPath}）：{ex.Message}");
            return false;
        }
    }
}
