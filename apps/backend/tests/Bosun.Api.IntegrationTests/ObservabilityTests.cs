using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bosun.Application.Llm;
using Bosun.Application.Monitoring;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bosun.Api.IntegrationTests;

/// <summary><c>/metrics</c> (bearer token) and <c>/ready</c>. Monitoring sockets stay off, as in every ordinary test.</summary>
public class ObservabilityTests
{
    private static readonly string Token = new('m', 40);

    private static Dictionary<string, string?> WithToken() => new() { ["Metrics:Token"] = Token };

    private static async Task<(HttpStatusCode Status, string? ContentType, string Body)> ScrapeAsync(HttpClient client, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_does_not_exist_without_a_token_and_is_not_the_spa()
    {
        using var app = new CustomApp("Development");
        var client = app.CreateClient();

        var (status, contentType, _) = await ScrapeAsync(client, Token);
        status.Should().Be(HttpStatusCode.NotFound);
        contentType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task Metrics_rejects_a_missing_or_wrong_token_and_serves_text_plain_for_the_right_one()
    {
        using var app = new CustomApp("Development", WithToken());
        var client = app.CreateClient();

        (await ScrapeAsync(client)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await ScrapeAsync(client, new string('x', 40))).Status.Should().Be(HttpStatusCode.Unauthorized);
        using (var bare = new HttpRequestMessage(HttpMethod.Get, "/metrics"))
        {
            bare.Headers.TryAddWithoutValidation("Authorization", Token); // right secret, no "Bearer" scheme
            (await client.SendAsync(bare)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var (status, contentType, body) = await ScrapeAsync(client, Token);
        status.Should().Be(HttpStatusCode.OK);
        contentType.Should().Be("text/plain");
        body.Should().Contain("# TYPE http_request_duration_seconds histogram");
        body.Should().Contain("app_build_info{");
        body.Should().Contain("bosun_live_tags ");
    }

    [Fact]
    public async Task Metrics_is_reachable_without_a_session_outside_development()
    {
        var extra = WithToken();
        extra["Auth:AdminPassword"] = "a-real-production-secret";
        using var production = new CustomApp("Production", extra);
        var client = production.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://127.0.0.1") });

        (await client.GetAsync("/api/projects/1/tags")).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // reads do need a login there
        (await ScrapeAsync(client, Token)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void A_token_shorter_than_32_characters_stops_the_app_from_starting()
    {
        using var app = new CustomApp("Development", new() { ["Metrics:Token"] = "short" });
        var start = () => app.CreateClient();
        start.Should().Throw<Exception>().Which.ToString().Should().Contain("METRICS_TOKEN");
    }

    [Fact]
    public async Task Requests_are_labelled_by_route_pattern_never_by_the_url()
    {
        using var app = new CustomApp("Development", WithToken());
        var client = app.CreateClient();

        (await client.GetAsync("/api/projects/1")).StatusCode.Should().Be(HttpStatusCode.OK);
        await client.GetAsync("/no/such/path-8f3a1c");

        var body = (await ScrapeAsync(client, Token)).Body;
        body.Should().Contain("route=\"/api/projects/{projectId:int}\"");
        body.Should().NotContain("projects/1\"");
        body.Should().NotContain("path-8f3a1c");
    }

    [Fact]
    public async Task Link_state_is_a_gauge_that_tracks_the_connection_while_health_stays_200()
    {
        using var app = new CustomApp("Development", WithToken());
        var client = app.CreateClient();
        var state = app.Services.GetRequiredService<MonitoringState>();

        await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Connected);
        await state.SetConnectionAsync(MonitoringState.NmeaName, ConnectionState.Disabled);
        var up = (await ScrapeAsync(client, Token)).Body;
        up.Should().Contain("bosun_link_connected{protocol=\"modbus\"} 1");
        up.Should().NotContain("protocol=\"nmea\""); // disabled: nothing to measure, so no series (not 0)

        await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Disconnected);
        (await ScrapeAsync(client, Token)).Body.Should().Contain("bosun_link_connected{protocol=\"modbus\"} 0");
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Llm_calls_are_counted_by_provider_model_and_outcome()
    {
        using var app = new CustomApp("Development", WithToken());
        var client = app.CreateClient(); // starts the host, and with it the meter listener

        var recorder = new RecordingLlmClient(new ScriptedClient(), new NullCallLog(), new LlmPricing(Options.Create(new PricingOptions())), TimeProvider.System, NullLogger<RecordingLlmClient>.Instance);
        await recorder.GenerateAsync(new LlmRequest { Feature = "test", Messages = [LlmMessage.User("hi")] }, CancellationToken.None);

        var body = (await ScrapeAsync(client, Token)).Body;
        body.Should().MatchRegex("bosun_llm_requests(_total)?\\{[^}]*model=\"scripted-1\"[^}]*status=\"ok\"[^}]*\\} 1");
        body.Should().MatchRegex("bosun_llm_tokens(_total)?\\{[^}]*direction=\"in\"[^}]*\\} 7");
        body.Should().Contain("bosun_llm_request_duration_seconds");
    }

    [Theory]
    [InlineData("/ready")]
    [InlineData("/api/ready")]
    public async Task Ready_reports_the_database_and_names_it(string path)
    {
        using var app = new CustomApp("Development");
        var response = await app.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("status").GetString().Should().Be("ok");
        json.GetProperty("checks").GetProperty("database").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Ready_is_503_when_the_database_file_is_gone_but_health_text_stays_unchanged()
    {
        using var app = new CustomApp("Development");
        var client = app.CreateClient();
        (await client.GetAsync("/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(app.DbPath);
        foreach (var sidecar in new[] { app.DbPath + "-wal", app.DbPath + "-shm" })
        {
            File.Delete(sidecar);
        }

        Directory.CreateDirectory(app.DbPath); // a directory where the file was: nothing can open it
        try
        {
            var response = await client.GetAsync("/ready");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            json.GetProperty("status").GetString().Should().Be("degraded");
            json.GetProperty("checks").GetProperty("database").GetString().Should().Be("fail");
        }
        finally
        {
            Directory.Delete(app.DbPath);
        }
    }

    private sealed class ScriptedClient : ILlmClient
    {
        public string Provider => "scripted";

        public string DefaultModel => "scripted-1";

        public int DefaultMaxOutputTokens => 100;

        public int MaxOutputTokensCap => 100;

        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmResponse
            {
                Model = "scripted-1",
                Parts = [new TextPart("ok")],
                FinishReason = LlmFinishReason.Stop,
                Usage = new LlmUsage(7, 3, 0, 10),
                Latency = TimeSpan.FromMilliseconds(20),
            });
    }

    private sealed class NullCallLog : ILlmCallLog
    {
        public Task RecordAsync(LlmCall call, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
