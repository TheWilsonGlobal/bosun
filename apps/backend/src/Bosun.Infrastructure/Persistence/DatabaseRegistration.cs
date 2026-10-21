using Bosun.Application;
using Bosun.Application.Rag;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Infrastructure.Persistence;

/// <summary>Resolved database settings (from the root .env via <c>AddBosunEnv</c>).</summary>
public sealed record DatabaseSettings(string Provider, string ConnectionString)
{
    public const string SqliteProvider = "sqlite";

    public static DatabaseSettings From(IConfiguration configuration)
    {
        var provider = (configuration["Database:Provider"] ?? SqliteProvider).Trim().ToLowerInvariant();
        if (provider != SqliteProvider)
        {
            throw new InvalidOperationException(
                $"DB_PROVIDER='{provider}' is not supported. Only 'sqlite' is implemented; PostgreSQL (Npgsql + pgvector) arrives in Phase 8.");
        }

        var connection = configuration["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("DB_CONNECTION is empty. Example: Data Source=data/bosun.db");
        }

        return new DatabaseSettings(provider, connection);
    }
}

public static class DatabaseRegistration
{
    /// <summary>
    /// Registers the provider-neutral <see cref="BosunDbContext"/>. Settings are read lazily (when the first context is
    /// created) so test hosts can override configuration after the builder ran.
    /// </summary>
    public static IServiceCollection AddBosunDatabase(this IServiceCollection services)
    {
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContext<BosunDbContext>((sp, options) =>
        {
            var settings = DatabaseSettings.From(sp.GetRequiredService<IConfiguration>());
            options.UseSqlite(settings.ConnectionString, sqlite => sqlite.MigrationsAssembly("Bosun.Migrations.Sqlite"));
            options.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        services.AddScoped<IBosunDbContext>(sp => sp.GetRequiredService<BosunDbContext>());
        services.AddScoped<ISearchTags, SqliteSearchTags>();
        services.AddScoped<IVectorStore, SqliteVectorStore>();

        // "Rag" options: paths default to seed/docs and data/rag-embedding-cache.json under the repo root (Bosun:RepoRoot).
        services.AddOptions<RagOptions>().BindConfiguration("Rag").PostConfigure<IConfiguration>((rag, configuration) =>
        {
            var root = configuration["Bosun:RepoRoot"] ?? RepoRoot.Resolve();
            rag.DocsPath = string.IsNullOrWhiteSpace(rag.DocsPath) ? Path.Combine(root, "seed", "docs") : Path.GetFullPath(rag.DocsPath, root);
            rag.CachePath = rag.CachePath is null ? Path.Combine(root, "data", "rag-embedding-cache.json") : string.IsNullOrWhiteSpace(rag.CachePath) ? null : Path.GetFullPath(rag.CachePath, root);
        });
        return services;
    }
}
