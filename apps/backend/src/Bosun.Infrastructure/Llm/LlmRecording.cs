using System.Diagnostics;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bosun.Infrastructure.Llm;

/// <summary>Cost ESTIMATES from the configurable price table (llm-pricing.json). Unknown models fall back to Default, then to 0.</summary>
public sealed class LlmPricing(IOptions<PricingOptions> options)
{
    public double Estimate(string model, LlmUsage usage)
    {
        var table = options.Value;

        // exact match, else the longest configured name that prefixes the model (providers answer with "-001"-style suffixes), else Default
        var price = table.Models.TryGetValue(model, out var exact)
            ? exact
            : table.Models.Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault() ?? table.Default;
        if (price is null)
        {
            return 0;
        }

        // Reasoning tokens are billed at the output rate.
        return ((usage.PromptTokens * price.InputPerMillionUsd) + ((usage.CandidateTokens + usage.ThoughtTokens) * price.OutputPerMillionUsd)) / 1_000_000d;
    }
}

/// <summary>Writes llm_calls rows through a fresh scope (the clients are singletons; the DbContext is scoped). Never throws.</summary>
public sealed class LlmCallRecorder(IServiceScopeFactory scopes, ILogger<LlmCallRecorder> logger) : ILlmCallLog
{
    public async Task RecordAsync(LlmCall call, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
            db.LlmCalls.Add(call);

            // Not the caller's token: a cancelled request still spent the tokens.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the llm_calls row for feature {feature}; the call itself is unaffected.", call.Feature);
        }
    }
}

/// <summary>Logs (Serilog JSON) and persists every text/vision/tool call, successful or not, with an estimated cost.</summary>
public sealed class RecordingLlmClient(ILlmClient inner, ILlmCallLog log, LlmPricing pricing, TimeProvider clock, ILogger<RecordingLlmClient> logger) : ILlmClient
{
    public string Provider => inner.Provider;

    public string DefaultModel => inner.DefaultModel;

    public int DefaultMaxOutputTokens => inner.DefaultMaxOutputTokens;

    public int MaxOutputTokensCap => inner.MaxOutputTokensCap;

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model ?? inner.DefaultModel;
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await inner.GenerateAsync(request, cancellationToken);

            // Rows carry the model we ASKED for (what LLM_MODEL says), so successes and failures group together.
            var cost = pricing.Estimate(model, response.Usage);
            await Record(request, model, response.Usage, response.Latency, response.Attempts, response.FinishReasonRaw, null, cost);
            return response with { EstimatedCostUsd = cost };
        }
        catch (LlmException ex) when (ex.Attempts > 0)
        {
            // Attempts == 0 means nothing left the process (no key, invalid model id): not a call, so not a row.
            await Record(request, model, LlmUsage.Zero, watch.Elapsed, ex.Attempts, null, ex.Code, 0);
            throw;
        }
    }

    private async Task Record(LlmRequest request, string model, LlmUsage usage, TimeSpan latency, int attempts, string? finish, string? errorCode, double cost)
    {
        var ok = errorCode is null;
        logger.LogInformation(
            "llm_call feature={feature} model={model} prompt_version={prompt_version} tokens_in={tokens_in} tokens_out={tokens_out} tokens_thought={tokens_thought} latency_ms={latency_ms} attempts={attempts} finish_reason={finish_reason} status={status} error_code={error_code} cost_usd={cost_usd}",
            request.Feature,
            model,
            request.PromptVersion,
            usage.PromptTokens,
            usage.CandidateTokens,
            usage.ThoughtTokens,
            (int)latency.TotalMilliseconds,
            attempts,
            finish,
            ok ? "ok" : "error",
            errorCode,
            cost);

        await log.RecordAsync(
            new LlmCall
            {
                Timestamp = clock.GetUtcNow().UtcDateTime,
                Kind = "llm",
                Feature = request.Feature,
                Provider = inner.Provider,
                Model = model,
                PromptVersion = request.PromptVersion,
                PromptTokens = usage.PromptTokens,
                CandidateTokens = usage.CandidateTokens,
                ThoughtTokens = usage.ThoughtTokens,
                TotalTokens = usage.TotalTokens,
                LatencyMs = (int)latency.TotalMilliseconds,
                Attempts = attempts,
                FinishReason = finish,
                Succeeded = ok,
                ErrorCode = errorCode,
                CostUsd = cost,
                AgentRunId = request.AgentRunId,
            },
            CancellationToken.None);
    }
}

public sealed class RecordingEmbeddingClient(IEmbeddingClient inner, string provider, ILlmCallLog log, LlmPricing pricing, TimeProvider clock, ILogger<RecordingEmbeddingClient> logger)
    : IEmbeddingClient
{
    public string Model => inner.Model;

    public int Dimensions => inner.Dimensions;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingTask task, string feature, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await inner.EmbedAsync(texts, task, feature, cancellationToken);
            if (texts.Count > 0)
            {
                var usage = new LlmUsage(result.PromptTokens, 0, 0, result.PromptTokens);
                await Record(feature, inner.Model, usage, result.Latency, result.Attempts, null, pricing.Estimate(inner.Model, usage));
            }

            return result;
        }
        catch (LlmException ex) when (ex.Attempts > 0)
        {
            await Record(feature, inner.Model, LlmUsage.Zero, watch.Elapsed, ex.Attempts, ex.Code, 0);
            throw;
        }
    }

    private async Task Record(string feature, string model, LlmUsage usage, TimeSpan latency, int attempts, string? errorCode, double cost)
    {
        logger.LogInformation(
            "embedding_call feature={feature} model={model} tokens_in={tokens_in} latency_ms={latency_ms} attempts={attempts} status={status} error_code={error_code} cost_usd={cost_usd}",
            feature,
            model,
            usage.PromptTokens,
            (int)latency.TotalMilliseconds,
            attempts,
            errorCode is null ? "ok" : "error",
            errorCode,
            cost);

        await log.RecordAsync(
            new LlmCall
            {
                Timestamp = clock.GetUtcNow().UtcDateTime,
                Kind = "embedding",
                Feature = feature,
                Provider = provider,
                Model = model,
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
                LatencyMs = (int)latency.TotalMilliseconds,
                Attempts = attempts,
                Succeeded = errorCode is null,
                ErrorCode = errorCode,
                CostUsd = cost,
            },
            CancellationToken.None);
    }
}
