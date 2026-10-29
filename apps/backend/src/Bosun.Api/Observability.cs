using System.Security.Cryptography;
using System.Text;
using Bosun.Application.Monitoring;
using Bosun.Infrastructure.Llm;
using Bosun.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace Bosun.Api;

public sealed record ReadyDto(string Status, Dictionary<string, string> Checks);

/// <summary>
/// <c>/metrics</c> (bearer token) and <c>/ready</c>, plus HTTP and domain metrics. Each app instance owns its own registry, so
/// integration tests that boot several apps in one process never see each other's numbers.
/// </summary>
public static class Observability
{
    public const int MinTokenLength = 32;

    public static void AddBosunMetrics(this IServiceCollection services) =>
        services.AddSingleton(Metrics.NewCustomRegistry());

    /// <summary>Fail fast on a token that is set but too short to be a secret; unset is fine (the endpoint then does not exist).</summary>
    public static void EnsureSafeMetricsToken(IConfiguration config)
    {
        var token = config["Metrics:Token"];
        if (!string.IsNullOrEmpty(token) && token.Length < MinTokenLength)
        {
            throw new InvalidOperationException($"METRICS_TOKEN must be at least {MinTokenLength} characters (or unset to disable /metrics).");
        }
    }

    /// <summary>Call after routing is available and before the endpoints, so the route pattern can be read when the response completes.</summary>
    public static void UseBosunHttpMetrics(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<CollectorRegistry>();
        DotNetStats.Register(registry);
        var factory = Metrics.WithCustomRegistry(registry);

        app.UseHttpMetrics(o =>
        {
            o.SetMetricFactory(factory);
            // The route PATTERN, never the URL: ids in the path would otherwise create a series per row. Unmatched = static files / 404.
            o.AddCustomLabel("route", ctx => (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched");
        });

        // System.Diagnostics.Metrics instruments (the LLM client's) into this app's registry.
        var meters = MeterAdapter.StartListening(new MeterAdapterOptions { Registry = registry, MetricFactory = factory });
        app.Lifetime.ApplicationStopping.Register(meters.Dispose);

        var state = app.Services.GetRequiredService<MonitoringState>();
        var link = factory.CreateGauge("bosun_link_connected", "1 when the Modbus / NMEA link is up (connected or degraded), 0 when it is not; absent when disabled by configuration", "protocol");
        var liveTags = factory.CreateGauge("bosun_live_tags", "Tags with a live value in the store");
        var build = factory.CreateGauge("app_build_info", "Build identity; always 1", "version", "commit");
        build.WithLabels(typeof(Observability).Assembly.GetName().Version?.ToString(3) ?? "unknown", Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "unknown").Set(1);

        // Read at scrape time. A link that is disabled has nothing to measure, so its series is removed rather than reported as 0.
        registry.AddBeforeCollectCallback(() =>
        {
            foreach (var name in new[] { MonitoringState.ModbusName, MonitoringState.NmeaName })
            {
                var current = state.Connection(name).State;
                if (current == ConnectionState.Disabled)
                {
                    link.RemoveLabelled(name);
                }
                else
                {
                    link.WithLabels(name).Set(current is ConnectionState.Connected or ConnectionState.Degraded ? 1 : 0);
                }
            }

            liveTags.Set(state.Values.Count);
        });
    }

    public static void MapObservability(this WebApplication app)
    {
        var token = app.Configuration["Metrics:Token"];
        var registry = app.Services.GetRequiredService<CollectorRegistry>();

        // Readiness: the database answers. Modbus/NMEA being down is by design (see bosun_link_connected), not unreadiness.
        async Task<Results<Ok<ReadyDto>, JsonHttpResult<ReadyDto>>> Ready(BosunDbContext db, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var database = "fail";
            try
            {
                database = await db.Database.CanConnectAsync(timeout.Token) ? "ok" : "fail";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // a timeout or a broken connection is exactly what readiness reports
            }

            var ok = database == "ok";
            var dto = new ReadyDto(ok ? "ok" : "degraded", new() { ["database"] = database });
            return ok ? TypedResults.Ok(dto) : TypedResults.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        app.MapGet("/ready", Ready).AllowAnonymous().WithTags("Health");
        app.MapGet("/api/ready", Ready).AllowAnonymous().WithTags("Health"); // reachable through the SPA's /api proxy

        // Always mapped: without a route the SPA fallback would answer /metrics with 200 HTML, which looks like success to a prober.
        app.MapGet("/metrics", async (HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(token))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!TokenMatches(ctx.Request.Headers.Authorization.ToString(), token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await registry.CollectAndExportAsTextAsync(ctx.Response.Body, ctx.RequestAborted);
        }).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>Constant-time bearer check: hashing first makes the compared buffers the same length whatever was sent.</summary>
    private static bool TokenMatches(string header, string expected)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sent = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..]));
        return CryptographicOperations.FixedTimeEquals(sent, SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
