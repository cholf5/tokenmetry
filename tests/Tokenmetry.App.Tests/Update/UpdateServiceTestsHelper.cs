using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Tests.Update;

/// <summary>
/// 跨测试文件的更新服务测试夹具：SettingsViewModelTests（更新区状态行）与 UpdateServiceTests
/// 共用同一套假响应构造，避免两处漂移。
/// </summary>
internal static class UpdateServiceTestsHelper
{
    /// <summary>releases/latest 回 302 指向 latestTag 的假响应更新服务。</summary>
    public static UpdateService CreateService(string latestTag, string currentVersion)
    {
        var handler = new FakeUpdateHttpHandler(_ => Redirect(latestTag));
        return new UpdateService(
            new UpdateChecker(handler, currentVersion),
            new AppSettings(),
            new FakeStore());
    }

    /// <summary>检查必失败（HTTP 403）的更新服务。</summary>
    public static UpdateService CreateFailing()
    {
        var handler = new FakeUpdateHttpHandler(_ => FakeUpdateHttpHandler.Status(HttpStatusCode.Forbidden));
        return new UpdateService(
            new UpdateChecker(handler, currentVersion: "v0.2.0"),
            new AppSettings(),
            new FakeStore());
    }

    /// <summary>已检查完（状态定格在 Available）的更新服务，供「重开设置页同步已知状态」类用例。</summary>
    public static async Task<(UpdateService Service, FakeStore Store)> CreateChecked(string latestTag, string currentVersion)
    {
        var store = new FakeStore();
        var service = new UpdateService(
            new UpdateChecker(new FakeUpdateHttpHandler(_ => Redirect(latestTag)), currentVersion),
            new AppSettings(),
            store);
        await service.CheckNowAsync();
        return (service, store);
    }

    private static HttpResponseMessage Redirect(string tag) =>
        FakeUpdateHttpHandler.Redirect($"/cholf5/tokenmetry/releases/tag/{tag}");

    internal sealed class FakeStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}
