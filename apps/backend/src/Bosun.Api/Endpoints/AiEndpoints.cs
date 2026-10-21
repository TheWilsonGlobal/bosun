using System.Globalization;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;

namespace Bosun.Api;

public static class AiEndpoints
{
    /// <summary>
    /// <c>/api/ai/*</c>. /usage is open in Development like the configurator reads and behind a session everywhere else (it reveals
    /// spend). /ping SPENDS tokens and is reachable by any web page on the machine (loopback), so it needs a session in EVERY environment.
    /// </summary>
    public static void MapAi(this WebApplication app, bool requireLogin)
    {
        var group = app.MapGroup("/api/ai").WithTags("AI");
        if (requireLogin)
        {
            group.RequireAuthorization();
        }

        app.MapGroup("/api/ai").WithTags("AI").RequireAuthorization()
            .MapMethods("/ping", ["GET", "POST"], async (string? echo, PingService ping, CancellationToken ct) =>
                TypedResults.Ok(await ping.PingAsync(echo, ct)))
            .WithSummary("One structured LLM call against LLM_MODEL (spends a few hundred tokens). Needs a session in every environment (401 unauthorized). Errors carry llm_* codes.");

        group.MapGet("/usage", async (string? from, string? to, LlmUsageQueries usage, CancellationToken ct) =>
                TypedResults.Ok(await usage.SummariseAsync(ParseInstant(from, "from"), ParseInstant(to, "to"), ct)))
            .WithSummary("Calls, tokens, latency and ESTIMATED cost per feature and model, from the llm_calls table. from (inclusive) / to (exclusive): ISO-8601, UTC when no offset is given.");
    }

    private static DateTime? ParseInstant(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : throw AppException.Invalid($"{name} must be an ISO-8601 date or date-time.");
    }
}
