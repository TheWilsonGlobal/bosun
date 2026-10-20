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

    /// <summary>Deletes the database file (+ -wal / -shm), migrates and reseeds. Backs <c>pnpm run db:reset</c>.</summary>
    public static async Task<int> ResetAsync(IServiceProvider services, GeneratorOptions? options = null, CancellationToken ct = default)
    {
        var settings = DatabaseSettings.From(services.GetRequiredService<IConfiguration>());
        SqliteFiles.Delete(settings.ConnectionString);
        await MigrateAsync(services, ct);
        return await SeedAsync(services, options, ct);
    }
}
