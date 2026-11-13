// Tokenmetry 冒烟工具：本地采集管道真机冒烟 + IO 行为验证。
//
// 用法：dotnet run --project tools/Tokenmetry.SmokeScan -- [轮数，默认 3]
//
// 行为：按 AppServices.cs 同款接线在进程内组装 20 源采集管道（LocalIngestionDataSource），
// 对真实本地数据源连续跑 N 轮扫描；facts 落临时库、日志落临时目录（零副作用——不读用户
// settings.json、不碰用户 tokenmetry.db、不用用户覆盖价），结束后自动清理临时目录。
// 逐源耗时/计数经生产同款 IngestionFileLogger 记录后整体回显——隐私红线同 logger：
// 只输出路径/计数/耗时/异常类型，绝无 prompt / 代码 / 会话内容。
//
// 读数口径（以 ZCode 源为最敏感哨兵——status 无索引、账面变化即全表 SCAN model_usage）：
// 首轮全量读取秒级；随后 writer 活跃期应在 10ms 量级（持久只读连接暖页缓存），
// 空闲期（账本无写入）应在 0-1ms 量级（文件指纹快路径，连 SQL 探针都不发）。
// 若持续出现秒级轮次，说明探针快路径/持久连接回归，查 ZCodeSqliteAdapter。

using System.Diagnostics;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Cache;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Cindy;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.LocalIngestion;
using Tokenmetry.Infrastructure.ModelsDev;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;

var rounds = args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0 ? parsed : 3;

// 接线镜像 AppServices.cs（差集：无 DI 容器 / 无用户设置 / 无 UI 服务）。
// 设置取默认值是有意为之：冒烟验证的是采集管道的 IO 行为，与用户的覆盖价、刷新间隔等无关。
var settings = new AppSettings();
var pricingClient = new ModelsDevPricingClient();
var pricingEngine = new PricingEngine(catalog: new ModelCatalog(pricingClient));
var scanRegistry = new IngestionScanRegistry();

var smokeRoot = Path.Combine(Path.GetTempPath(), $"tokenmetry-smoke-{Guid.NewGuid():N}");
var logDirectory = Path.Combine(smokeRoot, "logs");
Directory.CreateDirectory(logDirectory);
var fileLog = new IngestionFileLogger(logDirectory, enabled: true);

// 共用扫描注册表的适配器（同 AppServices 接线）；Pi 系适配器 identity cost 与主管道同引擎。
var claude = new ClaudeCodeJsonlAdapter(scanRegistry: scanRegistry);
var codex = new CodexJsonlAdapter(scanRegistry: scanRegistry);
var dsh = new DshSessionAdapter(scanRegistry: scanRegistry);
var qwen = new QwenCodeAdapter(scanRegistry: scanRegistry);
var kimi = new KimiCodeAdapter(scanRegistry: scanRegistry);
var grok = new GrokAdapter(scanRegistry: scanRegistry);
var openClaw = new OpenClawAdapter(scanRegistry: scanRegistry);
var pi = new PiAgentAdapter(pricingEngine: pricingEngine);
var cindy = new CindyPiAdapter(pricingEngine: pricingEngine);

var store = new SqliteUsageStore(Path.Combine(smokeRoot, "smoke.db"));
await store.InitializeAsync();

var dataSource = new LocalIngestionDataSource(
    claude,
    codex,
    new ZCodeSqliteAdapter(),
    dsh,
    new CopilotSessionAdapter(),
    new OpenCodeSqliteAdapter(),
    new GooseSqliteAdapter(),
    new HermesSqliteAdapter(),
    new KiloSqliteAdapter(),
    new GeminiCliAdapter(),
    new AmpAdapter(),
    new DroidAdapter(),
    qwen,
    kimi,
    new CodebuffAdapter(),
    pi,
    cindy,
    grok,
    openClaw,
    new AntigravityAdapter(),
    pricingEngine,
    store,
    pricingClient,
    timeZone: null,
    scanRegistry,
    fileLog);

Console.WriteLine($"Tokenmetry 冒烟：{rounds} 轮 × 20 源采集扫描（临时目录 {smokeRoot}）");
Console.WriteLine("源数据只读；facts 落临时库；结束自动清理。逐源明细见末尾。");
Console.WriteLine();

for (var round = 1; round <= rounds; round++)
{
    var stopwatch = Stopwatch.StartNew();
    var summaries = await dataSource.GetDailyUsageAsync();
    stopwatch.Stop();
    Console.WriteLine($"round {round}: total {stopwatch.ElapsedMilliseconds} ms, daily summaries {summaries.Count}");
}

Console.WriteLine();
Console.WriteLine($"--- 逐源明细（生产同款 {IngestionFileLogger.LogFileName}）---");
var logPath = Path.Combine(logDirectory, IngestionFileLogger.LogFileName);
if (File.Exists(logPath))
{
    foreach (var line in File.ReadLines(logPath))
    {
        Console.WriteLine(line);
    }
}

try
{
    Directory.Delete(smokeRoot, recursive: true);
}
catch (IOException)
{
    // 临时目录清理失败不构成冒烟失败（Windows 文件锁等），同测试 Dispose 口径。
    Console.WriteLine($"（临时目录未清理成功，可手动删除：{smokeRoot}）");
}
