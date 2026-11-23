using System.IO.Compression;
using Tokenmetry.PluginValidator;
using Tokenmetry.Plugins.Tests.Runtime;

namespace Tokenmetry.Plugins.Tests.Tools;

/// <summary>
/// 真实校验器测试：调用 <see cref="ValidatorCli.RunAsync(string[])"/>，使用真实的
/// <c>PluginPackageValidator</c> 与真实宿主子进程，不打桩。
/// </summary>
public class PluginValidatorTests
{
    [Fact]
    public async Task Validate_rejects_missing_icon()
    {
        // 只有 manifest：入口程序集与图标都缺失 → 数据缺失，退出码 2。
        using var package = TestPackage.Create(("plugin.json", ValidManifest.Json()));

        var exit = await ValidatorCli.RunAsync(["validate", package.Path]);

        Assert.Equal(ValidatorCli.ExitDataMissing, exit);
    }

    [Fact]
    public async Task Validate_accepts_structurally_valid_package()
    {
        using var package = TestPackage.CreateValid();

        var exit = await ValidatorCli.RunAsync(["validate", package.Path]);

        Assert.Equal(ValidatorCli.ExitPass, exit);
    }

    [Fact]
    public async Task Validate_rejects_invalid_manifest_as_invalid_output()
    {
        // id 含空格：manifest 不合规（不是“缺失”），归为输出不合规，退出码 3。
        using var package = TestPackage.Create(("plugin.json", ValidManifest.Json(id: "bad id")));

        var exit = await ValidatorCli.RunAsync(["validate", package.Path]);

        Assert.Equal(ValidatorCli.ExitInvalidOutput, exit);
    }

    [Fact]
    public async Task Validate_missing_package_file_is_runtime_failure()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "tokenmetry-missing-" + Guid.NewGuid().ToString("N") + ".tkmplugin");

        var exit = await ValidatorCli.RunAsync(["validate", missing]);

        Assert.Equal(ValidatorCli.ExitRuntimeFailure, exit);
    }

    [Fact]
    public async Task Scan_reports_invalid_output()
    {
        // 真实 fixture 插件：usage.jsonl 行缺 sessionId，宿主校验后整份快照全部不合规。
        var packagePath = CreateFixturePackage();
        var dataDirectory = Directory.CreateTempSubdirectory("tokenmetry-validator-data-").FullName;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "usage.jsonl"),
                """{"projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

            var output = new StringWriter();
            var exit = await ValidatorCli.RunAsync(
                ["scan", packagePath, "--data", dataDirectory], output, Console.Error);

            var text = output.ToString();
            Assert.Equal(ValidatorCli.ExitInvalidOutput, exit);
            Assert.Contains("records: 0", text, StringComparison.Ordinal);
            Assert.Contains("invalid: 1", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(dataDirectory);
            TryDeleteFile(packagePath);
        }
    }

    [Fact]
    public async Task Scan_valid_fixture_passes_and_prints_samples()
    {
        var packagePath = CreateFixturePackage();
        var dataDirectory = Directory.CreateTempSubdirectory("tokenmetry-validator-data-").FullName;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "usage.jsonl"),
                """
                {"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}
                {"sessionId":"s2","projectPath":"/work/bar","model":"foo-large","inputTokens":20,"outputTokens":7}
                """);

            var output = new StringWriter();
            var exit = await ValidatorCli.RunAsync(
                ["scan", packagePath, "--data", dataDirectory], output, Console.Error);

            var text = output.ToString();
            Assert.Equal(ValidatorCli.ExitPass, exit);
            Assert.Contains("records: 2", text, StringComparison.Ordinal);
            Assert.Contains("invalid: 0", text, StringComparison.Ordinal);
            Assert.Contains("sample:", text, StringComparison.Ordinal);
            Assert.Contains("s1", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(dataDirectory);
            TryDeleteFile(packagePath);
        }
    }

    /// <summary>
    /// 用测试项目构建期复制到输出的真实 fixture 插件（dll + deps.json + manifest）打一个
    /// 合法 <c>.tkmplugin</c>，补上校验器要求的 PNG 图标。
    /// </summary>
    private static string CreateFixturePackage()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "test-plugin");
        var path = Path.Combine(
            Path.GetTempPath(),
            "tokenmetry-validator-pkg-" + Guid.NewGuid().ToString("N") + ".tkmplugin");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "plugin.json", "Tokenmetry.TestPlugin.dll", "Tokenmetry.TestPlugin.deps.json" })
            {
                archive.CreateEntryFromFile(Path.Combine(source, name), name);
            }

            var icon = archive.CreateEntry("icon.png");
            using var iconStream = icon.Open();
            iconStream.Write(ValidManifest.PngBytes);
        }

        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
