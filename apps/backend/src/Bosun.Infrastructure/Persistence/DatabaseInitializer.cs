using Bosun.Application.Rag;
using Bosun.Domain;
using Bosun.Infrastructure.Persistence.Sqlite;
using Bosun.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>Creates the data directory if needed and applies pending migrations (includes the FTS5 table + triggers).</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var settings = DatabaseSettings.From(services.GetRequiredService<IConfiguration>());
        SqliteFiles.EnsureDirectory(settings.ConnectionString);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        await db.Database.MigrateAsync(ct);
    }

    public static async Task<int> SeedAsync(IServiceProvider services, GeneratorOptions? options = null, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        return await DemoSeeder.SeedIfEmptyAsync(db, options, ct);
    }

    /// <summary>Loads a .bosun.json into the (empty) database.</summary>
    public static async Task<int> ImportAsync(IServiceProvider services, BosunProjectFile file, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        return await ProjectFileImporter.ImportIntoEmptyAsync(db, file, ct);
    }

    /// <summary>
    /// Indexes the project documents for the first project WITHOUT calling the embedding provider: only when every chunk is already in
    /// the embedding cache file (a reseed after a real <c>kb:index</c> is free and instant). Otherwise nothing happens and the knowledge
    /// base starts empty ("not indexed"); <c>pnpm run kb:index</c> fills it. Never throws: the seed must work without Gemini.
    /// </summary>
    public static async Task<IndexReport?> IndexKnowledgeFromCacheAsync(IServiceProvider services, CancellationToken ct = default)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
            var projectId = await db.Projects.OrderBy(p => p.Id).Select(p => p.Id).FirstOrDefaultAsync(ct);
            return projectId == 0 ? null : await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(projectId, cacheOnly: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Knowledge base not indexed ({ex.Message}). Run `pnpm run kb:index` once Gemini is configured.");
            return null;
        }
    }

    /// <summary>Deletes the database file (+ -wal / -shm), migrates and reseeds. Backs <c>pnpm run db:reset</c>.</summary>
    public static async Task<int> ResetAsync(IServiceProvider services, GeneratorOptions? options = null, CancellationToken ct = default)
    {
        var settings = DatabaseSettings.From(services.GetRequiredService<IConfiguration>());
        SqliteFiles.Delete(settings.ConnectionString);
        await MigrateAsync(services, ct);
        return await SeedAsync(services, options, ct);
    }
}
