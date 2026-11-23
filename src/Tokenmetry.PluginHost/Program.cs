using Tokenmetry.PluginHost;

// 插件宿主进程入口：由主进程以 `dotnet Tokenmetry.PluginHost.dll --plugin-dir ... --data-dir ...
// --host-version ...` 拉起。stdin 收请求、stdout 写 NDJSON、stderr 写日志。
// 主应用保持 AOT，本进程是普通 CoreCLR Exe，只在这里加载第三方插件程序集。

var pluginDir = GetArg(args, "--plugin-dir");
var dataDir = GetArg(args, "--data-dir");
var hostVersion = GetArg(args, "--host-version") ?? "0.0.0";

if (pluginDir is null || dataDir is null)
{
    Console.Error.WriteLine(
        "usage: Tokenmetry.PluginHost --plugin-dir <dir> --data-dir <dir> [--host-version <version>]");
    return 2;
}

var options = new PluginHostOptions
{
    PluginDirectory = pluginDir,
    DataDirectory = dataDir,
    HostVersion = hostVersion,
};

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();
return await PluginHostServer.RunAsync(options, stdin, stdout, CancellationToken.None);

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}
