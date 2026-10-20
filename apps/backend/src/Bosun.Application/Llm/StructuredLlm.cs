using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Bosun.Application.Llm;

/// <summary>JSON schema generation and strict parsing for model output.</summary>
public static class StructuredJson
{
    private static readonly ConcurrentDictionary<Type, JsonNode> Schemas = new();

    /// <summary>
    /// Parsing is strict about shape (missing required members and nulls in non-nullable members fail, enums must be
    /// names) but lenient about member-name case: a model answering "Echo" for "echo" is not worth a repair round-trip.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    /// <summary>JSON Schema for <typeparamref name="T"/> generated from the C# type (records give the strictest schema).</summary>
    public static JsonNode SchemaFor<T>() => SchemaFor(typeof(T));

    public static JsonNode SchemaFor(Type type) =>
        Schemas.GetOrAdd(type, t => JsonSchemaExporter.GetJsonSchemaAsNode(Options, t, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }));

    /// <summary>Removes a Markdown code fence a model sometimes wraps around JSON.</summary>
    public static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? trimmed[(firstNewline + 1)..lastFence].Trim() : trimmed;
    }
}

public sealed record StructuredResult<T>(T Value, IReadOnlyList<LlmResponse> Calls)
{
    /// <summary>Everything this logical call cost, retries and repairs included.</summary>
    public LlmUsage Usage => Calls.Aggregate(LlmUsage.Zero, (sum, call) => sum + call.Usage);

    public double EstimatedCostUsd => Calls.Sum(c => c.EstimatedCostUsd ?? 0);

    public LlmResponse Last => Calls[^1];

    /// <summary>Extra model calls beyond the first (truncation retries + the repair).</summary>
    public int ExtraCalls => Calls.Count - 1;
}

/// <summary>
/// Typed structured output over <see cref="ILlmClient"/>: schema from the C# type, strict parse, and the two recoveries
/// plan s12 asks for: a truncated answer (finishReason MAX_TOKENS, usually reasoning tokens eating the budget) is retried
/// with a doubled budget, and malformed or semantically invalid JSON gets exactly one repair turn.
/// </summary>
public sealed class StructuredLlm(ILlmClient client)
{
    public const int MaxTruncationRetries = 2;
    public const int MaxRepairs = 1;

    /// <param name="request">The call; Messages are the conversation so far. ResponseSchema is filled in from <typeparamref name="T"/>.</param>
    /// <param name="validate">Optional semantic check on the parsed value: return an error message, or null when fine.</param>
    public async Task<StructuredResult<T>> GenerateAsync<T>(LlmRequest request, Func<T, string?>? validate = null, CancellationToken cancellationToken = default)
        where T : notnull
    {
        var schema = request.ResponseSchema ?? new LlmJsonSchema(typeof(T).Name, StructuredJson.SchemaFor<T>());
        var messages = request.Messages.ToList();
        var budget = request.MaxOutputTokens ?? client.DefaultMaxOutputTokens;
        var calls = new List<LlmResponse>();
        var truncations = 0;
        var repairs = 0;

        while (true)
        {
            var response = await client.GenerateAsync(request with { Messages = messages, MaxOutputTokens = budget, ResponseSchema = schema }, cancellationToken);
            calls.Add(response);

            if (response.FinishReason == LlmFinishReason.MaxTokens)
            {
                if (truncations < MaxTruncationRetries && budget < client.MaxOutputTokensCap)
                {
                    truncations++;
                    budget = Math.Min(budget * 2, client.MaxOutputTokensCap);
                    continue;
                }

                throw Truncated(response, budget);
            }

            var problem = TryParse(response.Text, validate, out var value);
            if (problem is null)
            {
                return new StructuredResult<T>(value!, calls);
            }

            if (repairs < MaxRepairs)
            {
                repairs++;
                messages.Add(LlmMessage.Model(response.Text));
                messages.Add(LlmMessage.User(
                    $"Your previous reply could not be used: {problem} Reply again with ONLY a JSON value that matches the required schema: no prose and no code fences."));
                continue;
            }

            throw new LlmException(LlmErrorCodes.MalformedOutput,
                $"The model did not return usable JSON even after one repair attempt ({problem}). Try again; if it keeps happening the prompt or schema needs work.",
                attempts: calls.Sum(c => c.Attempts));
        }
    }

    /// <summary>Plain text with the same truncation recovery: a cut-off answer is retried with a larger budget.</summary>
    public async Task<LlmResponse> GenerateTextAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var budget = request.MaxOutputTokens ?? client.DefaultMaxOutputTokens;
        for (var truncations = 0; ; truncations++)
        {
            var response = await client.GenerateAsync(request with { MaxOutputTokens = budget }, cancellationToken);
            if (response.FinishReason != LlmFinishReason.MaxTokens)
            {
                return response;
            }

            if (truncations >= MaxTruncationRetries || budget >= client.MaxOutputTokensCap)
            {
                throw Truncated(response, budget);
            }

            budget = Math.Min(budget * 2, client.MaxOutputTokensCap);
        }
    }

    private static LlmException Truncated(LlmResponse response, int budget) =>
        new(LlmErrorCodes.Truncated,
            $"The answer was cut off at {budget} output tokens ({response.Usage.ThoughtTokens} of them spent on reasoning) and a larger budget did not help. Lower the thinking level or shorten the task.",
            attempts: response.Attempts);

    private static string? TryParse<T>(string text, Func<T, string?>? validate, out T? value)
    {
        value = default;
        var json = StructuredJson.StripFence(text);
        if (json.Length == 0)
        {
            return "the reply was empty.";
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, StructuredJson.Options);
        }
        catch (JsonException ex)
        {
            return $"it is not valid for the schema ({ex.Message}).";
        }

        if (value is null)
        {
            return "the reply was JSON null.";
        }

        return validate?.Invoke(value) is { } semantic ? $"{semantic}" : null;
    }
}
