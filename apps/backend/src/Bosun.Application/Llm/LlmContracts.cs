using System.Text.Json.Nodes;

namespace Bosun.Application.Llm;

// The provider-neutral LLM port (ADR-004). Application code and the agent loop only see these types; the Gemini
// adapter in Bosun.Infrastructure maps them onto the REST API, and a second adapter (OpenAI-compatible) could follow.

public enum LlmRole
{
    User,
    Model,
}

/// <summary>One piece of a message. Function calls/responses are how tools are exchanged (Phase 4 agent loop).</summary>
public abstract record LlmPart;

/// <summary>
/// Plain text. <paramref name="ThoughtSignature"/> is the opaque token Gemini 3 attaches to model parts: send the model's
/// own parts back unchanged (see <see cref="LlmResponse.ToMessage"/>) or function calling degrades.
/// </summary>
public sealed record TextPart(string Text, string? ThoughtSignature = null) : LlmPart;

/// <summary>Vision input: raw image bytes (PNG/JPEG/WEBP...), sent inline (keep the request under ~20 MB).</summary>
public sealed record ImagePart(byte[] Data, string MimeType) : LlmPart;

/// <summary>The model asks the host to run a tool.</summary>
public sealed record FunctionCallPart(string Name, JsonObject Args, string? Id = null, string? ThoughtSignature = null) : LlmPart;

/// <summary>The host answer to a <see cref="FunctionCallPart"/>. Non-object results are wrapped as {"result": value}.</summary>
public sealed record FunctionResponsePart(string Name, JsonNode? Response, string? Id = null) : LlmPart;

public sealed record LlmMessage(LlmRole Role, IReadOnlyList<LlmPart> Parts)
{
    public static LlmMessage User(string text) => new(LlmRole.User, [new TextPart(text)]);

    public static LlmMessage User(params LlmPart[] parts) => new(LlmRole.User, parts);

    public static LlmMessage Model(string text) => new(LlmRole.Model, [new TextPart(text)]);

    /// <summary>The user-role turn that carries tool results back to the model.</summary>
    public static LlmMessage ToolResults(IEnumerable<FunctionResponsePart> results) => new(LlmRole.User, results.ToList());
}

public enum LlmThinkingLevel
{
    Minimal,
    Low,
    Medium,
    High,
}

/// <summary>
/// How much the model may reason before answering. Reasoning ("thought") tokens are billed as output AND count against
/// maxOutputTokens, so they can starve the answer (plan s12). Gemini 3.x models take a level; 2.5 models take a token
/// budget; the API rejects both at once, and gemini-3.5-flash-lite rejects a budget of 0 (reasoning cannot be turned off).
/// </summary>
public sealed record LlmThinking(LlmThinkingLevel? Level = null, int? Budget = null)
{
    public static LlmThinking Minimal { get; } = new(LlmThinkingLevel.Minimal);

    public static LlmThinking Low { get; } = new(LlmThinkingLevel.Low);

    public static LlmThinking Medium { get; } = new(LlmThinkingLevel.Medium);

    public static LlmThinking High { get; } = new(LlmThinkingLevel.High);

    public static LlmThinking WithBudget(int tokens) => new(null, tokens);
}

/// <summary>Constrains the answer to JSON matching <paramref name="Schema"/> (a JSON Schema document).</summary>
public sealed record LlmJsonSchema(string Name, JsonNode Schema);

/// <summary>A function the model may call. <paramref name="ParametersSchema"/> is a JSON Schema object (null = no parameters).</summary>
public sealed record LlmToolDeclaration(string Name, string Description, JsonNode? ParametersSchema = null);

public enum LlmToolMode
{
    /// <summary>The model decides whether to call a tool.</summary>
    Auto,

    /// <summary>The model must call one of the (allowed) tools.</summary>
    Any,

    /// <summary>The model must not call tools.</summary>
    None,
}

public sealed record LlmRequest
{
    /// <summary>Product feature making the call (logged and aggregated in /api/ai/usage).</summary>
    public required string Feature { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    /// <summary>"{prompt}@{hash}" of the prompt file(s) behind this call.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>Overrides the configured LLM_MODEL (e.g. a stronger model for hard extraction pages).</summary>
    public string? Model { get; init; }

    public string? SystemInstruction { get; init; }
    public double? Temperature { get; init; }

    /// <summary>Includes reasoning tokens. Null = the configured generous default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Null = the configured default (Llm:Thinking).</summary>
    public LlmThinking? Thinking { get; init; }

    public LlmJsonSchema? ResponseSchema { get; init; }
    public IReadOnlyList<LlmToolDeclaration>? Tools { get; init; }
    public LlmToolMode ToolMode { get; init; } = LlmToolMode.Auto;

    /// <summary>With <see cref="LlmToolMode.Any"/>: restrict the choice to these tool names.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Links the call to an agent run in llm_calls (Phase 4).</summary>
    public int? AgentRunId { get; init; }
}

public enum LlmFinishReason
{
    Stop,

    /// <summary>The output budget ran out (answer, or reasoning + answer, was cut off): retry with a larger budget.</summary>
    MaxTokens,
    Other,
}

/// <param name="PromptTokens">Input tokens.</param>
/// <param name="CandidateTokens">Visible output tokens (excludes reasoning).</param>
/// <param name="ThoughtTokens">Reasoning tokens (billed as output).</param>
public sealed record LlmUsage(int PromptTokens, int CandidateTokens, int ThoughtTokens, int TotalTokens, int CachedTokens = 0)
{
    public static LlmUsage Zero { get; } = new(0, 0, 0, 0);

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) =>
        new(a.PromptTokens + b.PromptTokens, a.CandidateTokens + b.CandidateTokens, a.ThoughtTokens + b.ThoughtTokens, a.TotalTokens + b.TotalTokens, a.CachedTokens + b.CachedTokens);
}

public sealed record LlmResponse
{
    /// <summary>The model that actually answered (the provider modelVersion, else the requested model).</summary>
    public required string Model { get; init; }

    /// <summary>The model content, thought signatures included.</summary>
    public required IReadOnlyList<LlmPart> Parts { get; init; }

    public required LlmFinishReason FinishReason { get; init; }
    public string? FinishReasonRaw { get; init; }
    public required LlmUsage Usage { get; init; }
    public required TimeSpan Latency { get; init; }

    /// <summary>HTTP attempts, retries included (1 = first try worked).</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>ESTIMATE in USD from the configurable price table; null when no recorder priced it.</summary>
    public double? EstimatedCostUsd { get; init; }

    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));

    public IReadOnlyList<FunctionCallPart> FunctionCalls => Parts.OfType<FunctionCallPart>().ToList();

    /// <summary>The model turn to append to the history before sending tool results (keeps thought signatures intact).</summary>
    public LlmMessage ToMessage() => new(LlmRole.Model, Parts);
}

public interface ILlmClient
{
    string Provider { get; }

    /// <summary>The configured LLM_MODEL.</summary>
    string DefaultModel { get; }

    /// <summary>Generous default output budget (reasoning tokens included) used when a request sets none.</summary>
    int DefaultMaxOutputTokens { get; }

    /// <summary>Upper bound for budget escalation after a truncated answer.</summary>
    int MaxOutputTokensCap { get; }

    /// <exception cref="LlmException">Any provider failure, mapped to a stable code (see <see cref="LlmErrorCodes"/>).</exception>
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken);
}
