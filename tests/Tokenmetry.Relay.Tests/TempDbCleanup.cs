namespace Tokenmetry.Relay.Tests;

using Microsoft.Data.Sqlite;

/// <summary>
/// 临时中继库的收尾删除。中继走 Microsoft.Data.Sqlite 的**默认连接池**，主机 dispose 只归还
/// 连接、不关文件句柄——不先清池，Windows 上 <see cref="File.Delete(string)"/> 会直接撞共享锁抛
/// <see cref="IOException"/>（POSIX unlink 不校验，所以 macOS/Linux 上一直没暴露）。
/// 删除失败仍然吞掉：临时目录自清理即可，测试不因此失败（与 RelayFactory.Dispose 同口径）。
/// </summary>
internal static class TempDbCleanup
{
    public static void DeleteWithSidecars(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();

            // -wal / -shm 边车一并清掉：残留会让下次同路径重开（或临时目录）读到旧状态。
            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // 文件句柄晚释放：留到临时目录自清理。
        }
    }
}
