using System.Security.Claims;
using System.Text.Json.Nodes;
using Bosun.Api.Auth;
using Bosun.Application;
using Bosun.Application.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Api;

/// <summary>
/// Live monitoring REST surface. Reads follow the configurator rule (open in Development, a session elsewhere); acknowledging
/// needs a signed-in operator; the hub always needs a session, like the agent hub.
/// </summary>
public static class MonitoringEndpoints
{
    public const string HubPath = "/hubs/monitoring";

    public static void MapMonitoring(this WebApplication app, bool readsRequireLogin)
    {
        var reads = app.MapGroup("/api").WithTags("Monitoring");
        if (readsRequireLogin)
        {
            reads.RequireAuthorization();
        }

        reads.MapGet("/monitoring/status",
                async (MonitoringState state, AlarmManager alarms, MonitoringOptions options, CancellationToken ct) =>
                    TypedResults.Ok(await MonitoringHub.BuildStatusAsync(state, alarms, options, ct)))
            .WithSummary("Link states (modbus, nmea), tag count and alarm counts.");

        reads.MapGet("/monitoring/values", (MonitoringState state, TimeProvider time) =>
                TypedResults.Ok(new ValuesBatchDto(time.GetUtcNow().UtcDateTime, [.. state.Values.All().Select(v => v.ToDto())])))
            .WithSummary("Latest value of every live tag: { timestamp, values: [{ tag, value, unit, quality, timestamp }] }.");

        reads.MapGet("/monitoring/navigation", (MonitoringState state) =>
                TypedResults.Ok(state.Navigation ?? new NavigationDto(false, null, null, null, null, null, null)))
            .WithSummary("Latest NMEA position / speed / course / heading (valid=false before the first fix).");

        reads.MapGet("/monitoring/alarms",
                async (string? state, int? limit, AlarmManager alarms, CancellationToken ct) =>
                    TypedResults.Ok(await alarms.ListAsync(state, limit ?? 200, ct)))
            .WithSummary("Alarm list, newest first: { items, active, unacknowledged }. ?state= all (default) | active | unacknowledged | acknowledged | returned_unacknowledged | cleared; ?limit= 1..1000.");

        reads.MapGet("/projects/{projectId:int}/mimic",
                async (int projectId, MimicService mimic, CancellationToken ct) => Results.Json(await mimic.GetAsync(projectId, ct)))
            .WithSummary("Data-driven mimic layout (shapes + tag bindings) for the engine-room SVG; bindings to tags missing from the project are dropped and listed in missingTags.");

        var writes = app.MapGroup("/api").WithTags("Monitoring").RequireAuthorization();
        writes.MapPost("/monitoring/alarms/{id:int}/ack",
                async (int id, AlarmManager alarms, ClaimsPrincipal principal, CancellationToken ct) =>
                    TypedResults.Ok(await alarms.AcknowledgeAsync(id, principal.ToUser().Name, ct)))
            .WithSummary("Acknowledge an alarm (session required). 404 not_found, 409 already_acknowledged / alarm_closed.");

        app.MapHub<MonitoringHub>(HubPath).RequireAuthorization();
    }
}

/// <summary>Serves <c>seed/mimic.json</c> for a project, checked against the project's real tags.</summary>
public sealed class MimicService(IBosunDbContext db, IConfiguration configuration)
{
    public const string FileName = "mimic.json";

    public async Task<JsonNode> GetAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var root = configuration["Bosun:RepoRoot"] ?? Directory.GetCurrentDirectory();
        var path = configuration["Monitoring:MimicPath"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured, root)
            : Path.Combine(root, "seed", FileName);
        if (!File.Exists(path))
        {
            throw new AppException(ErrorKind.NotFound, "mimic_not_found", "No mimic layout is configured (seed/mimic.json is missing).");
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject()
            ?? throw new AppException(ErrorKind.Unprocessable, "mimic_invalid", "mimic.json is empty.");
        var bindings = document["bindings"] as JsonArray ?? [];

        var wanted = bindings.Select(b => b?["tag"]?.GetValue<string>()).Where(t => t is not null).Select(t => t!).Distinct().ToList();
        var found = await db.Tags.AsNoTracking()
            .Where(t => t.ProjectId == projectId && wanted.Contains(t.TagName))
            .Select(t => new { t.TagName, t.Unit, t.RangeLow, t.RangeHigh, t.Description, t.SafetyClass })
            .ToListAsync(ct);
        var known = found.ToDictionary(t => t.TagName, StringComparer.Ordinal);

        var missing = new JsonArray();
        foreach (var binding in bindings.ToList())
        {
            var tag = binding?["tag"]?.GetValue<string>();
            if (tag is not null && !known.ContainsKey(tag))
            {
                bindings.Remove(binding);
                missing.Add(tag);
            }
        }

        var info = new JsonObject();
        foreach (var t in found)
        {
            info[t.TagName] = new JsonObject
            {
                ["unit"] = t.Unit,
                ["rangeLow"] = t.RangeLow,
                ["rangeHigh"] = t.RangeHigh,
                ["description"] = t.Description,
                ["safetyClass"] = t.SafetyClass == Domain.SafetyClass.None ? "NONE" : t.SafetyClass.ToString(),
            };
        }

        document["projectId"] = projectId;
        document["tags"] = info;
        document["missingTags"] = missing;
        return document;
    }
}
