using System.Security.Claims;
using System.Text.Json;
using Bosun.Api.Auth;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Api;

public sealed record TagListParams(
    string? Filter,
    string? System,
    string? Unit,
    string? SignalType,
    string? SafetyClass,
    string? AlarmGroup,
    bool? HasAlarm,
    bool? Bound,
    int? ControllerId,
    string? Sort,
    string? Order,
    int? Page,
    int? PageSize);

public sealed record CreateChangeSetBody(string? Title, string? Source, List<JsonElement>? Ops);

public sealed record AddOpsBody(List<JsonElement>? Ops);

public sealed record RejectBody(string? Reason);

/// <summary><c>Environment</c> is only reported in Development (the name of the environment is reconnaissance for an attacker).</summary>
public sealed record HealthDto(
    string Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Environment,
    string DatabaseProvider,
    bool DatabaseOk,
    string Version);

public static class ApiEndpoints
{
    public static void MapBosunApi(this WebApplication app, bool readsRequireLogin)
    {
        app.MapHealth();
        app.MapAuth();

        // Reads are open in Development (the SPAs can render without a login) and require a session everywhere else.
        var reads = app.MapGroup("/api").WithTags("Configurator");
        if (readsRequireLogin)
        {
            reads.RequireAuthorization();
        }

        reads.MapGet("/projects", async (ProjectQueries q, CancellationToken ct) => TypedResults.Ok(await q.ListProjectsAsync(ct)));
        reads.MapGet("/projects/{projectId:int}", async (int projectId, ProjectQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.GetProjectAsync(projectId, ct)));

        reads.MapGet("/projects/{projectId:int}/tags", async (int projectId, [AsParameters] TagListParams p, TagQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.ListAsync(projectId, ToQuery(p), ct)))
            .WithSummary("Server-side paged/sorted/filtered tag grid: { items, total, page, pageSize }.");
        reads.MapGet("/projects/{projectId:int}/tags/{tagId:int}", async (int projectId, int tagId, TagQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.GetAsync(projectId, tagId, ct)));
        reads.MapGet("/projects/{projectId:int}/tag-facets", async (int projectId, TagQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.FacetsAsync(projectId, ct)))
            .WithSummary("Distinct values for the grid's column filters.");

        reads.MapGet("/projects/{projectId:int}/export", async (int projectId, ProjectFileService files, CancellationToken ct) =>
        {
            var file = await files.ExportAsync(projectId, ct);
            var name = new string(file.Project.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            return TypedResults.File(
                System.Text.Encoding.UTF8.GetBytes(ProjectFileSerializer.Serialize(file)),
                "application/json",
                $"{name}.bosun.json");
        }).WithSummary("Download the project as a .bosun.json file.");

        reads.MapGet("/projects/{projectId:int}/controllers", async (int projectId, ProjectQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.ListControllersAsync(projectId, ct)));
        reads.MapGet("/projects/{projectId:int}/modules/{moduleId:int}/channels", async (int projectId, int moduleId, ProjectQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.ListChannelsAsync(projectId, moduleId, ct)));
        reads.MapGet("/projects/{projectId:int}/alarm-groups", async (int projectId, ProjectQueries q, CancellationToken ct) =>
            TypedResults.Ok(await q.ListAlarmGroupsAsync(projectId, ct)));

        reads.MapGet("/projects/{projectId:int}/change-sets",
            async (int projectId, string? status, int? page, int? pageSize, ChangeSetService svc, CancellationToken ct) =>
            {
                ChangeSetStatus? parsed = null;
                if (status is not null)
                {
                    parsed = Enum.TryParse<ChangeSetStatus>(status, true, out var s) && Enum.IsDefined(s)
                        ? s
                        : throw AppException.Invalid($"Unknown status '{status}'. Allowed: draft, validated, approved, applied, rejected.");
                }

                return TypedResults.Ok(await svc.ListAsync(projectId, parsed, page ?? 1, pageSize ?? 50, ct));
            });
        reads.MapGet("/change-sets/{id:int}", async (int id, ChangeSetService svc, CancellationToken ct) => TypedResults.Ok(await svc.GetAsync(id, ct)));
        reads.MapGet("/change-sets/{id:int}/diff", async (int id, ChangeSetService svc, CancellationToken ct) => TypedResults.Ok(await svc.DiffAsync(id, ct)))
            .WithSummary("Per-op old -> new rows plus the current rule violations.");
        reads.MapGet("/projects/{projectId:int}/audit-entries",
            async (int projectId, int? changeSetId, string? tagName, int? take, ChangeSetService svc, CancellationToken ct) =>
                TypedResults.Ok(await svc.AuditAsync(projectId, changeSetId, tagName, take ?? 100, ct)));

        // Everything that changes state needs a signed-in human.
        var writes = app.MapGroup("/api").WithTags("Change sets").RequireAuthorization();

        writes.MapPost("/projects/{projectId:int}/change-sets",
            async (int projectId, CreateChangeSetBody body, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
            {
                var source = ParseSource(body.Source);
                var ops = ParseOps(body.Ops);
                var created = await svc.CreateAsync(projectId, new CreateChangeSetRequest(body.Title ?? "", source, ops), principal.ToUser(), ct);
                return TypedResults.Created($"/api/change-sets/{created.Id}", created);
            }).WithSummary("Create a draft change set (optionally with ops). Inline grid edits use this same path.");

        writes.MapPost("/change-sets/{id:int}/ops",
            async (int id, AddOpsBody body, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                TypedResults.Ok(await svc.AddOpsAsync(id, ParseOps(body.Ops), principal.ToUser(), ct)));

        writes.MapPost("/change-sets/{id:int}/validate",
            async (int id, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                TypedResults.Ok(await svc.ValidateAsync(id, principal.ToUser(), ct)));

        writes.MapPost("/change-sets/{id:int}/approve",
            async (int id, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                TypedResults.Ok(await svc.ApproveAsync(id, principal.ToUser(), ct)));

        writes.MapPost("/change-sets/{id:int}/apply",
            async (int id, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                TypedResults.Ok(await svc.ApplyAsync(id, principal.ToUser(), ct)));

        writes.MapPost("/change-sets/{id:int}/reject",
            async (int id, RejectBody? body, ChangeSetService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                TypedResults.Ok(await svc.RejectAsync(id, body?.Reason, principal.ToUser(), ct)));
    }

    private static void MapHealth(this WebApplication app)
    {
        async Task<Results<Ok<HealthDto>, JsonHttpResult<HealthDto>>> Health(BosunDbContext db, IHostEnvironment env, IConfiguration config, CancellationToken ct)
        {
            var ok = await db.Database.CanConnectAsync(ct);
            var dto = new HealthDto(ok ? "ok" : "degraded", env.IsDevelopment() ? env.EnvironmentName : null, config["Database:Provider"] ?? "sqlite", ok, "0.1.0");
            return ok ? TypedResults.Ok(dto) : TypedResults.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        app.MapGet("/health", Health).AllowAnonymous().WithTags("Health");
        app.MapGet("/api/health", Health).AllowAnonymous().WithTags("Health"); // reachable through the SPA's /api proxy
    }

    // ---------- request parsing ----------

    private static ChangeSetSource ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return ChangeSetSource.Manual;
        }

        if (source.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            return ChangeSetSource.Manual;
        }

        if (source.Equals("extraction", StringComparison.OrdinalIgnoreCase))
        {
            return ChangeSetSource.Extraction;
        }

        // 'agent' provenance (model, prompt version, run id) is set server-side by the agent runner, never by a client.
        throw AppException.Invalid($"source must be 'manual' or 'extraction' (got '{source}').");
    }

    private static List<OpInput> ParseOps(List<JsonElement>? elements)
    {
        var inputs = new List<OpInput>(elements?.Count ?? 0);
        for (var i = 0; i < (elements?.Count ?? 0); i++)
        {
            var element = elements![i];
            ChangeOp? op;
            try
            {
                op = element.ValueKind == JsonValueKind.Object ? element.Deserialize<ChangeOp>(BosunJson.Options) : null;
            }
            catch (JsonException ex)
            {
                throw AppException.Invalid($"ops[{i}] is not a valid operation: {ex.Message}", new Dictionary<string, object?> { ["index"] = i });
            }

            if (op is null || string.IsNullOrWhiteSpace(op.TagName) || op.TagName.Length > 100)
            {
                throw AppException.Invalid($"ops[{i}] needs a 'type' and a tagName of 1-100 characters.", new Dictionary<string, object?> { ["index"] = i });
            }

            // An omitted "old" means "take the current value"; an explicit one (even null) is enforced at apply time.
            inputs.Add(new OpInput(op, element.TryGetProperty("old", out _)));
        }

        return inputs;
    }

    private static TagQuery ToQuery(TagListParams p)
    {
        static List<string> Split(string? value) =>
            string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        static List<T> ParseEnums<T>(string? value, string name)
            where T : struct, Enum =>
            Split(value).Select(v => Enum.TryParse<T>(v, true, out var e) && Enum.IsDefined(e)
                ? e
                : throw AppException.Invalid($"Unknown {name} '{v}'.")).ToList();

        var sort = (p.Sort ?? "tagName").Trim();
        var descending = string.Equals(p.Order, "desc", StringComparison.OrdinalIgnoreCase);
        if (p.Order is not null && !descending && !string.Equals(p.Order, "asc", StringComparison.OrdinalIgnoreCase))
        {
            throw AppException.Invalid("order must be 'asc' or 'desc'.");
        }

        if (sort.StartsWith('-'))
        {
            sort = sort[1..];
            descending = true;
        }

        return new TagQuery
        {
            Text = p.Filter,
            Systems = Split(p.System),
            Units = Split(p.Unit),
            SignalTypes = ParseEnums<SignalType>(p.SignalType, "signalType"),
            SafetyClasses = ParseEnums<SafetyClass>(p.SafetyClass, "safetyClass"),
            AlarmGroups = Split(p.AlarmGroup),
            HasAlarm = p.HasAlarm,
            Bound = p.Bound,
            ControllerId = p.ControllerId,
            Sort = sort,
            Descending = descending,
            Page = p.Page ?? 1,
            PageSize = p.PageSize ?? 50,
        };
    }
}
