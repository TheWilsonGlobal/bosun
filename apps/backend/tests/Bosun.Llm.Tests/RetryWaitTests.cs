using System.Net;
using System.Net.Http.Headers;
using Bosun.Infrastructure.Llm;

namespace Bosun.Llm.Tests;

/// <summary>Reviewer finding 9: a negative Retry-After is a zero wait, and a 5xx cannot park a request for as long as the server asks.</summary>
public sealed class RetryWaitTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(20);

    [Fact]
    public void A_negative_wait_is_zero()
    {
        GeminiHttp.CapWait(TimeSpan.FromSeconds(-30), Cap, isRateLimit: true, new ResilienceSettings()).Should().Be(TimeSpan.Zero);
        GeminiHttp.CapWait(TimeSpan.FromSeconds(-1), Cap, isRateLimit: false, new ResilienceSettings()).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_wait_within_the_cap_is_honoured()
    {
        GeminiHttp.CapWait(TimeSpan.FromSeconds(7), Cap, isRateLimit: false, new ResilienceSettings()).Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void A_5xx_asking_for_an_hour_waits_at_most_the_cap_when_quota_retries_are_off()
    {
        var settings = new ResilienceSettings { QuotaRetryAttempts = 0 };

        GeminiHttp.CapWait(TimeSpan.FromHours(1), Cap, isRateLimit: false, settings).Should().Be(Cap);
        GeminiHttp.CapWait(TimeSpan.FromHours(1), Cap, isRateLimit: true, settings).Should().Be(Cap);
    }

    [Fact]
    public void Only_a_rate_limit_with_quota_retries_on_uses_the_fixed_quota_delay()
    {
        var settings = new ResilienceSettings { QuotaRetryAttempts = 2, QuotaRetryDelaySeconds = 6 };

        GeminiHttp.CapWait(TimeSpan.FromHours(1), Cap, isRateLimit: true, settings).Should().Be(TimeSpan.FromSeconds(6));
        GeminiHttp.CapWait(TimeSpan.FromHours(1), Cap, isRateLimit: false, settings).Should().Be(Cap, "a server error never gets the quota treatment");
    }

    [Fact]
    public void A_Retry_After_header_in_the_past_or_negative_is_zero_not_negative()
    {
        using var past = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-10));
        GeminiErrors.RetryAfterHeader(past).Should().Be(TimeSpan.Zero);

        using var negative = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        negative.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(-5));
        GeminiErrors.RetryAfterHeader(negative).Should().Be(TimeSpan.Zero);

        GeminiErrors.RetryDelay(past, "{}").Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_negative_retryDelay_in_the_error_body_is_zero_too()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        const string body = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"x","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"-3s"}]}}""";

        var delay = GeminiErrors.RetryDelay(response, body);

        (delay is null || delay >= TimeSpan.Zero).Should().BeTrue("never negative");
    }
}
