namespace Bosun.Infrastructure.Llm;

/// <summary>
/// "Llm" configuration section. Mapped from the root .env (LLM_PROVIDER, LLM_MODEL, GEMINI_LLM_API_KEY); everything
/// else has a default here and can be overridden in appsettings. A class, not a record, so the key can never be
/// printed by an accidental ToString().
/// </summary>
public sealed class LlmOptions
{
    public string Provider { get; set; } = "gemini";
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>
    /// Output budget when a request sets none. Reasoning tokens count against it, so it is deliberately generous (plan s12).
    /// </summary>
    public int DefaultMaxOutputTokens { get; set; } = 8192;

    /// <summary>The ceiling for escalation after a truncated answer.</summary>
    public int MaxOutputTokensCap { get; set; } = 32768;

    /// <summary>Default thinking level when a request sets none: minimal | low | medium | high, or empty for the model default.</summary>
    public string? ThinkingLevel { get; set; } = "minimal";

    /// <summary>Token budget instead of a level, for models that take one (2.5 family). Wins over <see cref="ThinkingLevel"/>.</summary>
    public int? ThinkingBudget { get; set; }

    public ResilienceSettings Resilience { get; set; } = new();
}

/// <summary>"Embedding" section: EMBEDDING_PROVIDER, EMBEDDING_MODEL, EMBEDDING_DIMS, GEMINI_EMBEDDING_API_KEY (a separate key).</summary>
public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "gemini";
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";
    public int Dimensions { get; set; } = 768;

    /// <summary>The API accepts at most 100 texts per batch request.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>Retry, timeout and circuit-breaker settings for both Gemini HTTP clients ("Llm:Resilience").</summary>
public sealed class ResilienceSettings
{
    /// <summary>Retries after the first attempt, for 429 and 5xx. Jittered exponential backoff; Retry-After / retryDelay wins when present.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    public int BaseDelayMs { get; set; } = 1000;

    /// <summary>A 429 asking for a longer wait than this is not retried (daily quota will not recover in seconds): it surfaces as llm_rate_limited.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 20;

    /// <summary>
    /// Extra attempts for a 429 whose provider wait is longer than <see cref="MaxRetryDelaySeconds"/> (a "daily quota" answer). Off (0) by
    /// default: normally such a 429 is surfaced at once as llm_rate_limited. The free tier has been seen to FLAP: after the daily limit
    /// was reported, roughly every second request still succeeded seconds later. Set it (Llm__Resilience__QuotaRetryAttempts) for
    /// live evals, never to hide a genuinely spent quota: the attempts are bounded and the error text is unchanged when they run out.
    /// </summary>
    public int QuotaRetryAttempts { get; set; }

    /// <summary>Fixed pause between those attempts.</summary>
    public double QuotaRetryDelaySeconds { get; set; } = 6;

    public double AttemptTimeoutSeconds { get; set; } = 60;
    public double TotalTimeoutSeconds { get; set; } = 150;

    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerMinimumThroughput { get; set; } = 5;
    public double BreakerSamplingSeconds { get; set; } = 60;
    public double BreakerBreakSeconds { get; set; } = 30;
}

/// <summary>Price table section "Llm:Pricing", loaded from llm-pricing.json. All numbers are ESTIMATES in USD per million tokens.</summary>
public sealed class PricingOptions
{
    public string? Note { get; set; }
    public ModelPrice? Default { get; set; }
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelPrice
{
    public double InputPerMillionUsd { get; set; }

    /// <summary>Applies to visible output AND reasoning tokens.</summary>
    public double OutputPerMillionUsd { get; set; }
}
