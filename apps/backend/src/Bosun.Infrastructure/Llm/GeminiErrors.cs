using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Bosun.Infrastructure.Llm;

/// <summary>
/// Maps Gemini failures onto the stable llm_* codes (plan s7.8). Real responses recorded on this machine:
/// <list type="bullet">
/// <item>a wrong AIza-style key: HTTP 400, status INVALID_ARGUMENT, reason API_KEY_INVALID, "API key not valid. Please pass a valid API key."</item>
/// <item>a wrong AQ.-style key: HTTP 401, status UNAUTHENTICATED, reason ACCESS_TOKEN_TYPE_UNSUPPORTED, the same message</item>
/// <item>no key at all: HTTP 403, status PERMISSION_DENIED, "Method doesn't allow unregistered callers"</item>
/// </list>
/// so auth failure is detected from status, reason AND message, never from "401" alone.
/// </summary>
public static class GeminiErrors
{
    public static LlmException FromHttp(HttpStatusCode status, string body, TimeSpan? retryAfterHeader, int attempts, params string?[] secrets)
    {
        var error = ParseError(body);
        var code = (int)status;
        var message = Redact(error.Message ?? $"HTTP {code}", secrets);
        var retryAfter = error.RetryDelay ?? retryAfterHeader;

        if (code == 429)
        {
            var quota = error.Reasons.Contains("RATE_LIMIT_EXCEEDED") ? "rate limit" : "quota";
            var wait = retryAfter is { } r ? $" Retry in about {Math.Ceiling(r.TotalSeconds)} s." : "";
            return new LlmException(LlmErrorCodes.RateLimited,
                $"Gemini {quota} exceeded (HTTP 429).{wait} Provider message: {Shorten(message)}", code, retryAfter, attempts);
        }

        if (IsAuthFailure(code, error))
        {
            return new LlmException(LlmErrorCodes.AuthFailed,
                $"Gemini rejected the API key (HTTP {code}{(error.Reasons.Count > 0 ? ", " + error.Reasons[0] : "")}). Check the key variable in .env (GEMINI_LLM_API_KEY for text, GEMINI_EMBEDDING_API_KEY for embeddings). Provider message: {Shorten(message)}",
                code, null, attempts);
        }

        if (code is 408 or 504)
        {
            return new LlmException(LlmErrorCodes.Timeout, $"Gemini timed out (HTTP {code}). Try again, or lower the thinking level. Provider message: {Shorten(message)}", code, retryAfter, attempts);
        }

        if (code >= 500)
        {
            return new LlmException(LlmErrorCodes.Unavailable,
                $"Gemini is temporarily unavailable (HTTP {code}{(error.Status is null ? "" : " " + error.Status)}). Provider message: {Shorten(message)}", code, retryAfter, attempts);
        }

        return new LlmException(LlmErrorCodes.BadRequest,
            $"Gemini rejected the request (HTTP {code}{(error.Status is null ? "" : " " + error.Status)}): {Shorten(message)}", code, null, attempts);
    }

    /// <summary>Maps a transport-level exception; null when it is not one of ours (the caller rethrows).</summary>
    public static LlmException? FromTransport(Exception exception, int attempts, TimeSpan? breakerRetryAfter = null) => exception switch
    {
        BrokenCircuitException => new LlmException(
            LlmErrorCodes.Unavailable,
            "Gemini calls are paused: too many recent failures opened the circuit breaker. Retrying automatically shortly.",
            null,
            breakerRetryAfter,
            attempts,
            exception),
        TimeoutRejectedException => new LlmException(
            LlmErrorCodes.Timeout,
            "Gemini did not answer in time (every attempt timed out). Try again, or lower the thinking level.",
            null,
            null,
            attempts,
            exception),
        TaskCanceledException => new LlmException(LlmErrorCodes.Timeout, "The call to Gemini timed out.", null, null, attempts, exception),
        HttpRequestException http => new LlmException(
            LlmErrorCodes.Unavailable,
            $"Gemini could not be reached ({http.HttpRequestError}). Check the network connection.",
            null,
            null,
            attempts,
            exception),
        _ => null,
    };

    public static TimeSpan? RetryAfterHeader(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        // A date in the past means "now", not a negative wait.
        return header?.Date is { } date ? Max0(date - DateTimeOffset.UtcNow) : null;
    }

    /// <summary>The wait Gemini asks for: Retry-After header, else error.details[].retryDelay ("33s").</summary>
    public static TimeSpan? RetryDelay(HttpResponseMessage response, string body) =>
        (ParseError(body).RetryDelay is { } parsed ? Max0(parsed) : (TimeSpan?)null) ?? RetryAfterHeader(response);

    private static TimeSpan Max0(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    public static string Redact(string text, params string?[] secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static bool IsAuthFailure(int code, ParsedError error) =>
        code is 401 or 403
        || error.Reasons.Any(r => r.StartsWith("API_KEY_", StringComparison.Ordinal) || r == "ACCESS_TOKEN_TYPE_UNSUPPORTED")
        || (error.Message?.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Shorten(string message) => message.Length <= 300 ? message : message[..300] + "...";

    private sealed record ParsedError(string? Message, string? Status, TimeSpan? RetryDelay, List<string> Reasons);

    private static ParsedError ParseError(string body)
    {
        var reasons = new List<string>();
        string? message = null;
        string? status = null;
        TimeSpan? delay = null;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            message = error?["message"]?.GetValue<string>();
            status = error?["status"]?.GetValue<string>();
            if (error?["details"] is JsonArray details)
            {
                foreach (var detail in details)
                {
                    if (detail?["reason"]?.GetValue<string>() is { } reason)
                    {
                        reasons.Add(reason);
                    }

                    if (detail?["retryDelay"]?.GetValue<string>() is { } text && TryParseDuration(text, out var parsed))
                    {
                        delay = parsed;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // not a Google error envelope (an HTML proxy page, say): the status code alone decides
        }

        return new ParsedError(message, status, delay, reasons);
    }

    /// <summary>Parses protobuf durations such as "33s", "0.5s" or "33.123456789s".</summary>
    public static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;
        var trimmed = text.Trim();
        if (!trimmed.EndsWith('s') || !double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
