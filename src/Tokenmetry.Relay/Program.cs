using Tokenmetry.Relay;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var database = new RelayDatabase(
    Environment.GetEnvironmentVariable("TOKENMETRY_RELAY_DB") ?? "relay.db");
await database.EnsureCreatedAsync();

// 单租户自举（A15）：env token 非空 = 自建模式（唯一账号）；缺失 = 官方多租户模式（仅 GitHub OAuth）。
var bootstrapToken = Environment.GetEnvironmentVariable("TOKENMETRY_RELAY_TOKEN");
if (!string.IsNullOrEmpty(bootstrapToken))
{
    await RelayDatabase.BootstrapSharedAccountAsync(database, bootstrapToken);
}

// 匿名健康检查（自建编排用）；/api/* 全部要求 Bearer。
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        // TODO(v1 上线项)：限流未内建（设计 §4.5 留给部署层——反代/防火墙；自建文档警示）。
        var token = RelayAuth.ExtractBearerToken(context.Request);
        if (token is null)
        {
            await RelayAuth.UnauthorizedAsync(context);
            return;
        }

        await using var connection = database.OpenConnection();
        var account = await RelayAuth.MapAccountAsync(connection, token);
        if (account is null)
        {
            await RelayAuth.UnauthorizedAsync(context);
            return;
        }

        context.Items[RelayAuth.AccountItemKey] = account;
    }

    await next(context);
});

app.MapGet("/api/me", (HttpContext context) =>
{
    var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
    return Results.Ok(new { id = account.Id, kind = account.Kind, displayName = account.DisplayName });
});

// TODO(v1 上线项)：HTTPS 不在此强制（自建允许 http://，设计 A15；文档警示明文风险，
// 反代终结 TLS 是部署责任）。
app.Run();

// WebApplicationFactory<Program> 的可见入口（集成测试）。
public partial class Program;
