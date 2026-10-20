using System.Security.Claims;
using Bosun.Agent;
using Bosun.Api.Auth;
using Bosun.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Bosun.Api;

public sealed record StartAgentRunBody(string? Message);

public sealed record StartAgentRunResponse(int RunId);

/// <summary>
/// The agent's REST surface. Starting and cancelling need a signed-in human (the run spends tokens and drafts changes in
/// their name); reads follow the configurator rule (open in Development, a session elsewhere). Nothing here, and nothing the
/// agent can reach, applies or approves a change set.
/// </summary>
public static class AgentEndpoints
{
    public const string HubPath = "/hubs/agent";

    public static void MapAgent(this WebApplication app, bool readsRequireLogin)
    {
        var reads = app.MapGroup("/api").WithTags("Agent");
        if (readsRequireLogin)
        {
            reads.RequireAuthorization();
        }

        reads.MapGet("/projects/{projectId:int}/agent/runs",
                async (int projectId, int? page, int? pageSize, AgentQueries q, CancellationToken ct) =>
                    TypedResults.Ok(await q.ListAsync(projectId, page ?? 1, pageSize ?? 20, ct)))
            .WithSummary("Agent runs of a project, newest first: { items, total, page, pageSize } (no steps).");
        reads.MapGet("/agent/runs/{id:int}", async (int id, AgentQueries q, CancellationToken ct) => TypedResults.Ok(await q.GetAsync(id, ct)))
            .WithSummary("One run with its ordered steps.");

        var writes = app.MapGroup("/api").WithTags("Agent").RequireAuthorization();
        writes.MapPost("/projects/{projectId:int}/agent/runs",
                async (int projectId, StartAgentRunBody? body, AgentRunManager manager, ClaimsPrincipal principal, CancellationToken ct) =>
                {
                    var runId = await manager.StartAsync(projectId, body?.Message, principal.ToUser(), ct);
                    return TypedResults.Accepted($"/api/agent/runs/{runId}", new StartAgentRunResponse(runId));
                })
            .WithSummary("Start an agent run in the background (202 { runId }). One run at a time per project (409 agent_run_in_progress).");
        writes.MapPost("/agent/runs/{id:int}/cancel",
                async (int id, AgentQueries q, CancellationToken ct) => TypedResults.Accepted($"/api/agent/runs/{id}", await q.CancelAsync(id, ct)))
            .WithSummary("Ask a running run to stop (202). The final status arrives as a 'done' event. 409 when it already finished.");

        app.MapHub<AgentHub>(HubPath).RequireAuthorization();
    }
}

/// <summary>
/// Live run progress (SignalR, JSON protocol, cookie session). Clients call <c>JoinRun(runId)</c>, then receive
/// <c>step</c>, <c>status</c> and <c>done</c> events for that run. JoinRun returns the run so far (with its steps), so a
/// late joiner needs no extra GET; steps carry a <c>seq</c> for de-duplication.
/// </summary>
[Authorize]
public sealed class AgentHub(AgentQueries queries) : Hub
{
    public static string GroupOf(int runId) => $"agent-run-{runId}";

    public async Task<AgentRunDto> JoinRun(int runId)
    {
        AgentRunDto run;
        try
        {
            run = await queries.GetAsync(runId, Context.ConnectionAborted);
        }
        catch (AppException)
        {
            throw new HubException($"Agent run {runId} was not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(runId), Context.ConnectionAborted);

        // Re-read after joining: anything that happened in between is in the snapshot, the rest arrives as events.
        return await queries.GetAsync(runId, Context.ConnectionAborted) is { } latest ? latest : run;
    }

    public Task LeaveRun(int runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupOf(runId));
}

/// <summary>Pushes agent progress to the clients that joined the run's group.</summary>
public sealed class SignalRAgentEventSink(IHubContext<AgentHub> hub) : IAgentEventSink
{
    public Task StepAsync(AgentStepEvent e) => hub.Clients.Group(AgentHub.GroupOf(e.RunId)).SendAsync("step", e);

    public Task StatusAsync(AgentStatusEvent e) => hub.Clients.Group(AgentHub.GroupOf(e.RunId)).SendAsync("status", e);

    public Task DoneAsync(AgentDoneEvent e) => hub.Clients.Group(AgentHub.GroupOf(e.RunId)).SendAsync("done", e);
}

/// <summary>Fails runs that a restart cut off, and cancels live runs on shutdown.</summary>
public sealed class AgentLifecycle(AgentRunManager manager, ILogger<AgentLifecycle> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await manager.RecoverOrphansAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Typically the database is not migrated yet (non-Development): not a reason to stop the server.
            logger.LogWarning(ex, "Could not check for orphaned agent runs");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        manager.CancelAll();
        return Task.CompletedTask;
    }
}
