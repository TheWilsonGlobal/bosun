using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Agent;
using Bosun.Agent.Tests;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Tools;
using Bosun.Domain;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bosun.Api.IntegrationTests;

/// <summary>The real API with the agent on a SCRIPTED model: no network, no spend.</summary>
public sealed class AgentApp(ScriptedAgentLlm llm) : TestApp("Development")
{
    public ScriptedAgentLlm Llm { get; } = llm;

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<ILlmClient>();
        services.AddSingleton<ILlmClient>(Llm);
    }
}

public sealed class AgentApiTests : IDisposable
{
    private static readonly string[] Tags = Enumerable.Range(1, 8).Select(i => $"ME-EXH-TT0{i}").ToArray();

    private readonly ScriptedAgentLlm llm = new();
    private readonly AgentApp app;

    public AgentApiTests() => app = new AgentApp(llm);

    public void Dispose() => app.Dispose();

    private ScriptedAgentLlm DemoScript() => llm
        .Plan(steps: ["Find the sensors", "Set the alarms", "Assign the group"])
        .Call(ToolNames.SearchTags, Args.Obj(("query", Args.V("ME-EXH-TT")), ("system", Args.V("ME"))))
        .Calls((ToolNames.ProposeSetAlarmLimit, Args.SetLimit(Tags, "H", 95, 10)), (ToolNames.ProposeAssignGroup, Args.Assign(Tags, "ME-EXH")))
        .Call(ToolNames.ValidateDraft, new JsonObject())
        .Finish("draft_ready", "Added H alarms to the eight sensors.");

    private async Task<JsonElement> WaitForRunAsync(HttpClient client, int runId)
    {
        for (var i = 0; i < 600; i++)
        {
            var response = await client.GetAsync($"/api/agent/runs/{runId}");
            var run = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            if (run.GetProperty("status").GetString() != "running")
            {
                return run;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The run did not finish.");
    }

    [Fact]
    public async Task Starting_a_run_needs_a_session_but_reading_follows_the_configurator_rule()
    {
        using var anonymous = app.CreateClient();
        (await anonymous.PostJsonAsync("/api/projects/1/agent/runs", new { message = "hello" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/agent/runs/1/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/projects/1/agent/runs")).StatusCode.Should().Be(HttpStatusCode.OK, "reads are open in Development, like the rest of the configurator");

        using var production = new CustomApp("Production", new Dictionary<string, string?> { ["Auth:AdminPassword"] = "a-real-production-secret" });
        using var client = production.CreateClient();
        (await client.GetAsync("/api/projects/1/agent/runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_run_goes_from_202_to_awaiting_review_with_a_replayable_step_log_and_a_server_side_provenance()
    {
        DemoScript();
        using var client = await app.LoginAsync();

        var accepted = await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "Add high-temperature alarms at 95 °C with a 10 s delay to all main engine exhaust gas temperature sensors, and put them in alarm group ME-EXH." });
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        accepted.Headers.Location!.ToString().Should().StartWith("/api/agent/runs/");
        var runId = (await accepted.ReadAsync<StartAgentRunResponse>()).RunId;

        var run = await WaitForRunAsync(client, runId);
        run.GetProperty("status").GetString().Should().Be("awaiting_review");
        run.GetProperty("createdBy").GetString().Should().Be("admin");
        run.GetProperty("resultKind").GetString().Should().Be("draft");
        run.GetProperty("toolCalls").GetInt32().Should().Be(4);
        run.GetProperty("tokens").GetProperty("total").GetInt32().Should().BeGreaterThan(0);
        run.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString())
            .Should().Equal("plan", "tool_call", "tool_result", "tool_call", "tool_result", "tool_call", "tool_result", "tool_call", "tool_result", "tool_call", "verify", "final");
        run.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("seq").GetInt32()).Should().Equal(Enumerable.Range(1, 12));

        // The draft is a normal change set: source agent, provenance from the server, and it is only a proposal.
        var csId = run.GetProperty("changeSetId").GetInt32();
        var cs = await (await client.GetAsync($"/api/change-sets/{csId}")).ReadAsync<ChangeSetDto>();
        cs.Source.Should().Be(ChangeSetSource.Agent);
        cs.Status.Should().Be(ChangeSetStatus.Validated);
        cs.AgentRunId.Should().Be(runId);
        cs.Model.Should().Be("scripted-model");
        cs.PromptVersion.Should().Be(run.GetProperty("promptVersion").GetString());
        cs.Ops.Should().HaveCount(16);

        // List endpoint: newest first, no steps.
        var list = await (await client.GetAsync("/api/projects/1/agent/runs")).ReadAsync<PagedResult<AgentRunDto>>();
        list.Total.Should().Be(1);
        list.Items.Single().Id.Should().Be(runId);
        list.Items.Single().Steps.Should().BeNull();
    }

    [Fact]
    public async Task The_human_approval_gate_applies_the_agent_draft_and_the_audit_trail_records_the_agent_provenance()
    {
        DemoScript();
        using var client = await app.LoginAsync();
        var runId = (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "Alarms on the ME exhaust sensors" })).ReadAsync<StartAgentRunResponse>()).RunId;
        var run = await WaitForRunAsync(client, runId);
        var csId = run.GetProperty("changeSetId").GetInt32();

        (await client.TagAsync("ME-EXH-TT01")).AlarmLimits.Should().BeEmpty("nothing is applied before a human decides");

        await (await client.PostJsonAsync($"/api/change-sets/{csId}/approve")).ReadAsync<ChangeSetDto>();
        var applied = await (await client.PostJsonAsync($"/api/change-sets/{csId}/apply")).ReadAsync<ChangeSetDto>();
        applied.Status.Should().Be(ChangeSetStatus.Applied);
        applied.AppliedBy.Should().Be("admin");

        var tag = await client.TagAsync("ME-EXH-TT05");
        tag.AlarmGroup.Should().Be("ME-EXH");
        tag.AlarmLimits.Should().ContainSingle().Which.Should().BeEquivalentTo(new AlarmLimitDto(AlarmLevel.H, 95, 10, 0));

        var audit = await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={csId}")).ReadAsync<List<AuditEntryDto>>();
        audit.Should().HaveCount(16).And.OnlyContain(a => a.ProposedBy == ActorType.Agent && a.ActorType == ActorType.User && a.Actor == "admin"
            && a.AgentRunId == runId && a.Model == "scripted-model" && a.PromptVersion == run.GetProperty("promptVersion").GetString());
    }

    [Fact]
    public async Task Clients_can_never_create_agent_change_sets_or_set_provenance()
    {
        using var client = await app.LoginAsync();
        var forged = await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "forged", source = "agent", model = "gemini", promptVersion = "x", agentRunId = 1 });
        (await forged.ReadProblemAsync(400)).Code.Should().Be("invalid_request");

        var manual = await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "mine", model = "gemini", promptVersion = "x", agentRunId = 7 })).ReadAsync<ChangeSetDto>();
        manual.Source.Should().Be(ChangeSetSource.Manual);
        manual.Model.Should().BeNull();
        manual.PromptVersion.Should().BeNull();
        manual.AgentRunId.Should().BeNull();
    }

    [Fact]
    public async Task Validation_conflict_cancel_and_not_found_errors_use_stable_codes()
    {
        using var client = await app.LoginAsync();
        (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "  " })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/999/agent/runs", new { message = "hi" })).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.GetAsync("/api/agent/runs/12345")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.PostJsonAsync("/api/agent/runs/12345/cancel")).ReadProblemAsync(404)).Code.Should().Be("not_found");

        var gate = new TaskCompletionSource();
        llm.Plan().Then(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return ScriptedAgentLlm.Respond([new TextPart("x")]);
        });
        var runId = (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "first" })).ReadAsync<StartAgentRunResponse>()).RunId;

        var busy = await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "second" })).ReadProblemAsync(409);
        busy.Code.Should().Be("agent_run_in_progress");
        busy.Body.GetProperty("runId").GetInt32().Should().Be(runId);

        (await client.PostJsonAsync($"/api/agent/runs/{runId}/cancel")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var run = await WaitForRunAsync(client, runId);
        run.GetProperty("status").GetString().Should().Be("cancelled");

        (await (await client.PostJsonAsync($"/api/agent/runs/{runId}/cancel")).ReadProblemAsync(409)).Code.Should().Be("run_already_finished");
    }

    [Fact]
    public async Task An_llm_failure_is_reported_in_the_run_with_its_specific_code()
    {
        llm.Plan().Throw(new LlmException(LlmErrorCodes.SafetyBlocked, "Blocked by the provider's safety filter.", 200, null, 1));
        using var client = await app.LoginAsync();
        var runId = (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "hello" })).ReadAsync<StartAgentRunResponse>()).RunId;

        var run = await WaitForRunAsync(client, runId);
        run.GetProperty("status").GetString().Should().Be("failed");
        run.GetProperty("errorCode").GetString().Should().Be("llm_safety_blocked");
        run.GetProperty("steps").EnumerateArray().Last().GetProperty("kind").GetString().Should().Be("error");
    }

    [Fact]
    public async Task The_hub_streams_step_status_and_done_events_to_clients_that_joined_the_run()
    {
        var release = new TaskCompletionSource();
        llm.Then(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return ScriptedAgentLlm.Respond([new TextPart("""{"decision":"proceed","message":"","title":"Hub test","steps":["a"],"affected_scope":"s","risks":[]}""")]);
        });
        llm.Call(ToolNames.SearchTags, Args.Obj(("query", Args.V("ME-EXH-TT01"))))
            .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
            .Finish("draft_ready", "Done.");

        var cookie = await SessionCookieAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var runId = (await (await client.PostJsonAsync("/api/projects/1/agent/runs", new { message = "ME-EXH-TT01 to ME-EXH" })).ReadAsync<StartAgentRunResponse>()).RunId;

        var events = new ConcurrentQueue<(string Name, JsonElement Payload)>();
        var done = new TaskCompletionSource<JsonElement>();
        await using var connection = Connect(cookie);
        connection.On<JsonElement>("step", e => events.Enqueue(("step", e)));
        connection.On<JsonElement>("status", e => events.Enqueue(("status", e)));
        connection.On<JsonElement>("done", e =>
        {
            events.Enqueue(("done", e));
            done.TrySetResult(e);
        });
        await connection.StartAsync();

        var snapshot = await connection.InvokeAsync<JsonElement>("JoinRun", runId);
        snapshot.GetProperty("id").GetInt32().Should().Be(runId);
        snapshot.GetProperty("status").GetString().Should().Be("running");
        snapshot.GetProperty("phase").GetString().Should().Be("planning", "events before the join are not replayed; the snapshot carries the current phase");
        release.SetResult();

        var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        final.GetProperty("runId").GetInt32().Should().Be(runId);
        final.GetProperty("status").GetString().Should().Be("awaiting_review");
        final.GetProperty("changeSetId").GetInt32().Should().BeGreaterThan(0);
        final.GetProperty("resultKind").GetString().Should().Be("draft");

        var steps = events.Where(e => e.Name == "step").Select(e => e.Payload).ToList();
        steps.Select(s => s.GetProperty("kind").GetString()).Should().Equal("plan", "tool_call", "tool_result", "tool_call", "tool_result", "tool_call", "verify", "final");
        steps.Select(s => s.GetProperty("seq").GetInt32()).Should().Equal(Enumerable.Range(1, 8));
        steps.Should().OnlyContain(s => s.GetProperty("runId").GetInt32() == runId && Has(s, "payload") && Has(s, "tokens") && Has(s, "latencyMs"));
        events.Where(e => e.Name == "status").Select(e => e.Payload.GetProperty("status").GetString()).Should().Contain("running").And.Contain("awaiting_review");
        events.Where(e => e.Name == "status").Select(e => e.Payload.GetProperty("phase").ValueKind == JsonValueKind.String ? e.Payload.GetProperty("phase").GetString() : null)
            .Should().Contain(["acting", "verifying"]);

        // A late joiner gets the whole run in the JoinRun reply.
        var late = await connection.InvokeAsync<JsonElement>("JoinRun", runId);
        late.GetProperty("steps").GetArrayLength().Should().Be(8);
        await connection.InvokeAsync<JsonElement?>("JoinRun", runId); // idempotent
        var missing = async () => await connection.InvokeAsync<JsonElement>("JoinRun", 424242);
        (await missing.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task The_hub_refuses_connections_without_a_session()
    {
        await using var connection = Connect(cookie: null);
        var start = async () => await connection.StartAsync();
        await start.Should().ThrowAsync<HttpRequestException>().Where(e => e.Message.Contains("401") || e.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task No_route_reachable_from_the_agent_tool_surface_can_write_to_tags_or_approve_and_apply()
    {
        // Every route a tool is documented to correspond to must exist in the real route table...
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => $"{m} {NormalisePath(e.RoutePattern.RawText!)}"))
            .ToHashSet();

        await using var scope = app.Services.CreateAsyncScope();
        var tools = scope.ServiceProvider.GetRequiredService<ToolRegistry>().Tools;
        var reachable = tools.SelectMany(t => t.Routes.Select(r => (Tool: t.Name, Route: Normalise(r)))).ToList();
        reachable.Should().NotBeEmpty();
        foreach (var (tool, route) in reachable)
        {
            routes.Should().Contain(route, $"{tool} documents the route {route}");
        }

        // ...and the only reachable routes that are not plain reads append to / validate a DRAFT change set.
        var draftOnly = new[] { "POST /api/change-sets/{id}/ops", "POST /api/change-sets/{id}/validate" };
        reachable.Select(r => r.Route).Where(r => !r.StartsWith("GET ", StringComparison.Ordinal)).Distinct().Should().BeSubsetOf(draftOnly);

        // The routes that can change tags, approve or apply are named explicitly and none of them is reachable.
        var tagWriting = routes.Where(r => r.StartsWith("POST ", StringComparison.Ordinal) &&
            (r.EndsWith("/apply", StringComparison.Ordinal) || r.EndsWith("/approve", StringComparison.Ordinal) || r.EndsWith("/reject", StringComparison.Ordinal))).ToList();
        tagWriting.Should().Contain("POST /api/change-sets/{id}/apply", "the guard must be looking at the real apply route");
        reachable.Select(r => r.Route).Should().NotIntersectWith(tagWriting);

        // The only mutating routes of the whole API are the known change-set and agent ones; any new one must be classified here on purpose.
        var mutating = routes.Where(r => !r.StartsWith("GET ", StringComparison.Ordinal) && !r.StartsWith("* ", StringComparison.Ordinal) && !r.StartsWith("HEAD ", StringComparison.Ordinal)).ToList();
        // Phase 5 added one PATCH on purpose: the human correction of an extracted row (it edits the REVIEW row, never a tag).
        var classified = new[] { "PATCH /api/extractions/{id}/rows/{rowId}" };
        mutating.Where(r => !r.Contains(" /hubs/", StringComparison.Ordinal) && !classified.Contains(r)).Should().OnlyContain(r => r.StartsWith("POST /api/", StringComparison.Ordinal));
        reachable.Select(r => r.Route).Should().NotIntersectWith(classified.Concat(mutating.Where(r => r.Contains("/extractions", StringComparison.Ordinal) || r.EndsWith("/documents", StringComparison.Ordinal))),
            "the agent tool surface cannot reach the extraction routes either");
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

    private static string NormalisePath(string path) =>
        "/" + System.Text.RegularExpressions.Regex.Replace(path, @"\{(\w+)(:\w+)?\}", "{$1}").TrimStart('/');

    private static string Normalise(string route)
    {
        var space = route.IndexOf(' ');
        return $"{route[..space]} {NormalisePath(route[(space + 1)..])}";
    }

    private async Task<string> SessionCookieAsync()
    {
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword });
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bosun.session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private HubConnection Connect(string? cookie)
    {
        var server = app.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/agent"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
            })
            .AddJsonProtocol(o => BosunJson.Configure(o.PayloadSerializerOptions))
            .Build();
    }
}
