using Bosun.Application;
using Bosun.Application.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Agent;

/// <summary>What the model is asked to return for the probe.</summary>
public sealed record PingModelOutput(bool Ok, string Echo);

public sealed record PingUsage(int PromptTokens, int CandidateTokens, int ThoughtTokens, int TotalTokens);

/// <summary>GET/POST /api/ai/ping response.</summary>
public sealed record PingResponse(
    bool Ok,
    string Model,
    string Echo,
    string PromptVersion,
    string FinishReason,
    PingUsage Usage,
    int LatencyMs,
    int Attempts,
    int ExtraCalls,
    double EstimatedCostUsd);

/// <summary>
/// The end-to-end proof for Phase 3: prompt file -> structured call against LLM_MODEL -> schema-validated reply. It
/// exercises exactly the path the agent and extraction use (StructuredLlm, retries, logging, llm_calls).
/// </summary>
public sealed class PingService(StructuredLlm structured, PromptLibrary prompts)
{
    public const string Feature = "ping";
    public const int MaxEchoLength = 64;

    public async Task<PingResponse> PingAsync(string? echo, CancellationToken ct)
    {
        echo = string.IsNullOrWhiteSpace(echo) ? "bosun-ping" : echo.Trim();
        if (echo.Length > MaxEchoLength || echo.Any(c => char.IsControl(c) || c is '<' or '>'))
        {
            throw AppException.Invalid($"echo must be at most {MaxEchoLength} characters, without control characters or angle brackets.");
        }

        var prompt = prompts.Get("ping.system");
        var result = await structured.GenerateAsync<PingModelOutput>(
            new LlmRequest
            {
                Feature = Feature,
                PromptVersion = prompt.Version,
                SystemInstruction = prompt.Text,
                Messages = [LlmMessage.User($"<data>{echo}</data>")],
                Temperature = 0,
                Thinking = LlmThinking.Minimal,
            },
            output => output.Echo == echo ? null : $"echo must be exactly \"{echo}\".",
            ct);

        var last = result.Last;
        var usage = result.Usage;
        return new PingResponse(
            result.Value.Ok,
            last.Model,
            result.Value.Echo,
            prompt.Version,
            last.FinishReasonRaw ?? last.FinishReason.ToString(),
            new PingUsage(usage.PromptTokens, usage.CandidateTokens, usage.ThoughtTokens, usage.TotalTokens),
            (int)result.Calls.Sum(c => c.Latency.TotalMilliseconds),
            result.Calls.Sum(c => c.Attempts),
            result.ExtraCalls,
            result.EstimatedCostUsd);
    }
}
