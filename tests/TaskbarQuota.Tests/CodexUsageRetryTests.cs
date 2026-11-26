using System.Net;
using System.Net.Http.Headers;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class CodexUsageRetryTests
{
    private const string BaseUrl = "https://example.test/backend-api";
    private static readonly CodexProvider.Credentials Credentials = new("test-token", "test-account");

    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    [InlineData("503")]
    public async Task TransientUsageFailureRetriesOnceAndRecovers(string failure)
    {
        using var handler = new UsageHandler(attempt => attempt != 1 ? QuotaResponse() : failure switch
        {
            "connection" => throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic disconnect"),
            "timeout" => throw new TaskCanceledException("Synthetic HTTP timeout", new TimeoutException()),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        });
        using var http = new HttpClient(handler);

        var result = await new CodexProvider(http).FetchUsageAsync(Credentials, BaseUrl);

        Assert.Equal(2, handler.UsageAttempts);
        Assert.Equal(1, handler.ResetCreditsAttempts);
        Assert.Equal(37, result.Usage.Primary.UsedPercent);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, 2, ProviderErrorKind.Other)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, 1, ProviderErrorKind.Other)]
    [InlineData(HttpStatusCode.Unauthorized, false, 1, ProviderErrorKind.AuthRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, false, 1, ProviderErrorKind.RateLimited)]
    public async Task FailedUsageDoesNotLoopOrRetryBeforeServerAllowsIt(
        HttpStatusCode status, bool retryAfter, int attempts, ProviderErrorKind expectedKind)
    {
        using var handler = new UsageHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ProviderException>(() => new CodexProvider(http).FetchUsageAsync(Credentials, BaseUrl));

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal(attempts, handler.UsageAttempts);
        Assert.Equal(0, handler.ResetCreditsAttempts);
    }

    [Fact]
    public async Task CallerCancellationPreventsAnotherUsageRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new UsageHandler(_ =>
        {
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CodexProvider(http).FetchUsageAsync(Credentials, BaseUrl, cancellation.Token));

        Assert.Equal(1, handler.UsageAttempts);
        Assert.Equal(0, handler.ResetCreditsAttempts);
    }

    private static HttpResponseMessage QuotaResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"rate_limit":{"primary_window":{"used_percent":37}}}"""),
    };

    private sealed class UsageHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int UsageAttempts { get; private set; }
        public int ResetCreditsAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
                return Task.FromResult(respond(++UsageAttempts));

            ResetCreditsAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
