using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Bosun.Application;

/// <summary>
/// The persistence surface the use cases need. Implemented by the provider-neutral BosunDbContext in
/// Bosun.Infrastructure; nothing here knows whether SQLite or PostgreSQL is underneath.
/// </summary>
public interface IBosunDbContext
{
    DbSet<Vessel> Vessels { get; }
    DbSet<Project> Projects { get; }
    DbSet<Controller> Controllers { get; }
    DbSet<IoModule> IoModules { get; }
    DbSet<Channel> Channels { get; }
    DbSet<AlarmGroup> AlarmGroups { get; }
    DbSet<Tag> Tags { get; }
    DbSet<AlarmLimit> AlarmLimits { get; }
    DbSet<ChangeSet> ChangeSets { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<LlmCall> LlmCalls { get; }
    DbSet<AgentRun> AgentRuns { get; }
    DbSet<AgentStep> AgentSteps { get; }
    DbSet<EvalRun> EvalRuns { get; }
    DbSet<EvalResult> EvalResults { get; }
    DbSet<SourceDocument> SourceDocuments { get; }
    DbSet<Extraction> Extractions { get; }
    DbSet<ExtractedRow> ExtractedRows { get; }
    DbSet<KnowledgeChunk> KnowledgeChunks { get; }
    DbSet<Alarm> Alarms { get; }
    DbSet<AlarmEvent> AlarmEvents { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Fuzzy / substring tag search. SQLite: FTS5 trigram. PostgreSQL (Phase 8): pg_trgm.</summary>
public interface ISearchTags
{
    /// <summary>Narrows <paramref name="tags"/> to those whose name or description contains <paramref name="text"/> (case-insensitive).</summary>
    IQueryable<Tag> Apply(IQueryable<Tag> tags, string text);

    /// <summary>Ranked search for the agent's <c>search_tags</c> tool.</summary>
    Task<IReadOnlyList<TagHit>> SearchAsync(int projectId, string query, string? system, int limit, CancellationToken cancellationToken);
}

public sealed record TagHit(int Id, string TagName, string? Description, string System);

/// <summary>The signed-in human behind a request.</summary>
public sealed record UserContext(string Name, IReadOnlySet<string> Roles)
{
    public const string ApproverRole = "approver";

    public bool IsApprover => Roles.Contains(ApproverRole);
}

public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unprocessable,

    /// <summary>The request body is bigger than the endpoint accepts (HTTP 413).</summary>
    PayloadTooLarge,

    /// <summary>The body is not of the kind the endpoint accepts, for example a file that is not a PDF (HTTP 415).</summary>
    UnsupportedMedia,
}

/// <summary>
/// An expected, user-facing failure. <c>Code</c> is the stable machine-readable id the frontend maps; the API turns
/// this into an RFC 7807 problem response.
/// </summary>
public sealed class AppException(ErrorKind kind, string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();

    public static AppException NotFound(string what, object id) =>
        new(ErrorKind.NotFound, "not_found", $"{what} {id} was not found.");

    public static AppException InvalidState(string message, ChangeSetStatus status) =>
        new(ErrorKind.Conflict, "invalid_state", message, new Dictionary<string, object?> { ["currentStatus"] = status });

    public static AppException Invalid(string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(ErrorKind.Validation, "invalid_request", message, extensions);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
