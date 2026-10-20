using System.Net;
using System.Text.Json;
using Bosun.Agent;
using Bosun.Application.Llm;
using Bosun.Infrastructure.Llm;
using Bosun.Llm.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Api.IntegrationTests;

/// <summary>The real API on a fake Gemini: /api/ai/ping and /api/ai/usage, success and every llm_* error mapping.</summary>
public sealed class AiApp : TestApp
{
    public FakeHandler Llm { get; } = new();

    public AiApp(string environment = "Development", Dictionary<string, string?>? extra = null)
        : base(environment, With(extra))
    {
    }

    private static Dictionary<string, string?> With(Dictionary<string, string?>? extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Resilience:BaseDelayMs"] = "1",
            ["Llm:Resilience:MaxRetryAttempts"] = "2",
            ["Auth:AdminPassword"] = TestApp.AdminPassword,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(GeminiHttp.LlmClientName).ConfigurePrimaryHttpMessageHandler(() => Llm));
    }
}

public class AiEndpointTests
{
    private static string PingReply(string echo = "bosun-ping") => $$"""{"ok": true, "echo": "{{echo}}"}""";

    [Fact]
    public async Task Ping_returns_the_structured_result_with_model_tokens_latency_and_prompt_version()
    {
        using var app = new AiApp();
        app.Llm.ThenOk(PingReply(), prompt: 41, candidates: 12, thoughts: 3);
        var client = app.CreateClient(); // no session: open in Development

        var response = await client.GetAsync("/api/ai/ping");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("model").GetString().Should().Be("test-model-v1");
        json.GetProperty("echo").GetString().Should().Be("bosun-ping");
        json.GetProperty("promptVersion").GetString().Should().Be(new PromptLibrary().Get("ping.system").Version);
        json.GetProperty("finishReason").GetString().Should().Be("STOP");
        json.GetProperty("usage").GetProperty("promptTokens").GetInt32().Should().Be(41);
        json.GetProperty("usage").GetProperty("candidateTokens").GetInt32().Should().Be(12);
        json.GetProperty("usage").GetProperty("thoughtTokens").GetInt32().Should().Be(3);
        json.GetProperty("usage").GetProperty("totalTokens").GetInt32().Should().Be(56);
        json.GetProperty("latencyMs").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        json.GetProperty("attempts").GetInt32().Should().Be(1);
        json.GetProperty("extraCalls").GetInt32().Should().Be(0);
        json.TryGetProperty("estimatedCostUsd", out _).Should().BeTrue();

        // the call used a JSON schema, the prompt file and the configured model, and the key stayed in the header
        var sent = app.Llm.Requests.Single();
        sent.Url.AbsolutePath.Should().EndWith("/models/test-model:generateContent");
        sent.Json["generationConfig"]!["responseMimeType"]!.GetValue<string>().Should().Be("application/json");
        sent.Json["generationConfig"]!["responseJsonSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("ok", "echo");
        sent.Json["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Contain("health probe");
        sent.Json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>().Should().Be("<data>bosun-ping</data>");
    }

    [Fact]
    public async Task Ping_accepts_post_and_an_echo_and_repairs_a_wrong_echo_once()
    {
        using var app = new AiApp();
        app.Llm.ThenOk(PingReply("something else")).ThenOk(PingReply("hello 42"));
        var client = app.CreateClient();

        var response = await client.PostAsync("/api/ai/ping?echo=hello%2042", null);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.GetProperty("echo").GetString().Should().Be("hello 42");
        json.GetProperty("extraCalls").GetInt32().Should().Be(1);
        json.GetProperty("usage").GetProperty("promptTokens").GetInt32().Should().Be(20, "both calls are counted");
        app.Llm.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData("<script>")]
    [InlineData("a%00b")]
    public async Task Ping_rejects_an_echo_that_could_break_the_data_block(string echo)
    {
        using var app = new AiApp();
        var response = await app.CreateClient().GetAsync($"/api/ai/ping?echo={echo}");
        (await response.ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        app.Llm.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ping_is_logged_to_llm_calls_and_shows_up_in_the_usage_summary()
    {
        using var app = new AiApp();
        app.Llm.ThenOk(PingReply(), prompt: 100, candidates: 20, thoughts: 5).ThenOk(PingReply(), prompt: 50, candidates: 10);
        var client = app.CreateClient();
        await client.GetAsync("/api/ai/ping");
        await client.GetAsync("/api/ai/ping");

        var usage = JsonDocument.Parse(await client.GetStringAsync("/api/ai/usage")).RootElement;

        var row = usage.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("feature").GetString().Should().Be("ping");
        row.GetProperty("model").GetString().Should().Be("test-model");
        row.GetProperty("calls").GetInt32().Should().Be(2);
        row.GetProperty("failures").GetInt32().Should().Be(0);
        row.GetProperty("promptTokens").GetInt64().Should().Be(150);
        row.GetProperty("candidateTokens").GetInt64().Should().Be(30);
        row.GetProperty("thoughtTokens").GetInt64().Should().Be(5);
        usage.GetProperty("totals").GetProperty("calls").GetInt32().Should().Be(2);

        var future = JsonDocument.Parse(await client.GetStringAsync($"/api/ai/usage?from={Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("O"))}")).RootElement;
        future.GetProperty("rows").GetArrayLength().Should().Be(0);

        (await (await client.GetAsync("/api/ai/usage?from=not-a-date")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
    }

    public static TheoryData<string, int, string> Failures()
    {
        var data = new TheoryData<string, int, string>
        {
            { "auth", 502, "llm_auth_failed" },
            { "ratelimit", 429, "llm_rate_limited" },
            { "unavailable", 503, "llm_unavailable" },
            { "timeout", 504, "llm_timeout" },
            { "safety", 422, "llm_safety_blocked" },
            { "malformed", 502, "llm_malformed_output" },
            { "truncated", 502, "llm_truncated" },
            { "badrequest", 502, "llm_bad_request" },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Each_llm_failure_maps_to_its_own_status_and_stable_code_with_a_specific_message(string scenario, int status, string code)
    {
        using var app = new AiApp(extra: new() { ["Llm:Resilience:MaxRetryAttempts"] = "0", ["Llm:DefaultMaxOutputTokens"] = "10000", ["Llm:MaxOutputTokensCap"] = "20000" });
        switch (scenario)
        {
            case "auth":
                app.Llm.Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", ("API_KEY_INVALID", null)));
                break;
            case "ratelimit":
                app.Llm.Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "You exceeded your current quota", ("", "3600s")));
                break;
            case "unavailable":
                app.Llm.Then(HttpStatusCode.ServiceUnavailable, FakeHandler.Error(503, "UNAVAILABLE", "The model is overloaded."));
                break;
            case "timeout":
                app.Llm.Then(HttpStatusCode.GatewayTimeout, FakeHandler.Error(504, "DEADLINE_EXCEEDED", "Deadline expired"));
                break;
            case "safety":
                app.Llm.Then(HttpStatusCode.OK, FakeHandler.Ok("", finish: "SAFETY"));
                break;
            case "malformed":
                app.Llm.ThenOk("not json").ThenOk("still not json");
                break;
            case "truncated":
                app.Llm.ThenOk("{\"ok\":", finish: "MAX_TOKENS", thoughts: 9000).ThenOk("{\"ok\":", finish: "MAX_TOKENS", thoughts: 19000);
                break;
            case "badrequest":
                app.Llm.Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "Request contains an invalid argument."));
                break;
        }

        var response = await app.CreateClient().GetAsync("/api/ai/ping");
        var (actualCode, problem) = await response.ReadProblemAsync(status);

        actualCode.Should().Be(code);
        problem.GetProperty("detail").GetString()!.Length.Should().BeGreaterThan(30, "each code carries a specific, human-readable message");
        problem.GetProperty("detail").GetString().Should().NotContain("test-key", "the API key never leaves the server");
        problem.GetProperty("attempts").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        if (scenario == "ratelimit")
        {
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromHours(1));
            problem.GetProperty("retryAfterSeconds").GetInt32().Should().Be(3600);
        }
    }

    [Fact]
    public async Task A_missing_key_is_a_503_llm_not_configured_and_names_the_variable()
    {
        using var app = new AiApp(extra: new() { ["Llm:ApiKey"] = "" });

        var response = await app.CreateClient().GetAsync("/api/ai/ping");

        var (code, problem) = await response.ReadProblemAsync(503);
        code.Should().Be("llm_not_configured");
        problem.GetProperty("detail").GetString().Should().Contain("GEMINI_LLM_API_KEY");
        app.Llm.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Outside_development_ping_and_usage_need_a_session()
    {
        using var app = new AiApp("Production", new() { ["Auth:AdminPassword"] = "a-real-production-secret" });
        app.Llm.ThenOk(PingReply());
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://127.0.0.1") });

        (await (await client.GetAsync("/api/ai/ping")).ReadProblemAsync(401)).Code.Should().Be("unauthorized");
        (await (await client.GetAsync("/api/ai/usage")).ReadProblemAsync(401)).Code.Should().Be("unauthorized");
        app.Llm.Calls.Should().Be(0, "an anonymous caller cannot spend tokens");

        (await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "a-real-production-secret" })).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/ai/ping")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/ai/usage")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
