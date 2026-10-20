using Bosun.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Application;

/// <summary>Exports a project as the portable <c>.bosun.json</c> document (see <see cref="BosunProjectFile"/>).</summary>
public sealed class ProjectFileService(IBosunDbContext db)
{
    public async Task<BosunProjectFile> ExportAsync(int projectId, CancellationToken ct)
    {
        // Separate queries on one tracking context: EF fixes up the navigation properties (tag -> group/channel/limits)
        // without the cartesian explosion a single mega-Include would cause for 5,000 tags.
        var project = await db.Projects.Include(p => p.Vessel).FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw AppException.NotFound("Project", projectId);

        await db.AlarmGroups.Where(g => g.ProjectId == projectId).OrderBy(g => g.Id).LoadAsync(ct);
        await db.Controllers.Where(c => c.ProjectId == projectId)
            .Include(c => c.Modules).ThenInclude(m => m.Channels)
            .OrderBy(c => c.Id).LoadAsync(ct);
        await db.Tags.Where(t => t.ProjectId == projectId).Include(t => t.AlarmLimits).OrderBy(t => t.Id).LoadAsync(ct);

        try
        {
            return ProjectFileMapper.ToFile(project.Vessel!, project);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
