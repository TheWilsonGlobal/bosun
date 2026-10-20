using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Bosun.Migrations.Sqlite;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>. It always targets SQLite (this assembly only holds SQLite migrations;
/// Phase 8 adds Bosun.Migrations.Postgres with its own factory) and resolves the database from the root .env, so
/// <c>dotnet ef</c> sees the same data/bosun.db as <c>dotnet run</c> whatever the cwd is.
/// </summary>
public sealed class BosunDbContextFactory : IDesignTimeDbContextFactory<BosunDbContext>
{
    public BosunDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddBosunEnv().Build();
        var connection = configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException("Database:ConnectionString is not configured.");

        var options = new DbContextOptionsBuilder<BosunDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(BosunDbContextFactory).Assembly.GetName().Name))
            .Options;
        return new BosunDbContext(options);
    }
}
