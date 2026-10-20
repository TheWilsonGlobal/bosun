using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Infrastructure.Seed;

/// <summary>
/// A migrated, seeded SQLite file that can be COPIED (plan s9): evals and tests give every run an identical database by
/// copying <c>bosun.seed.db</c>, which takes milliseconds, instead of re-seeding 5,000 tags each time.
/// </summary>
public static class SeedDatabase
{
    /// <summary>Creates (or replaces) a migrated database with the demo vessel at <paramref name="path"/>. The result is one plain file.</summary>
    public static async Task CreateAsync(string path, GeneratorOptions? options = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var connection = $"Data Source={full}";
        SqliteFiles.Delete(connection);

        var dbOptions = new DbContextOptionsBuilder<BosunDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Bosun.Migrations.Sqlite"))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        await using (var db = new BosunDbContext(dbOptions))
        {
            await db.Database.MigrateAsync(ct);
            await DemoSeeder.SeedIfEmptyAsync(db, options, ct);
        }

        // Fold the WAL into the main file and leave the rollback journal mode, so a plain file copy is a complete database.
        SqliteConnection.ClearAllPools();
        await using (var raw = new SqliteConnection(connection))
        {
            await raw.OpenAsync(ct);
            await using var command = raw.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            await command.ExecuteNonQueryAsync(ct);
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>Copies the seed file to <c>{directory}/bosun.db</c> and returns that path.</summary>
    public static string CopyTo(string seedFile, string directory)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "bosun.db");
        File.Copy(seedFile, target, overwrite: true);
        return target;
    }
}
