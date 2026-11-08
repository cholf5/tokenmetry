using Microsoft.AspNetCore.Mvc.Testing;

namespace Tokenmetry.Relay.Tests;

/// <summary>
/// 每测试一个独立中继实例：独立临时库 + 进程级环境变量（Program 启动时读取）。
/// 环境变量是进程级的 → 本程序集禁并行（同 Infrastructure.Tests 的 A-3 先例，见 xunit.runner.json）。
/// </summary>
internal sealed class RelayFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;

    public RelayFactory(string? token = null)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-relay-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_DB", _dbPath);
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_TOKEN", token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // 文件句柄晚释放：留到临时目录自清理，测试不因此失败。
        }
    }
}
