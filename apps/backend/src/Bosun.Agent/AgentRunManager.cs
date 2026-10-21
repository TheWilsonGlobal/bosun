using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Application;
using Bosun.Application.Tools;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bosun.Agent;

/// <summary>
/// Starts agent runs in the background (one at a time per project), keeps the handle needed to cancel them, and marks runs
/// that a restart orphaned. The run itself is <see cref="AgentRunner"/>.
/// </summary>
public sealed class AgentRunManager(IServiceScopeFactory scopes, IOptions<AgentOptions> options, TimeProvider clock, ILogger<AgentRunManager> logger)
{
    private sealed record Active(int RunId, int ProjectId, CancellationTokenSource Cts, Task Completion);

    private readonly ConcurrentDictionary<int, Active> active = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly AgentOptions opts = options.Value;

    public bool IsActive(int runId) => active.ContainsKey(runId);

    /// <summary>The background task of a run that is still executing, for callers that must wait for it (tests, shutdown).</summary>
    public Task? Completion(int runId) => active.TryGetValue(runId, out var a) ? a.Completion : null;

    /// <summary>Creates the run row (status running) and starts the loop in the background. Returns the run id.</summary>
    public async Task<int> StartAsync(int projectId, string? message, UserContext user, CancellationToken ct)
    {
        var text = Validate(message);

        await startGate.WaitAsync(ct);
        try
        {
            int runId;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
                if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
                {
                    throw AppException.NotFound("Project", projectId);
                }

                var running = await db.AgentRuns.AsNoTracking()
                    .Where(r => r.ProjectId == projectId && r.Status == AgentRunStatus.Running)
                    .Select(r => (int?)r.Id)
                    .FirstOrDefaultAsync(ct);
                if (running is { } existing)
                {
                    throw new AppException(ErrorKind.Conflict, "agent_run_in_progress",
                        "An agent run is already in progress for this project. Wait for it to finish or cancel it.",
                        new Dictionary<string, object?> { ["runId"] = existing });
                }

                var run = new AgentRun
                {
                    ProjectId = projectId,
                    Message = text,
                    Status = AgentRunStatus.Running,
                    Phase = "planning",
                    CreatedBy = user.Name,
                    StartedAt = clock.GetUtcNow().UtcDateTime,
                    MaxToolCalls = opts.MaxToolCalls,
                    MaxTotalTokens = opts.MaxTotalTokens,
                };
                using (await scope.ServiceProvider.GetRequiredService<WriteGate>().EnterAsync(ct))
                {
                    db.AgentRuns.Add(run);
                    await db.SaveChangesAsync(ct);
                }

                runId = run.Id;
            }

            var cts = new CancellationTokenSource();
            var completion = Task.Run(() => ExecuteAsync(runId, cts.Token), CancellationToken.None);
            active[runId] = new Active(runId, projectId, cts, completion);
            _ = completion.ContinueWith(_ =>
            {
                if (active.TryRemove(runId, out var done))
                {
                    done.Cts.Dispose();
                }
            }, TaskScheduler.Default);
            return runId;
        }
        finally
        {
            startGate.Release();
        }
    }

    /// <summary>Asks a running run to stop. Returns false when the run is not running in this process.</summary>
    public bool Cancel(int runId)
    {
        if (!active.TryGetValue(runId, out var a))
        {
            return false;
        }

        try
        {
            a.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public void CancelAll()
    {
        foreach (var runId in active.Keys)
        {
            Cancel(runId);
        }
    }

    /// <summary>At start-up no run can still be running: whatever says so was cut off by a restart.</summary>
    public async Task RecoverOrphansAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var orphans = await db.AgentRuns.Where(r => r.Status == AgentRunStatus.Running).ToListAsync(ct);
        foreach (var run in orphans)
        {
            run.Status = AgentRunStatus.Failed;
            run.Phase = null;
            run.FinishedAt = clock.GetUtcNow().UtcDateTime;
            run.ErrorCode = AgentErrorCodes.ServerRestarted;
            run.ErrorMessage = "The server restarted while this run was in progress.";
        }

        if (orphans.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Marked {Count} orphaned agent run(s) as failed", orphans.Count);
        }
    }

    private string Validate(string? message)
    {
        var text = message?.Trim() ?? "";
        if (text.Length == 0 || text.Length > opts.MaxMessageLength)
        {
            throw AppException.Invalid($"message is required and at most {opts.MaxMessageLength} characters.");
        }

        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            throw AppException.Invalid("message must not contain control characters.");
        }

        return text;
    }

    private async Task ExecuteAsync(int runId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentRunner>().RunAsync(runId, ct);
        }
        catch (Exception ex)
        {
            // AgentRunner records its own failures; this is the last resort so a run can never stay "running" forever.
            logger.LogError(ex, "Agent run {RunId} escaped its runner", runId);
            await MarkFailedAsync(runId);
        }
    }

    private async Task MarkFailedAsync(int runId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
            var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is { Status: AgentRunStatus.Running })
            {
                run.Status = AgentRunStatus.Failed;
                run.Phase = null;
                run.FinishedAt = clock.GetUtcNow().UtcDateTime;
                run.ErrorCode = AgentErrorCodes.InternalError;
                run.ErrorMessage = "The agent run failed unexpectedly. See the server log.";
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark agent run {RunId} as failed", runId);
        }
    }
}

/// <summary>Read side of the agent runs: REST endpoints and the hub's catch-up.</summary>
public sealed class AgentQueries(IBosunDbContext db, AgentRunManager manager)
{
    public async Task<AgentRunDto> GetAsync(int runId, CancellationToken ct)
    {
        var run = await db.AgentRuns.AsNoTracking().Include(r => r.Steps).FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw AppException.NotFound("Agent run", runId);
        return ToDto(run, run.Steps.OrderBy(s => s.Seq).Select(ToDto).ToList());
    }

    public async Task<PagedResult<AgentRunDto>> ListAsync(int projectId, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
        {
            throw AppException.Invalid("page must be between 1 and 100000 and pageSize between 1 and 100.");
        }

        var query = db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == projectId);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AgentRunDto>(rows.Select(r => ToDto(r, null)).ToList(), total, page, pageSize);
    }

    /// <summary>Requests cancellation. 404 for an unknown run; 409 run_already_finished when it ended; 409 run_not_active when it is marked running but not held here.</summary>
    public async Task<AgentRunDto> CancelAsync(int runId, CancellationToken ct)
    {
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw AppException.NotFound("Agent run", runId);
        if (run.Status != AgentRunStatus.Running)
        {
            throw new AppException(ErrorKind.Conflict, AgentErrorCodes.RunAlreadyFinished, $"The run already ended (status {run.Status}).",
                new Dictionary<string, object?> { ["currentStatus"] = run.Status });
        }

        if (!manager.Cancel(runId))
        {
            throw new AppException(ErrorKind.Conflict, AgentErrorCodes.RunNotActive,
                "The run is marked as running but this server is not executing it (it may have just finished, or the server restarted).",
                new Dictionary<string, object?> { ["currentStatus"] = run.Status });
        }

        return ToDto(run, null);
    }

    public static AgentRunDto ToDto(AgentRun r, IReadOnlyList<AgentStepDto>? steps) => new(
        r.Id, r.ProjectId, r.Message, r.Status, r.Phase, r.CreatedBy, r.Model, r.PromptVersion, r.StartedAt, r.FinishedAt,
        r.ToolCalls, r.MaxToolCalls, r.ElapsedMs, new AgentTokensDto(r.PromptTokens, r.CandidateTokens, r.ThoughtTokens, r.TotalTokens),
        r.MaxTotalTokens, r.ChangeSetId, r.ResultKind, r.ResultMessage, r.ErrorCode, r.ErrorMessage, steps);

    public static AgentStepDto ToDto(AgentStep s) =>
        new(s.Seq, s.Kind, JsonNode.Parse(s.PayloadJson), s.Tokens, s.LatencyMs, s.CreatedAt);
}

public static class AgentRegistration
{
    /// <summary>
    /// The prompt library, the probe, and the agent: runner (scoped, one per run), manager (singleton), queries. The event sink
    /// defaults to a no-op; the API replaces it with SignalR.
    /// </summary>
    public static IServiceCollection AddBosunAgent(this IServiceCollection services)
    {
        services.AddOptions<AgentOptions>().BindConfiguration("Agent");
        services.AddSingleton<PromptLibrary>();
        services.AddScoped<PingService>();
        services.AddScoped<RagAskService>();
        services.AddSingleton<IAgentEventSink, NullAgentEventSink>();
        services.AddScoped<AgentRunner>();
        services.AddSingleton<AgentRunManager>();
        services.AddScoped<AgentQueries>();
        return services;
    }
}
