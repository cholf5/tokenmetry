namespace Tokenmetry.Infrastructure.Tests.ModelsDev;

using System.Net;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.ModelsDev;

public class ModelsDevPricingClientTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    private const string SampleResponse = """
        {
          "anthropic": {
            "id": "anthropic",
            "models": {
              "claude-opus-4-8": {
                "id": "claude-opus-4-8",
                "name": "Claude Opus 4.8",
                "cost": { "input": 10, "output": 50, "cache_read": 1, "cache_write": 12.5 }
              },
              "claude-sonnet-4-6": {
                "id": "claude-sonnet-4-6",
                "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task CurrentIsNullAndNoNetworkBeforeFirstRefresh()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);

        client.Current.Should().BeNull();
        client.IsRefreshDue.Should().BeTrue();
        handler.RequestCount.Should().Be(0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshAsyncFetchesAndPopulatesCurrent()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);

        var refreshed = await client.RefreshAsync();

        refreshed.Should().BeTrue();
        handler.RequestCount.Should().Be(1);
        handler.LastRequestUrl.Should().Be(client.ApiUrl.ToString());
        client.LastError.Should().BeNull();
        client.LastSuccessAt.Should().Be(_timeProvider.GetUtcNow());
        client.Current.Should().NotBeNull();
        client.Current!.Entries.Should().Contain(entry => entry.ModelId == "claude-opus-4-8");
        client.Current.Entries.Should().OnlyContain(entry => entry.Provider == "anthropic");

        var engine = new PricingEngine(client);
        var priced = engine.PriceTokens("claude-opus-4-8", new TokenUsageDetail { InputTokens = 1_000_000 });
        priced.Source.Should().Be(PricingSourceKind.Remote);
        priced.CostUsd.Should().Be(10m);
    }

    [Fact]
    public async Task RefreshIfDueAsyncSkipsFetchWhileWithinTtlAndRefetchesAfterExpiry()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);
        await client.RefreshAsync();
        handler.RequestCount.Should().Be(1);

        _timeProvider.Advance(TimeSpan.FromHours(23));
        (await client.RefreshIfDueAsync()).Should().BeFalse();
        handler.RequestCount.Should().Be(1);

        // TTL 24h 到期后重新拉取。
        _timeProvider.Advance(TimeSpan.FromHours(2));
        client.IsRefreshDue.Should().BeTrue();
        (await client.RefreshIfDueAsync()).Should().BeTrue();
        handler.RequestCount.Should().Be(2);
        client.LastError.Should().BeNull();
    }

    [Fact]
    public async Task NeverRefreshedIsAlwaysDue()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);

        (await client.RefreshIfDueAsync()).Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task NetworkFailureKeepsLastSuccessfulDataSilently()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);
        await client.RefreshAsync();
        var first = client.Current;

        handler.NextResponse = () => throw new HttpRequestException("offline");
        _timeProvider.Advance(TimeSpan.FromHours(25));
        var refreshed = await client.RefreshIfDueAsync();

        refreshed.Should().BeFalse();
        client.Current.Should().BeSameAs(first);
        client.Current!.Entries.Should().NotBeEmpty();
        client.LastError.Should().Be("offline");

        // 恢复后再次成功并清空错误。
        handler.NextResponse = handler.OkResponse;
        (await client.RefreshAsync()).Should().BeTrue();
        client.LastError.Should().BeNull();
    }

    [Fact]
    public async Task HttpErrorStatusIsAFailure()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);
        await client.RefreshAsync();

        handler.NextResponse = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var refreshed = await client.RefreshAsync();

        refreshed.Should().BeFalse();
        client.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResponseWithoutPricedModelsIsAFailure()
    {
        var handler = new FakePricingHandler("{ \"anthropic\": { \"models\": {} } }");
        using var client = CreateClient(handler);

        var refreshed = await client.RefreshAsync();

        refreshed.Should().BeFalse();
        client.Current.Should().BeNull();
        client.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task MalformedResponseIsAFailure()
    {
        var handler = new FakePricingHandler("not-json");
        using var client = CreateClient(handler);

        var refreshed = await client.RefreshAsync();

        refreshed.Should().BeFalse();
        client.Current.Should().BeNull();
        client.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshNeverThrowsEvenWhenCancelled()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await client.RefreshAsync(cts.Token);

        await act.Should().NotThrowAsync();
        client.Current.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentRefreshIsSingleFlight()
    {
        var handler = new FakePricingHandler(SampleResponse);
        var gate = new TaskCompletionSource();
        handler.HoldUntil(gate.Task);
        using var client = CreateClient(handler);

        var first = client.RefreshAsync();
        var second = await client.RefreshAsync();

        second.Should().BeFalse("another refresh is in flight");
        gate.SetResult();
        (await first).Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public void CustomTtlIsHonored()
    {
        var handler = new FakePricingHandler(SampleResponse);
        using var client = CreateClient(handler, ttl: TimeSpan.FromHours(2));

        client.Ttl.Should().Be(TimeSpan.FromHours(2));
    }

    private ModelsDevPricingClient CreateClient(FakePricingHandler handler, TimeSpan? ttl = null)
    {
        return new ModelsDevPricingClient(handler, _timeProvider, ttl);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow += delta;
        }
    }

    private sealed class FakePricingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private Task? _holdUntil;

        public FakePricingHandler(string responseBody)
        {
            _responseBody = responseBody;
            NextResponse = OkResponse;
        }

        public Func<HttpResponseMessage> NextResponse { get; set; }

        public int RequestCount { get; private set; }

        public string? LastRequestUrl { get; private set; }

        public HttpResponseMessage OkResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responseBody) };
        }

        public void HoldUntil(Task task)
        {
            _holdUntil = task;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUrl = request.RequestUri?.ToString();
            if (_holdUntil is not null)
            {
                await _holdUntil;
            }

            return NextResponse();
        }
    }
}
