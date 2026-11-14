using System.Runtime.InteropServices;

namespace Tokenmetry.SmokeScan;

/// <summary>一轮进程级读取计数快照：累计读取字节（read_bytes）与累计读取次数（read_ops）。</summary>
internal readonly record struct ProcessIoCounters(long ReadBytes, long ReadOps);

/// <summary>
/// I-052 进程级读取量尺：每轮扫描前后各取一次读取计数，差值即该轮的进程读取量。
/// 平台按可得性分级（不引第三方包、P/Invoke 只此一处）：
/// - Windows：kernel32 <c>GetProcessIoCounters</c>（ReadTransferCount / ReadOperationCount）；
/// - Linux：<c>/proc/self/io</c> 的 <c>rchar</c> / <c>syscr</c>；
/// - macOS：无等价的进程级读字节计数可白拿（getrusage 无字节字段；proc_pid_rusage 需要额外的
///   P/Invoke，卡内开放问题明确允许留空）→ 恒不可得。
/// 不可得/读取失败一律降级 n/a，绝不因量尺缺失让冒烟失败；stderr 只做一次性说明，
/// 不进 stdout 逐源明细（量尺输出只有计数与增量，无会话内容，隐私红线同 SmokeScan）。
/// </summary>
internal static class ProcessIo
{
    private static bool _unavailabilityReported;

    public static bool TryRead(out ProcessIoCounters counters)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindows(out counters);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryReadLinux(out counters);
        }

        counters = default;
        return ReportUnavailable("当前平台无进程级读取计数实现（macOS 暂留 n/a，见 I-052）");
    }

    private static bool ReportUnavailable(string reason)
    {
        if (!_unavailabilityReported)
        {
            _unavailabilityReported = true;
            Console.Error.WriteLine($"[I-052] 读取量尺不可得，read_bytes/read_ops 记 n/a（不影响扫描）：{reason}");
        }

        return false;
    }

    private static bool TryReadWindows(out ProcessIoCounters counters)
    {
        counters = default;
        try
        {
            // GetCurrentProcess() 的伪句柄恒为 (HANDLE)-1（文档化行为），不必再 P/Invoke 一次。
            if (!GetProcessIoCounters(new IntPtr(-1), out var io))
            {
                return ReportUnavailable("GetProcessIoCounters 返回 false");
            }

            counters = new ProcessIoCounters(
                ReadBytes: (long)io.ReadTransferCount,
                ReadOps: (long)io.ReadOperationCount);
            return true;
        }
        catch (Exception ex)
        {
            return ReportUnavailable($"GetProcessIoCounters 异常 {ex.GetType().Name}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters lpIoCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private static bool TryReadLinux(out ProcessIoCounters counters)
    {
        counters = default;
        try
        {
            long? rchar = null;
            long? syscr = null;
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                if (line.StartsWith("rchar", StringComparison.Ordinal))
                {
                    rchar = long.Parse(line.AsSpan(colon + 1).Trim());
                }
                else if (line.StartsWith("syscr", StringComparison.Ordinal))
                {
                    syscr = long.Parse(line.AsSpan(colon + 1).Trim());
                }
            }

            if (rchar is not { } bytes || syscr is not { } ops)
            {
                return ReportUnavailable("/proc/self/io 缺少 rchar/syscr 字段");
            }

            counters = new ProcessIoCounters(bytes, ops);
            return true;
        }
        catch (Exception ex)
        {
            return ReportUnavailable($"/proc/self/io 读取异常 {ex.GetType().Name}");
        }
    }
}
