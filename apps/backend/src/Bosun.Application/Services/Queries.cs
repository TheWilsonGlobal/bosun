using System.Linq.Expressions;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Application;

/// <summary>Read side for the tag grid and the reference lists. All queries are server-side (paging, sorting, filtering).</summary>
public sealed class TagQueries(IBosunDbContext db, ISearchTags search)
{
    public const int MaxPageSize = 1000;

    /// <summary>With <see cref="MaxPageSize"/> this keeps (page - 1) * pageSize below int.MaxValue (999,999,000).</summary>
    public const int MaxPage = 1_000_000;

    public static readonly IReadOnlyCollection<string> SortFields =
        ["id", "tagName", "description", "system", "equipment", "signalType", "unit", "rangeLow", "rangeHigh", "safetyClass", "alarmGroup", "version"];

    public async Task<PagedResult<TagDto>> ListAsync(int projectId, TagQuery query, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);

        if (query.Page is < 1 or > MaxPage)
        {
            throw AppException.Invalid($"page must be between 1 and {MaxPage}.");
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            throw AppException.Invalid($"pageSize must be between 1 and {MaxPageSize}.");
        }

        var tags = Filter(db.Tags.AsNoTracking().Where(t => t.ProjectId == projectId), query);
        var total = await tags.CountAsync(ct);
        var page = await Sort(tags, query.Sort, query.Descending)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(ToDto)
            .ToListAsync(ct);

        return new PagedResult<TagDto>(page, total, query.Page, query.PageSize);
    }

    public async Task<TagDto> GetAsync(int projectId, int tagId, CancellationToken ct) =>
        await db.Tags.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Id == tagId)
            .Select(ToDto)
            .FirstOrDefaultAsync(ct)
        ?? throw AppException.NotFound("Tag", tagId);

    /// <summary>
    /// Tags whose name or description contains ALL the given terms (each term a case-insensitive substring through
    /// <see cref="ISearchTags"/>), best-first by name. Returns the page plus the total match count. Used by the agent's search_tags tool.
    /// </summary>
    public async Task<(IReadOnlyList<TagDto> Items, int Total)> SearchAsync(
        int projectId, IReadOnlyList<string> terms, string? system, int limit, CancellationToken ct)
    {
        var tags = db.Tags.AsNoTracking().Where(t => t.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(system))
        {
            var normalised = system.Trim().ToUpperInvariant();
            tags = tags.Where(t => t.System == normalised);
        }

        foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            tags = search.Apply(tags, term.Trim());
        }

        var total = await tags.CountAsync(ct);
        var items = await tags.OrderBy(t => t.TagName).Take(Math.Clamp(limit, 1, MaxPageSize)).Select(ToDto).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Tags by exact name (unknown names are simply absent from the result).</summary>
    public async Task<IReadOnlyList<TagDto>> GetByNamesAsync(int projectId, IReadOnlyList<string> tagNames, CancellationToken ct)
    {
        var names = tagNames.Distinct(StringComparer.Ordinal).ToList();
        return await db.Tags.AsNoTracking()
            .Where(t => t.ProjectId == projectId && names.Contains(t.TagName))
            .OrderBy(t => t.TagName)
            .Select(ToDto)
            .ToListAsync(ct);
    }

    public async Task<TagFacetsDto> FacetsAsync(int projectId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var tags = db.Tags.AsNoTracking().Where(t => t.ProjectId == projectId);
        return new TagFacetsDto(
            await tags.Select(t => t.System).Distinct().OrderBy(x => x).ToListAsync(ct),
            await tags.Where(t => t.Unit != null).Select(t => t.Unit!).Distinct().OrderBy(x => x).ToListAsync(ct),
            await tags.Select(t => t.SignalType).Distinct().OrderBy(x => x).ToListAsync(ct),
            await tags.Select(t => t.SafetyClass).Distinct().OrderBy(x => x).ToListAsync(ct),
            await db.AlarmGroups.AsNoTracking().Where(g => g.ProjectId == projectId).OrderBy(g => g.Name).Select(g => g.Name).ToListAsync(ct));
    }

    private async Task EnsureProjectAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }
    }

    private IQueryable<Tag> Filter(IQueryable<Tag> tags, TagQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            tags = search.Apply(tags, q.Text.Trim());
        }

        if (q.Systems.Count > 0)
        {
            var systems = q.Systems;
            tags = tags.Where(t => systems.Contains(t.System));
        }

        if (q.Units.Count > 0)
        {
            var units = q.Units;
            tags = tags.Where(t => t.Unit != null && units.Contains(t.Unit));
        }

        if (q.SignalTypes.Count > 0)
        {
            var types = q.SignalTypes;
            tags = tags.Where(t => types.Contains(t.SignalType));
        }

        if (q.SafetyClasses.Count > 0)
        {
            var classes = q.SafetyClasses;
            tags = tags.Where(t => classes.Contains(t.SafetyClass));
        }

        if (q.AlarmGroups.Count > 0)
        {
            var names = q.AlarmGroups.Where(n => !n.Equals("none", StringComparison.OrdinalIgnoreCase)).ToList();
            var includeNone = names.Count != q.AlarmGroups.Count;
            tags = tags.Where(t => (t.AlarmGroup != null && names.Contains(t.AlarmGroup.Name)) || (includeNone && t.AlarmGroupId == null));
        }

        if (q.HasAlarm is { } hasAlarm)
        {
            tags = hasAlarm ? tags.Where(t => t.AlarmLimits.Any()) : tags.Where(t => !t.AlarmLimits.Any());
        }

        if (q.Bound is { } bound)
        {
            tags = bound ? tags.Where(t => t.ChannelId != null) : tags.Where(t => t.ChannelId == null);
        }

        if (q.ControllerId is { } controllerId)
        {
            tags = tags.Where(t => t.Channel!.IoModule!.ControllerId == controllerId);
        }

        return tags;
    }

    private static IQueryable<Tag> Sort(IQueryable<Tag> tags, string field, bool desc)
    {
        IOrderedQueryable<Tag> By<TKey>(Expression<Func<Tag, TKey>> key) => desc ? tags.OrderByDescending(key) : tags.OrderBy(key);

        var ordered = field switch
        {
            "id" => By(t => t.Id),
            "tagName" => By(t => t.TagName),
            "description" => By(t => t.Description),
            "system" => By(t => t.System),
            "equipment" => By(t => t.Equipment),
            "signalType" => By(t => t.SignalType),
            "unit" => By(t => t.Unit),
            "rangeLow" => By(t => t.RangeLow),
            "rangeHigh" => By(t => t.RangeHigh),
            "safetyClass" => By(t => t.SafetyClass),
            "alarmGroup" => By(t => t.AlarmGroup!.Name),
            "version" => By(t => t.Version),
            _ => throw AppException.Invalid($"Cannot sort by '{field}'. Allowed: {string.Join(", ", SortFields)}."),
        };

        return ordered.ThenBy(t => t.Id);
    }

    private static readonly Expression<Func<Tag, TagDto>> ToDto = t => new TagDto(
        t.Id,
        t.ProjectId,
        t.TagName,
        t.Description,
        t.System,
        t.Equipment,
        t.SignalType,
        t.Unit,
        t.RangeLow,
        t.RangeHigh,
        t.SafetyClass,
        t.AlarmGroupId,
        t.AlarmGroup == null ? null : t.AlarmGroup.Name,
        t.ChannelId,
        t.Channel == null
            ? null
            : t.Channel.IoModule!.Controller!.Name + "/" + t.Channel.IoModule.Name + "/" + t.Channel.Number.ToString(),
        t.AlarmLimits.OrderBy(l => l.Level == AlarmLevel.LL ? 0 : l.Level == AlarmLevel.L ? 1 : l.Level == AlarmLevel.H ? 2 : 3).Select(l => new AlarmLimitDto(l.Level, l.Value, l.DelayS, l.Hysteresis)).ToList(),
        t.AlarmLimits.Any(),
        t.Version);
}

public sealed class ProjectQueries(IBosunDbContext db)
{
    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new ProjectDto(
                p.Id,
                p.VesselId,
                p.Name,
                p.Vessel!.Name,
                p.Vessel.ImoNumber,
                p.Description,
                db.Tags.Count(t => t.ProjectId == p.Id),
                p.Version))
            .ToListAsync(ct);

    public async Task<ProjectDto> GetProjectAsync(int projectId, CancellationToken ct) =>
        (await ListProjectsAsync(ct)).FirstOrDefault(p => p.Id == projectId) ?? throw AppException.NotFound("Project", projectId);

    public async Task<IReadOnlyList<AlarmGroupDto>> ListAlarmGroupsAsync(int projectId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        return await db.AlarmGroups.AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .OrderBy(g => g.Name)
            .Select(g => new AlarmGroupDto(g.Id, g.Name, g.Description, db.Tags.Count(t => t.AlarmGroupId == g.Id)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ControllerDto>> ListControllersAsync(int projectId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var rows = await db.Controllers.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Model,
                c.IpAddress,
                Modules = c.Modules.OrderBy(m => m.Slot).Select(m => new IoModuleDto(
                    m.Id,
                    m.Name,
                    m.Slot,
                    m.SignalType,
                    m.Channels.Count,
                    db.Tags.Count(t => t.Channel != null && t.Channel.IoModuleId == m.Id),
                    null)).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new ControllerDto(r.Id, r.Name, r.Model, r.IpAddress, r.Modules)).ToList();
    }

    /// <summary>Channels of one module with the tag bound to each (used to pick a free channel for BindChannel).</summary>
    public async Task<IReadOnlyList<ChannelDto>> ListChannelsAsync(int projectId, int moduleId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        return await db.Channels.AsNoTracking()
            .Where(c => c.IoModuleId == moduleId && c.IoModule!.Controller!.ProjectId == projectId)
            .OrderBy(c => c.Number)
            .Select(c => new ChannelDto(c.Id, c.Number, c.SignalType, db.Tags.Where(t => t.ChannelId == c.Id).Select(t => t.TagName).FirstOrDefault()))
            .ToListAsync(ct);
    }

    private async Task EnsureProjectAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }
    }
}
