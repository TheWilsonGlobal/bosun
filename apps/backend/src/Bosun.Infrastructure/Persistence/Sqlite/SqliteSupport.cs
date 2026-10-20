using System.Data.Common;
using Bosun.Application;
using Bosun.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bosun.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Applied to every SQLite connection: WAL + busy_timeout (plan s5.1 "single writer" row), so a background writer
/// and an apply running together wait instead of throwing "database is locked".
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Substring tag search on the FTS5 <c>tag_fts</c> table (trigram tokenizer, created by the first migration and kept
/// in sync by triggers). Queries shorter than three characters cannot form a trigram and fall back to LIKE.
/// </summary>
public sealed class SqliteSearchTags(BosunDbContext db) : ISearchTags
{
    public const int MinTrigramLength = 3;

    public IQueryable<Tag> Apply(IQueryable<Tag> tags, string text)
    {
        if (text.Length < MinTrigramLength)
        {
            var like = "%" + EscapeLike(text) + "%";
            return tags.Where(t => EF.Functions.Like(t.TagName, like, "\\") || (t.Description != null && EF.Functions.Like(t.Description, like, "\\")));
        }

        // Quoted => a single FTS5 phrase, so characters such as '-' are data, not query operators.
        var match = "\"" + text.Replace("\"", "\"\"") + "\"";
        var ids = db.Database.SqlQuery<int>($"SELECT rowid AS Value FROM tag_fts WHERE tag_fts MATCH {match}");
        return tags.Where(t => ids.Contains(t.Id));
    }

    public async Task<IReadOnlyList<TagHit>> SearchAsync(int projectId, string query, string? system, int limit, CancellationToken cancellationToken)
    {
        var tags = db.Tags.AsNoTracking().Where(t => t.ProjectId == projectId);
        if (system is not null)
        {
            tags = tags.Where(t => t.System == system);
        }

        return await Apply(tags, query.Trim())
            .OrderBy(t => t.TagName)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(t => new TagHit(t.Id, t.TagName, t.Description, t.System))
            .ToListAsync(cancellationToken);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public static class SqliteFiles
{
    /// <summary>Deletes the database file and its -wal / -shm companions (after releasing pooled connections).</summary>
    public static void Delete(string connectionString)
    {
        SqliteConnection.ClearAllPools();
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    public static void EnsureDirectory(string connectionString)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrEmpty(path) && path != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir)
        {
            Directory.CreateDirectory(dir);
        }
    }
}
