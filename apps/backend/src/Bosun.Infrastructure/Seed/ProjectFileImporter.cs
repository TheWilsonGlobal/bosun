using Bosun.Domain;
using Bosun.Infrastructure.Persistence;

namespace Bosun.Infrastructure.Seed;

public static class ProjectFileImporter
{
    /// <summary>Loads a <c>.bosun.json</c> into an empty database (bulk insert). Returns the number of tags.</summary>
    public static async Task<int> ImportIntoEmptyAsync(BosunDbContext db, BosunProjectFile file, CancellationToken ct = default) =>
        await DemoSeeder.InsertGraphAsync(db, ProjectFileMapper.ToGraph(file), ct);

    public static BosunProjectFile FromGenerator(GeneratorOptions? options = null)
    {
        var vessel = VesselGenerator.Generate(options);
        return ProjectFileMapper.ToFile(vessel, vessel.Projects.Single());
    }
}
