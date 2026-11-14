using Microsoft.AspNetCore.Mvc.Testing;

namespace Tokenmetry.Relay.Tests;

/// <summary>
/// 每测试一个独立中继实例：独立临时库 + 进程级环境变量（Program 启动时读取）。
/// 环境变量是进程级的 → 本程序集禁并行（同 Infrastructure.Tests 的 A-3 先例，见 xunit.runner.json）。
/// </summary>
internal sealed class RelayFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;

    /// <summary>WithWebHostBuilder 派生实例专用（反射走此构造）：必须保持 env 原样——派生主机沿用根实例
    /// 已设置的进程级环境变量（static 状态，先于派生）。测试根实例一律走带参构造（钉死独立临时库与 env），
    /// 无参构造的 env 透传不得用于根实例（会吃进前序测试留下的进程级 env）。</summary>
    public RelayFactory()
    {
        _dbPath = string.Empty;
    }

    public RelayFactory(string? token = null, string? githubClientId = null, string? githubClientSecret = null, string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(Path.GetTempPath(), $"tokenmetry-relay-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_DB", _dbPath);
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_TOKEN", token);
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_GITHUB_CLIENT_ID", githubClientId);
        Environment.SetEnvironmentVariable("TOKENMETRY_RELAY_GITHUB_CLIENT_SECRET", githubClientSecret);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // 连接池归还后才轮得到删文件（Windows 上不清池必撞共享锁），收尾口径见 TempDbCleanup。
        TempDbCleanup.DeleteWithSidecars(_dbPath);
    }
}
