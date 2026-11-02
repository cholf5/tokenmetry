using System;
using System.Diagnostics;

namespace Tokenmetry.App.Update;

/// <summary>
/// 自更新交接的握手：旧进程替换完文件后，带着 <c>--wait-for-exit &lt;pid&gt;</c> 拉起新版本再退出。
/// 新进程必须先等旧进程真的死掉再去抢单实例互斥量——否则抢不到锁，只能给旧进程发个激活请求然后自杀，
/// 用户看到的是「更新完应用不见了」。等待有上限：旧进程若卡在退出流程里，超时后照常启动，最坏结果
/// 是新实例被单实例守卫挡回去（磁盘上已经是新版本，下次启动即生效）。
/// </summary>
public static class UpdateRelaunchHandshake
{
    public const string WaitForExitOption = "--wait-for-exit";

    /// <summary>等旧进程退出的上限。正常退出远低于此（UI 关闭 + 定时器停止约百毫秒级）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>从命令行取要等待的进程号。缺失/非正整数/不是本选项都返回 false（按普通启动处理）。</summary>
    public static bool TryParse(string[]? args, out int processId)
    {
        processId = 0;
        if (args is null)
        {
            return false;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], WaitForExitOption, StringComparison.Ordinal)
                && int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                processId = pid;
                return true;
            }
        }

        return false;
    }

    /// <summary>等进程退出（不存在/已退出立即返回）。任何异常都吞掉——这是启动路径上的一步，
    /// 等不到只是多一个实例被守卫挡回去，不该让应用起不来。</summary>
    public static void WaitForExit(int processId, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // 进程已经不在了（最常见的正常路径）或无权访问：直接继续启动。
        }
    }
}
