using Tokenmetry.Plugin.Abstractions;
using Tokenmetry.PluginValidator;
using Tokenmetry.Plugins.Tests.Runtime;

namespace Tokenmetry.Plugins.Tests.Tools;

/// <summary>
/// 契约包同步校验：<c>plugin-sdk/contract</c> 里包的公开 API 必须与当前源码一致，
/// 否则插件作者编译时看到的是旧契约，宿主运行时用的却是新契约。
/// </summary>
public class ContractSyncVerifierTests
{
    [Fact]
    public void Committed_package_matches_current_source()
    {
        var report = ContractSyncVerifier.Verify(
            ContractPackagePath(), typeof(IUsageSourcePlugin).Assembly);

        Assert.True(report.InSync, string.Join(Environment.NewLine, report.Differences));
    }

    [Fact]
    public void Detects_missing_contract_assembly()
    {
        using var package = TestPackage.Create(("plugin.json", "{}"));

        var report = ContractSyncVerifier.Verify(
            package.Path, typeof(IUsageSourcePlugin).Assembly);

        Assert.False(report.InSync);
        Assert.Contains(
            report.Differences,
            difference => difference.Contains(
                ContractSyncVerifier.PackageAssemblyPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Detects_public_api_difference()
    {
        // 拿校验器自己的程序集冒充契约 DLL：程序集名与类型集都不同，必然报差异。
        var foreignAssembly = typeof(ContractSyncVerifier).Assembly.Location;
        using var package = TestPackage.CreateRaw(
            (ContractSyncVerifier.PackageAssemblyPath, File.ReadAllBytes(foreignAssembly), 0));

        var report = ContractSyncVerifier.Verify(
            package.Path, typeof(IUsageSourcePlugin).Assembly);

        Assert.False(report.InSync);
        Assert.NotEmpty(report.Differences);
    }

    [Fact]
    public async Task Cli_resolves_committed_package_and_passes()
    {
        // CI 跑的就是不带 --package 的这条：从当前目录/校验器目录向上找
        // plugin-sdk/contract，所以这里覆盖默认解析路径。
        var output = new StringWriter();

        var exit = await ValidatorCli.RunAsync(["verify-contract"], output, Console.Error);

        Assert.Equal(ValidatorCli.ExitPass, exit);
        Assert.Contains("contract: PASS", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_fails_when_package_has_no_contract_assembly()
    {
        using var package = TestPackage.Create(("plugin.json", "{}"));
        var output = new StringWriter();

        var exit = await ValidatorCli.RunAsync(
            ["verify-contract", "--package", package.Path], output, Console.Error);

        Assert.Equal(ValidatorCli.ExitInvalidOutput, exit);
    }

    /// <summary>从测试输出目录向上找仓库里的 plugin-sdk/contract，取唯一一个 nupkg。</summary>
    private static string ContractPackagePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var contractDirectory = Path.Combine(directory.FullName, "plugin-sdk", "contract");
            if (!Directory.Exists(contractDirectory))
            {
                continue;
            }

            var packages = Directory.GetFiles(contractDirectory, "*.nupkg");
            Assert.Single(packages);
            return packages[0];
        }

        throw new InvalidOperationException(
            "plugin-sdk/contract was not found above the test output directory");
    }
}
