namespace Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// agent 日志文件共享读入口（B-010）：Windows CreateFile 共享规则要求本次申请的 share mode
/// 覆盖已存在句柄的全部 access——agent 持写句柄追加日志时，<c>FileShare.Read</c> 打开即抛
/// sharing violation（Unix 上 FileShare 是空操作，macOS/Linux 开发机永不复现）。统一申请
/// <c>ReadWrite | Delete</c>：写句柄保持期间边写边读（<see cref="DshSession.DshZstdLogStream"/>
/// 把 torn tail 定为活跃会话预期状态——边写边读本就是设计内场景），日志轮转的 rename/delete
/// 也不被读句柄挡住。
/// </summary>
public static class AgentLogReader
{
    public static Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
