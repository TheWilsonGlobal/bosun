using System.Net;
using System.Text.Json;
using Bosun.Application.Llm;
using Bosun.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Bosun.Api.IntegrationTests;

/// <summary>
/// LIVE tests: real Gemini calls that spend (a few hundred) tokens. Excluded from the default `dotnet test` by
/// tests/default.runsettings; run them on purpose with `pnpm run test:live` (see TODO.md: deliberate spend, flash-lite).
/// The keys come from the root .env through the same loader the app uses, and are never printed.
/// </summary>
[Trait("Category", "Live")]
public class LiveLlmTests(ITestOutputHelper output)
{
    private static readonly IConfiguration Env = new ConfigurationBuilder().AddBosunEnv().Build();

    /// <summary>A test host that talks to the real network with the real .env keys (the base TestApp swaps them for fakes).</summary>
    private sealed class LiveApp(Dictionary<string, string?>? overrides = null) : TestApp("Development", Real(overrides));

    private static Dictionary<string, string?> Real(Dictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Model"] = Env["Llm:Model"],
            ["Llm:ApiKey"] = Env["Llm:ApiKey"],
            ["Llm:Provider"] = Env["Llm:Provider"],
            ["Embedding:Model"] = Env["Embedding:Model"],
            ["Embedding:ApiKey"] = Env["Embedding:ApiKey"],
            ["Embedding:Dimensions"] = Env["Embedding:Dimensions"],
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    [Fact]
    public async Task A_structured_ping_works_against_the_configured_model()
    {
        Env["Llm:ApiKey"].Should().NotBeNullOrWhiteSpace("GEMINI_LLM_API_KEY must be set in the root .env for live tests");
        using var app = new LiveApp();
        var client = app.CreateClient();

        var response = await client.GetAsync("/api/ai/ping?echo=bosun-live");
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine($"HTTP {(int)response.StatusCode}: {text}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var json = JsonDocument.Parse(text).RootElement;
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("echo").GetString().Should().Be("bosun-live");
        json.GetProperty("model").GetString().Should().StartWith(Env["Llm:Model"]!, "LLM_MODEL is honoured, not hardcoded");
        json.GetProperty("usage").GetProperty("promptTokens").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("usage").GetProperty("candidateTokens").GetInt32().Should().BeGreaterThan(0);

        var usage = await client.GetStringAsync("/api/ai/usage");
        output.WriteLine($"usage: {usage}");
        JsonDocument.Parse(usage).RootElement.GetProperty("rows").EnumerateArray().Should().Contain(r => r.GetProperty("feature").GetString() == "ping");
    }

    [Fact]
    public async Task An_embedding_call_returns_the_configured_dimensions_and_unit_length_vectors()
    {
        Env["Embedding:ApiKey"].Should().NotBeNullOrWhiteSpace("GEMINI_EMBEDDING_API_KEY must be set in the root .env for live tests");
        using var app = new LiveApp();
        app.CreateClient();
        var embeddings = app.Services.GetRequiredService<IEmbeddingClient>();
        var expectedDims = int.Parse(Env["Embedding:Dimensions"]!);

        string[] texts = ["High exhaust gas temperature alarm on the main engine.", "Lube oil pressure low shutdown."];
        EmbeddingResult result;
        try
        {
            result = await embeddings.EmbedAsync(texts, EmbeddingTask.Document, "live.test", CancellationToken.None);
        }
        catch (LlmException ex) when (ex.Code == LlmErrorCodes.RateLimited && ex.RetryAfter is { } wait && wait < TimeSpan.FromMinutes(2))
        {
            output.WriteLine($"429 free-tier quota; waiting {wait.TotalSeconds:F0} s and retrying once. Provider said: {ex.Message}");
            await Task.Delay(wait + TimeSpan.FromSeconds(1));
            result = await embeddings.EmbedAsync(texts, EmbeddingTask.Document, "live.test", CancellationToken.None);
        }

        output.WriteLine($"embedding model={result.Model} vectors={result.Vectors.Count} dims={result.Dimensions} promptTokens={result.PromptTokens} latencyMs={result.Latency.TotalMilliseconds:F0} attempts={result.Attempts}");
        result.Model.Should().Be(Env["Embedding:Model"]);
        result.Vectors.Should().HaveCount(2);
        foreach (var vector in result.Vectors)
        {
            vector.Length.Should().Be(expectedDims);
            VectorMath.Norm(vector).Should().BeApproximately(1.0, 1e-3);
            output.WriteLine($"  norm={VectorMath.Norm(vector):F6}");
        }

        // semantically different texts must not be identical
        var dot = result.Vectors[0].Zip(result.Vectors[1], (a, b) => (double)a * b).Sum();
        output.WriteLine($"  cosine(text0,text1)={dot:F4}");
        dot.Should().BeLessThan(0.999);
    }

    [Theory]
    [InlineData("AI", "zaSy")]
    [InlineData("AQ", ".")]
    public async Task A_deliberately_wrong_key_is_llm_auth_failed_with_no_retry(string stylePart1, string stylePart2)
    {
        // Built at run time so no key-shaped literal sits in the source (secret scanners flag those): the style prefix plus a filler.
        var wrongKey = stylePart1 + stylePart2 + "DeliberatelyWrongKeyForTestingOnly" + new string('0', 12);
        using var app = new LiveApp(new() { ["Llm:ApiKey"] = wrongKey }); // test-host override only: the real .env key is never touched
        var response = await app.CreateClient().GetAsync("/api/ai/ping");
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine($"wrong key shape '{wrongKey[..6]}...': HTTP {(int)response.StatusCode} {text}");

        var (code, problem) = await response.ReadProblemAsync(502);
        code.Should().Be("llm_auth_failed");
        problem.GetProperty("attempts").GetInt32().Should().Be(1);
        text.Should().NotContain(wrongKey);
    }
}
