using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Llm.Tests;

public sealed record SeenRequest(Uri Url, string Body, bool HasKeyHeader, string? KeyHeader)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>Scripted network: each call pops the next responder. No script left is a test failure, never a silent success.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> script = new();
    private readonly List<SeenRequest> seen = [];

    public IReadOnlyList<SeenRequest> Requests
    {
        get
        {
            lock (seen)
            {
                return seen.ToList();
            }
        }
    }

    public int Calls => Requests.Count;

    public FakeHandler Then(HttpStatusCode status, string body, Action<HttpResponseMessage>? tweak = null) =>
        Then((_, _) => Task.FromResult(Respond(status, body, tweak)));

    public FakeHandler Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        script.Enqueue(responder);
        return this;
    }

    public FakeHandler ThenOk(string text, int prompt = 10, int candidates = 5, int thoughts = 0, string finish = "STOP") =>
        Then(HttpStatusCode.OK, Ok(text, prompt, candidates, thoughts, finish));

    public FakeHandler ThenThrow(Exception exception) => Then((_, _) => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var hasKey = request.Headers.TryGetValues("x-goog-api-key", out var values);
        lock (seen)
        {
            seen.Add(new SeenRequest(request.RequestUri!, body, hasKey, values?.SingleOrDefault()));
        }

        if (!script.TryDequeue(out var responder))
        {
            throw new InvalidOperationException($"FakeHandler has no scripted response for call #{Calls}.");
        }

        return await responder(request, cancellationToken);
    }

    public static HttpResponseMessage Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? tweak = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        tweak?.Invoke(response);
        return response;
    }

    public static string Ok(string text, int prompt = 10, int candidates = 5, int thoughts = 0, string finish = "STOP") =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = finish,
            }),
            ["usageMetadata"] = Usage(prompt, candidates, thoughts),
            ["modelVersion"] = "test-model-v1",
        }.ToJsonString();

    public static JsonObject Usage(int prompt, int candidates, int thoughts) => new()
    {
        ["promptTokenCount"] = prompt,
        ["candidatesTokenCount"] = candidates,
        ["thoughtsTokenCount"] = thoughts,
        ["totalTokenCount"] = prompt + candidates + thoughts,
    };

    public static string Error(int code, string status, string message, params (string Reason, string? RetryDelay)[] details)
    {
        var detailArray = new JsonArray();
        foreach (var (reason, delay) in details)
        {
            detailArray.Add(delay is null
                ? new JsonObject { ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo", ["reason"] = reason }
                : new JsonObject { ["@type"] = "type.googleapis.com/google.rpc.RetryInfo", ["retryDelay"] = delay });
        }

        return new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["status"] = status, ["details"] = detailArray },
        }.ToJsonString();
    }
}

public sealed class MemoryCallLog : ILlmCallLog
{
    public List<LlmCall> Calls { get; } = [];

    public Task RecordAsync(LlmCall call, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The real LLM stack (resilience pipeline, adapters, recorder) on a fake network.</summary>
public sealed class LlmHost : IDisposable
{
    public const string TestKey = "test-key-0123456789abcdef";

    private readonly ServiceProvider provider;

    public FakeHandler Handler { get; }
    public FakeHandler EmbeddingHandler { get; }
    public MemoryCallLog Log { get; } = new();

    public LlmHost(FakeHandler? handler = null, Dictionary<string, string?>? config = null)
    {
        Handler = handler ?? new FakeHandler();
        EmbeddingHandler = new FakeHandler();

        var settings = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "gemini",
            ["Llm:Model"] = "test-model",
            ["Llm:ApiKey"] = TestKey,
            ["Llm:Resilience:BaseDelayMs"] = "1",
            ["Llm:Resilience:MaxRetryAttempts"] = "3",
            ["Llm:Pricing:Default:InputPerMillionUsd"] = "1",
            ["Llm:Pricing:Default:OutputPerMillionUsd"] = "2",
            ["Embedding:Provider"] = "gemini",
            ["Embedding:Model"] = "test-embedding",
            ["Embedding:ApiKey"] = TestKey,
            ["Embedding:Dimensions"] = "4",
        };
        foreach (var (key, value) in config ?? [])
        {
            settings[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddBosunLlm();
        services.AddSingleton<ILlmCallLog>(Log);
        services.AddHttpClient(GeminiHttp.LlmClientName).ConfigurePrimaryHttpMessageHandler(() => Handler);
        services.AddHttpClient(GeminiHttp.EmbeddingClientName).ConfigurePrimaryHttpMessageHandler(() => EmbeddingHandler);
        provider = services.BuildServiceProvider();
    }

    public ILlmClient Llm => provider.GetRequiredService<ILlmClient>();

    public IEmbeddingClient Embeddings => provider.GetRequiredService<IEmbeddingClient>();

    public StructuredLlm Structured => provider.GetRequiredService<StructuredLlm>();

    public T Get<T>()
        where T : notnull => provider.GetRequiredService<T>();

    public static LlmRequest Hello(string feature = "test") => new() { Feature = feature, Messages = [LlmMessage.User("hello")] };

    public void Dispose() => provider.Dispose();
}

/// <summary>Scripted ILlmClient for StructuredLlm logic tests (no network at all).</summary>
public sealed class ScriptedLlm : ILlmClient
{
    private readonly Queue<LlmResponse> replies = new();

    public List<LlmRequest> Requests { get; } = [];

    public string Provider => "scripted";

    public string DefaultModel => "scripted-model";

    public int DefaultMaxOutputTokens => 1000;

    public int MaxOutputTokensCap => 4000;

    public ScriptedLlm Reply(string text, LlmFinishReason finish = LlmFinishReason.Stop, int thoughts = 0)
    {
        replies.Enqueue(new LlmResponse
        {
            Model = "scripted-model",
            Parts = [new TextPart(text)],
            FinishReason = finish,
            FinishReasonRaw = finish == LlmFinishReason.MaxTokens ? "MAX_TOKENS" : "STOP",
            Usage = new LlmUsage(10, 5, thoughts, 15 + thoughts),
            Latency = TimeSpan.FromMilliseconds(5),
        });
        return this;
    }

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(replies.Dequeue());
    }
}
