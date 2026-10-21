using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Bosun.Infrastructure.Llm;

/// <summary>Counts the HTTP attempts one logical call needed (carried on the request, incremented inside the retry loop).</summary>
public sealed class AttemptCounter
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Increment() => Interlocked.Increment(ref count);
}

/// <summary>Sits INSIDE the resilience pipeline, so it sees every attempt including retries.</summary>
internal sealed class AttemptCountingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(GeminiHttp.CounterKey, out var counter))
        {
            counter.Increment();
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class GeminiHttp
{
    /// <summary>Named HttpClient for text/vision/tool calls. Tests replace its primary handler with a fake.</summary>
    public const string LlmClientName = "bosun-gemini-llm";

    /// <summary>Named HttpClient for embeddings (a different API key, the same resilience pipeline).</summary>
    public const string EmbeddingClientName = "bosun-gemini-embedding";

    public const string ApiKeyHeader = "x-goog-api-key";

    internal static readonly HttpRequestOptionsKey<AttemptCounter> CounterKey = new("bosun.attempts");

    public static HttpRequestMessage NewRequest(Uri url, string json, string apiKey, AttemptCounter counter)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // StringContent can be re-sent, which the retry strategy needs.
            Content = new StringContent(json, System.Text.Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        message.Headers.Add(ApiKeyHeader, apiKey); // header, never the query string: URLs end up in logs
        message.Options.Set(CounterKey, counter);
        return message;
    }

    /// <summary>
    /// total timeout -> retry (429/5xx, jittered, honours Retry-After / retryDelay) -> circuit breaker (5xx and
    /// timeouts, not 429) -> per-attempt timeout. HttpClient.Timeout is disabled: the pipeline owns time.
    /// </summary>
    public static IHttpClientBuilder AddGeminiClient(this IServiceCollection services, string name)
    {
        var client = services.AddHttpClient(name, http =>
        {
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bosun/0.1");
        });

        // Registration order is nesting order: the resilience handler first (outer), the attempt counter after it (inner),
        // so the counter sees every retry.
        client.AddResilienceHandler(name, (builder, context) =>
            ConfigurePipeline(builder, context.ServiceProvider.GetRequiredService<IOptions<LlmOptions>>().Value.Resilience));
        client.AddHttpMessageHandler(() => new AttemptCountingHandler());
        return client;
    }

    public static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder, ResilienceSettings settings)
    {
        var maxWait = TimeSpan.FromSeconds(settings.MaxRetryDelaySeconds);

        builder.AddTimeout(TimeSpan.FromSeconds(settings.TotalTimeoutSeconds));

        if (settings.MaxRetryAttempts >= 1) // Polly rejects 0; "no retries" is simply no retry strategy
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = Math.Max(settings.MaxRetryAttempts, settings.QuotaRetryAttempts),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(settings.BaseDelayMs),
                ShouldHandle = async args =>
                {
                    if (args.Outcome.Exception is { } exception)
                    {
                        return exception is HttpRequestException or TimeoutRejectedException;
                    }

                    var response = args.Outcome.Result!;
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // A quota reset hours away is not worth waiting for: surface it instead of burning the time budget.
                        var wait = await RetryDelayAsync(response, args.Context.CancellationToken);
                        if (wait is null || wait <= maxWait)
                        {
                            return true;
                        }

                        // opt-in: a "daily quota" answer is retried a bounded number of times (the free tier flaps)
                        return args.AttemptNumber < settings.QuotaRetryAttempts;
                    }

                    return (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
                },
                DelayGenerator = async args =>
                {
                    if (args.Outcome.Result is { } response && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                    {
                        // Honour the provider's wait (plus a little jitter so parallel callers do not stampede), else fall back to backoff.
                        if (await RetryDelayAsync(response, args.Context.CancellationToken) is { } wait)
                        {
                            return CapWait(wait, maxWait, response.StatusCode == HttpStatusCode.TooManyRequests, settings)
                                + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
                        }
                    }

                    return null;
                },
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = settings.BreakerFailureRatio,
            MinimumThroughput = settings.BreakerMinimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(settings.BreakerSamplingSeconds),
            BreakDuration = TimeSpan.FromSeconds(settings.BreakerBreakSeconds),
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is { } exception
                    ? exception is HttpRequestException or TimeoutRejectedException
                    : (int)args.Outcome.Result!.StatusCode >= 500),
        });

        builder.AddTimeout(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds));
    }

    /// <summary>
    /// The pause before a retry: never negative (a Retry-After date in the past), never longer than <paramref name="maxWait"/> for a
    /// 5xx (a server asking for an hour must not park a request for an hour), and for a 429 the fixed quota delay when quota retries
    /// are on and the provider asked for longer than the cap.
    /// </summary>
    public static TimeSpan CapWait(TimeSpan wait, TimeSpan maxWait, bool isRateLimit, ResilienceSettings settings)
    {
        if (wait < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (wait <= maxWait)
        {
            return wait;
        }

        return isRateLimit && settings.QuotaRetryAttempts > 0 ? TimeSpan.FromSeconds(settings.QuotaRetryDelaySeconds) : maxWait;
    }

    private static async ValueTask<TimeSpan?> RetryDelayAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(ct); // buffered: the adapter reads the same body again afterwards
        var body = await response.Content.ReadAsStringAsync(ct);
        return GeminiErrors.RetryDelay(response, body);
    }
}
