namespace Bosun.Application.Llm;

/// <summary>Stable problem codes for LLM failures (plan s7.8): each one drives a specific UI state, never a generic error.</summary>
public static class LlmErrorCodes
{
    public const string RateLimited = "llm_rate_limited";
    public const string AuthFailed = "llm_auth_failed";
    public const string SafetyBlocked = "llm_safety_blocked";
    public const string MalformedOutput = "llm_malformed_output";
    public const string Timeout = "llm_timeout";
    public const string Unavailable = "llm_unavailable";
    public const string Truncated = "llm_truncated";

    /// <summary>Beyond the plan: the provider rejected our request itself (a bug, or an unknown model name).</summary>
    public const string BadRequest = "llm_bad_request";

    /// <summary>Beyond the plan: no API key (or an unsupported provider) is configured; nothing was sent.</summary>
    public const string NotConfigured = "llm_not_configured";

    public static IReadOnlyList<string> All { get; } =
        [RateLimited, AuthFailed, SafetyBlocked, MalformedOutput, Timeout, Unavailable, Truncated, BadRequest, NotConfigured];
}

/// <summary>A failed LLM or embedding call. The message is safe to show: it never contains the API key.</summary>
public sealed class LlmException(string code, string message, int? providerStatus = null, TimeSpan? retryAfter = null, int attempts = 1, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;

    /// <summary>The provider HTTP status when there was a response.</summary>
    public int? ProviderStatus { get; } = providerStatus;

    /// <summary>When the provider (or our circuit breaker) says to try again.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>HTTP attempts made; 0 means nothing was sent (not configured, invalid model id).</summary>
    public int Attempts { get; } = attempts;
}
