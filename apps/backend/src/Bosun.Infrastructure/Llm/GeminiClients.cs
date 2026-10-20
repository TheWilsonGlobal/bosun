using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bosun.Application.Llm;
using Microsoft.Extensions.Options;

namespace Bosun.Infrastructure.Llm;

internal static partial class GeminiRequests
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex ModelNamePattern();

    public static string RequireKey(string? key, string variable)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LlmException(LlmErrorCodes.NotConfigured, $"No API key is configured: set {variable} in the root .env.", attempts: 0);
        }

        return key.Trim();
    }

    public static void RequireProvider(string provider, string variable)
    {
        if (!provider.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmException(LlmErrorCodes.NotConfigured, $"{variable}='{provider}' is not supported yet: only 'gemini' is implemented.", attempts: 0);
        }
    }

    /// <summary>Validates the model id before it becomes part of a URL path (it can come from configuration or a request override).</summary>
    public static string ModelId(string? model, string variable)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new LlmException(LlmErrorCodes.NotConfigured, $"No model is configured: set {variable} in the root .env.", attempts: 0);
        }

        var id = model.Trim();
        if (id.StartsWith("models/", StringComparison.Ordinal))
        {
            id = id["models/".Length..];
        }

        return ModelNamePattern().IsMatch(id) ? id : throw new LlmException(LlmErrorCodes.BadRequest, $"'{model}' is not a valid model id.", attempts: 0);
    }

    public static Uri Endpoint(string baseUrl, string model, string method) =>
        new(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), $"models/{model}:{method}");

    /// <summary>Sends, reads, and turns every failure into an <see cref="LlmException"/>. Returns the parsed 200 body.</summary>
    public static async Task<JsonNode> SendAsync(
        IHttpClientFactory factory, string clientName, Uri url, JsonObject body, string apiKey, AttemptCounter counter, CancellationToken ct)
    {
        try
        {
            using var message = GeminiHttp.NewRequest(url, body.ToJsonString(), apiKey, counter);
            using var http = factory.CreateClient(clientName);
            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw GeminiErrors.FromHttp(response.StatusCode, text, GeminiErrors.RetryAfterHeader(response), counter.Count, apiKey);
            }

            try
            {
                return JsonNode.Parse(text) ?? throw new JsonException("empty body");
            }
            catch (JsonException ex)
            {
                throw new LlmException(LlmErrorCodes.Unavailable, "Gemini answered with a body that is not JSON.", (int)response.StatusCode, null, counter.Count, ex);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LlmException && GeminiErrors.FromTransport(ex, counter.Count) is { } mapped)
        {
            throw mapped;
        }
    }
}

/// <summary>Gemini generateContent adapter: text, structured JSON, vision, tool calling. Thin by design (ADR-004 deviation: REST, not the SDK).</summary>
public sealed class GeminiLlmClient(IHttpClientFactory httpFactory, IOptions<LlmOptions> options) : ILlmClient
{
    public string Provider => options.Value.Provider;

    public string DefaultModel => options.Value.Model ?? "";

    public int DefaultMaxOutputTokens => options.Value.DefaultMaxOutputTokens;

    public int MaxOutputTokensCap => options.Value.MaxOutputTokensCap;

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        GeminiRequests.RequireProvider(o.Provider, "LLM_PROVIDER");
        var key = GeminiRequests.RequireKey(o.ApiKey, "GEMINI_LLM_API_KEY");
        var model = GeminiRequests.ModelId(request.Model ?? o.Model, "LLM_MODEL");

        var counter = new AttemptCounter();
        var watch = Stopwatch.StartNew();
        var root = await GeminiRequests.SendAsync(
            httpFactory,
            GeminiHttp.LlmClientName,
            GeminiRequests.Endpoint(o.BaseUrl, model, "generateContent"),
            GeminiWire.BuildGenerateContent(request, o),
            key,
            counter,
            cancellationToken);
        watch.Stop();

        return GeminiWire.ParseGenerateContent(root, model, watch.Elapsed, Math.Max(counter.Count, 1));
    }
}

/// <summary>Gemini batchEmbedContents adapter: batches of up to 100, outputDimensionality = EMBEDDING_DIMS, L2-normalised.</summary>
public sealed class GeminiEmbeddingClient(IHttpClientFactory httpFactory, IOptions<EmbeddingOptions> options) : IEmbeddingClient
{
    public string Model => options.Value.Model ?? "";

    public int Dimensions => options.Value.Dimensions;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingTask task, string feature, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (texts.Count == 0)
        {
            return new EmbeddingResult([], o.Dimensions, o.Model ?? "", 0, TimeSpan.Zero, 0);
        }

        GeminiRequests.RequireProvider(o.Provider, "EMBEDDING_PROVIDER");
        var key = GeminiRequests.RequireKey(o.ApiKey, "GEMINI_EMBEDDING_API_KEY");
        var model = GeminiRequests.ModelId(o.Model, "EMBEDDING_MODEL");
        var url = GeminiRequests.Endpoint(o.BaseUrl, model, "batchEmbedContents");
        var batchSize = Math.Clamp(o.BatchSize, 1, 100);

        var vectors = new List<float[]>(texts.Count);
        var promptTokens = 0;
        var attempts = 0;
        var watch = Stopwatch.StartNew();

        for (var start = 0; start < texts.Count; start += batchSize)
        {
            var requests = new JsonArray();
            foreach (var text in texts.Skip(start).Take(batchSize))
            {
                requests.Add(new JsonObject
                {
                    ["model"] = $"models/{model}",
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                    ["taskType"] = task == EmbeddingTask.Query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT",
                    ["outputDimensionality"] = o.Dimensions,
                });
            }

            var counter = new AttemptCounter();
            var root = await GeminiRequests.SendAsync(
                httpFactory, GeminiHttp.EmbeddingClientName, url, new JsonObject { ["requests"] = requests }, key, counter, cancellationToken);
            attempts += Math.Max(counter.Count, 1);

            var embeddings = root["embeddings"] as JsonArray;
            if (embeddings is null || embeddings.Count != requests.Count)
            {
                throw new LlmException(LlmErrorCodes.MalformedOutput,
                    $"Gemini returned {embeddings?.Count ?? 0} embeddings for {requests.Count} texts.", attempts: attempts);
            }

            foreach (var embedding in embeddings)
            {
                var values = (embedding?["values"] as JsonArray)?.Select(v => v!.GetValue<float>()).ToArray();
                if (values is null || values.Length != o.Dimensions)
                {
                    throw new LlmException(LlmErrorCodes.MalformedOutput,
                        $"Gemini returned an embedding with {values?.Length ?? 0} dimensions; EMBEDDING_DIMS is {o.Dimensions}.", attempts: attempts);
                }

                vectors.Add(VectorMath.L2Normalize(values));
            }

            promptTokens += GeminiWire.ParseUsage(root["usageMetadata"]).PromptTokens;
        }

        watch.Stop();
        return new EmbeddingResult(vectors, o.Dimensions, model, promptTokens, watch.Elapsed, attempts);
    }
}
