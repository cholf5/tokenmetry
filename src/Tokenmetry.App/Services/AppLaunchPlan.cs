using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Tokenmetry.App.Services;

/// <summary>一条进程启动计划：程序、参数、工作目录（参数走 ArgumentList，不拼 shell 字符串）。</summary>
internal sealed record AppLaunchPlan(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>启动计划执行器：best-effort，失败只打诊断并返回 false（调用方决定是否 toast）。</summary>
internal static class AppLaunchProcess
{
    public static bool TryStart(AppLaunchPlan plan)
    {
        try
        {
            var startInfo = new ProcessStartInfo(plan.FileName)
            {
                UseShellExecute = false,
                WorkingDirectory = plan.WorkingDirectory,
            };
            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 启动外部程序失败（{plan.FileName}）：{ex.Message}");
            return false;
        }
    }
}
